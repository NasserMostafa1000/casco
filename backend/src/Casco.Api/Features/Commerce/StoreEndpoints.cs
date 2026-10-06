using System.Globalization;
using System.Text;
using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.Backend;
using Casco.Api.Features.Notifications;
using Casco.Api.Features.Projects;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.Commerce;

public record OrderItemRequest(Guid ProductId, int Quantity);
public record PlaceOrderRequest(string CustomerName, string Phone, string? Email, string? Address, string? City, string? Notes,
    string? PaymentMethod, List<OrderItemRequest> Items, string? _hp = null);
public record ProductRequest(string Name, string? Description, decimal Price, decimal? ComparePrice, string? Category,
    List<string>? Images, int? Stock, bool IsActive = true, int SortOrder = 0);
public record StatusRequest(string Status);

public record OrderLine(Guid ProductId, string Name, decimal Price, int Quantity, string? Image);

/// <summary>
/// Online store without online payment: cash on delivery, WhatsApp, bank transfer or pickup.
/// Card payments need a licensed business and a developer, so they are handled by Casco support, not here.
/// </summary>
public static class StoreEndpoints
{
    public const int MaxProducts = 2000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string PaymentLabel(string method) => method switch
    {
        PaymentMethods.CashOnDelivery => "الدفع عند الاستلام",
        PaymentMethods.WhatsApp => "الاتفاق على الدفع عبر واتساب",
        PaymentMethods.BankTransfer => "تحويل بنكي",
        PaymentMethods.Pickup => "الدفع والاستلام من المحل",
        _ => method
    };

    public static void MapStoreEndpoints(this IEndpointRouteBuilder app)
    {
        var s = app.MapGroup("/api/s/{siteKey}/store").RequireCors("sites").RequireRateLimiting("site");

        s.MapGet("/config", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var st = project.Settings;
            return Results.Ok(new
            {
                st.Currency, st.ShippingFee, st.FreeShippingOver,
                paymentMethods = st.PaymentMethods.Select(m => new { id = m, label = PaymentLabel(m) }),
                bankDetails = st.PaymentMethods.Contains(PaymentMethods.BankTransfer) ? st.BankDetails : null,
                hasWhatsApp = !string.IsNullOrEmpty(st.WhatsApp),
                onlinePayment = false
            });
        });

        s.MapGet("/products", async (string siteKey, string? category, string? q, string? sort, int? page, int? pageSize, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var query = site.Db.Products.AsNoTracking().Where(p => p.ProjectId == project.Id && p.IsActive);
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(p => p.Name.Contains(q) || p.Description.Contains(q));
            query = sort switch
            {
                "price" => query.OrderBy(p => (double)p.Price),
                "-price" => query.OrderByDescending(p => (double)p.Price),
                "newest" => query.OrderByDescending(p => p.CreatedAt),
                _ => query.OrderBy(p => p.SortOrder).ThenByDescending(p => p.CreatedAt)
            };
            var size = Math.Clamp(pageSize ?? 24, 1, 100);
            var p = Math.Max(1, page ?? 1);
            var total = await query.CountAsync();
            var rows = await query.Skip((p - 1) * size).Take(size).ToListAsync();
            return Results.Ok(new { items = rows.Select(PublicProduct), total, page = p, pageSize = size, currency = project.Settings.Currency });
        });

        s.MapGet("/products/{pid:guid}", async (string siteKey, Guid pid, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var product = await site.Db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid && p.ProjectId == project.Id && p.IsActive)
                          ?? throw ApiException.NotFound("المنتج غير موجود");
            return Results.Ok(PublicProduct(product));
        });

        s.MapGet("/categories", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var items = await site.Db.Products.Where(p => p.ProjectId == project.Id && p.IsActive && p.Category != null)
                .GroupBy(p => p.Category!).Select(g => new { name = g.Key, count = g.Count() }).OrderBy(x => x.name).ToListAsync();
            return Results.Ok(new { items });
        });

        s.MapPost("/orders", async (string siteKey, PlaceOrderRequest req, HttpContext http, SiteContext site, SiteNotifier notifier) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            if (!string.IsNullOrEmpty(req._hp)) return Results.Ok(new { ok = true });
            var db = site.Db;
            var st = project.Settings;
            var name = RequireText(req.CustomerName, "الاسم", 120);
            var phone = RequirePhone(req.Phone);
            var method = string.IsNullOrWhiteSpace(req.PaymentMethod) ? st.PaymentMethods[0] : req.PaymentMethod.Trim();
            if (!st.PaymentMethods.Contains(method)) throw ApiException.BadRequest("طريقة الدفع غير متاحة في هذا المتجر");
            if (method == PaymentMethods.CashOnDelivery && string.IsNullOrWhiteSpace(req.Address))
                throw ApiException.BadRequest("اكتب عنوان التوصيل");
            var lines = (req.Items ?? []).Where(i => i.Quantity > 0).GroupBy(i => i.ProductId)
                .Select(g => (g.Key, Quantity: Math.Min(g.Sum(i => i.Quantity), 100))).ToList();
            if (lines.Count == 0) throw ApiException.BadRequest("السلة فارغة");
            if (lines.Count > 50) throw ApiException.BadRequest("عدد المنتجات في الطلب كبير جداً");
            var userId = await site.SiteUserIdAsync(http, project);

            var gate = RecordStore.LockFor(project.Id);
            await gate.WaitAsync();
            Order order;
            List<OrderLine> items;
            try
            {
                await site.EnsureTrialCapacityAsync(project, () => db.Orders.CountAsync(o => o.ProjectId == project.Id));
                var ids = lines.Select(l => l.Key).ToList();
                var products = await db.Products.Where(p => p.ProjectId == project.Id && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
                items = [];
                foreach (var (productId, qty) in lines)
                {
                    if (!products.TryGetValue(productId, out var product) || !product.IsActive)
                        throw ApiException.BadRequest("أحد المنتجات لم يعد متاحاً، حدّث السلة", "product_unavailable");
                    if (product.Stock is { } stock)
                    {
                        if (stock < qty) throw ApiException.BadRequest($"الكمية المتاحة من \"{product.Name}\" هي {stock} فقط", "out_of_stock");
                        product.Stock = stock - qty;
                    }
                    items.Add(new OrderLine(product.Id, product.Name, product.Price, qty, Images(product.ImagesJson).FirstOrDefault()));
                }
                var subtotal = items.Sum(i => i.Price * i.Quantity);
                var shipping = method == PaymentMethods.Pickup || (st.FreeShippingOver > 0 && subtotal >= st.FreeShippingOver) ? 0 : st.ShippingFee;
                var number = (await db.Orders.Where(o => o.ProjectId == project.Id).MaxAsync(o => (int?)o.Number) ?? 1000) + 1;
                order = new Order
                {
                    ProjectId = project.Id,
                    Number = number,
                    SiteUserId = userId,
                    CustomerName = name,
                    Phone = phone,
                    Email = OptionalEmail(req.Email),
                    Address = OptionalText(req.Address, 500),
                    City = OptionalText(req.City, 80),
                    Notes = OptionalText(req.Notes, 1000),
                    ItemsJson = JsonSerializer.Serialize(items, Json),
                    Subtotal = subtotal,
                    ShippingFee = shipping,
                    Total = subtotal + shipping,
                    Currency = st.Currency,
                    PaymentMethod = method
                };
                db.Orders.Add(order);
                await db.SaveChangesAsync();
            }
            finally { gate.Release(); }

            var summary = OrderSummary(order, items);
            notifier.Notify(project, SiteEventKind.Order, summary, order.Number);
            return Results.Ok(new
            {
                order.Id, order.Number, order.Subtotal, order.ShippingFee, order.Total, order.Currency, order.Status,
                paymentMethod = order.PaymentMethod,
                paymentLabel = PaymentLabel(order.PaymentMethod),
                bankDetails = order.PaymentMethod == PaymentMethods.BankTransfer ? st.BankDetails : null,
                whatsappUrl = SiteContext.WhatsAppLink(st.WhatsApp, summary)
            });
        }).RequireRateLimiting("forms");

        s.MapGet("/orders/{oid:guid}", async (string siteKey, Guid oid, string? phone, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var order = await site.Db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == oid && o.ProjectId == project.Id)
                        ?? throw ApiException.NotFound("الطلب غير موجود");
            var userId = await site.SiteUserIdAsync(http, project);
            var digits = Digits(phone);
            if (!(userId is not null && order.SiteUserId == userId) && (digits.Length < 6 || digits != Digits(order.Phone)))
                throw ApiException.NotFound("الطلب غير موجود");
            return Results.Ok(PublicOrder(order));
        });

        s.MapGet("/my/orders", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var user = await site.RequireUserAsync(http, project);
            var rows = await site.Db.Orders.AsNoTracking().Where(o => o.ProjectId == project.Id && o.SiteUserId == user.Id)
                .OrderByDescending(o => o.CreatedAt).Take(100).ToListAsync();
            return Results.Ok(new { items = rows.Select(PublicOrder) });
        });

        // ---------- Owner dashboard ----------
        var d = app.MapGroup("/api/projects/{id:guid}/data").RequireAuthorization();

        d.MapGet("/products", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var rows = await db.Products.Where(p => p.ProjectId == id).OrderBy(p => p.SortOrder).ThenByDescending(p => p.CreatedAt).ToListAsync();
            return Results.Ok(rows.Select(p => new
            {
                p.Id, p.Name, p.Description, p.Price, p.ComparePrice, p.Category, images = Images(p.ImagesJson), p.Stock, p.IsActive, p.SortOrder, p.CreatedAt
            }));
        });

        d.MapPost("/products", async (Guid id, ProductRequest req, HttpContext http, AppDbContext db, ProjectService projects, UploadService uploads) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (await db.Products.CountAsync(p => p.ProjectId == id) >= MaxProducts) throw ApiException.BadRequest("وصلت للحد الأقصى من المنتجات");
            var product = new Product { ProjectId = id };
            Apply(product, req, uploads.PublicPrefix(id));
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return Results.Ok(new { product.Id });
        });

        d.MapPut("/products/{pid:guid}", async (Guid id, Guid pid, ProductRequest req, HttpContext http, AppDbContext db, ProjectService projects, UploadService uploads) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var product = await db.Products.FirstOrDefaultAsync(p => p.Id == pid && p.ProjectId == id) ?? throw ApiException.NotFound();
            Apply(product, req, uploads.PublicPrefix(id));
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        d.MapDelete("/products/{pid:guid}", async (Guid id, Guid pid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            await db.Products.Where(p => p.Id == pid && p.ProjectId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        d.MapGet("/orders", async (Guid id, string? status, int? page, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var query = db.Orders.AsNoTracking().Where(o => o.ProjectId == id);
            if (!string.IsNullOrEmpty(status)) query = query.Where(o => o.Status == status);
            var p = Math.Max(1, page ?? 1);
            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(o => o.CreatedAt).Skip((p - 1) * 50).Take(50).ToListAsync();
            return Results.Ok(new
            {
                total,
                newCount = await db.Orders.CountAsync(o => o.ProjectId == id && o.Status == OrderStatuses.New),
                items = rows.Select(o => new
                {
                    o.Id, o.Number, o.CustomerName, o.Phone, o.Email, o.Address, o.City, o.Notes, items = Lines(o.ItemsJson),
                    o.Subtotal, o.ShippingFee, o.Total, o.Currency, o.PaymentMethod, paymentLabel = PaymentLabel(o.PaymentMethod),
                    o.Status, o.CreatedAt, whatsappUrl = SiteContext.WhatsAppLink(o.Phone, $"بخصوص طلبك رقم #{o.Number}")
                })
            });
        });

        d.MapPost("/orders/{oid:guid}/status", async (Guid id, Guid oid, StatusRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (!OrderStatuses.All.Contains(req.Status)) throw ApiException.BadRequest("حالة غير صالحة");
            var gate = RecordStore.LockFor(id);
            await gate.WaitAsync();
            try
            {
                var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == oid && o.ProjectId == id) ?? throw ApiException.NotFound();
                if (order.Status == req.Status) return Results.NoContent();
                var restock = req.Status == OrderStatuses.Canceled ? 1 : order.Status == OrderStatuses.Canceled ? -1 : 0;
                if (restock != 0)
                {
                    var lines = Lines(order.ItemsJson);
                    var ids = lines.Select(l => l.ProductId).ToList();
                    var products = await db.Products.Where(p => p.ProjectId == id && ids.Contains(p.Id) && p.Stock != null).ToListAsync();
                    foreach (var p in products)
                        p.Stock = Math.Max(0, p.Stock!.Value + restock * lines.Where(l => l.ProductId == p.Id).Sum(l => l.Quantity));
                }
                order.Status = req.Status;
                await db.SaveChangesAsync();
            }
            finally { gate.Release(); }
            return Results.NoContent();
        });
    }

    private static object PublicProduct(Product p) => new
    {
        p.Id, p.Name, p.Description, p.Price, p.ComparePrice, p.Category, images = Images(p.ImagesJson),
        image = Images(p.ImagesJson).FirstOrDefault(), inStock = p.Stock is null or > 0, p.Stock
    };

    private static object PublicOrder(Order o) => new
    {
        o.Id, o.Number, items = Lines(o.ItemsJson), o.Subtotal, o.ShippingFee, o.Total, o.Currency,
        o.PaymentMethod, paymentLabel = PaymentLabel(o.PaymentMethod), o.Status, o.CreatedAt
    };

    public static string OrderSummary(Order o, IEnumerable<OrderLine> items)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"طلب جديد رقم #{o.Number}");
        foreach (var i in items) sb.AppendLine($"- {i.Quantity} × {i.Name} ({(i.Price * i.Quantity).ToString("0.##", inv)} {o.Currency})");
        if (o.ShippingFee > 0) sb.AppendLine($"التوصيل: {o.ShippingFee.ToString("0.##", inv)} {o.Currency}");
        sb.AppendLine($"الإجمالي: {o.Total.ToString("0.##", inv)} {o.Currency}");
        sb.AppendLine($"الاسم: {o.CustomerName}");
        sb.AppendLine($"الهاتف: {o.Phone}");
        if (!string.IsNullOrEmpty(o.Address)) sb.AppendLine($"العنوان: {o.Address}{(string.IsNullOrEmpty(o.City) ? "" : "، " + o.City)}");
        if (!string.IsNullOrEmpty(o.Notes)) sb.AppendLine($"ملاحظات: {o.Notes}");
        sb.Append($"طريقة الدفع: {PaymentLabel(o.PaymentMethod)}");
        return sb.ToString();
    }

    private static void Apply(Product p, ProductRequest r, string uploadPrefix)
    {
        p.Name = RequireText(r.Name, "اسم المنتج", 200);
        if (r.Price < 0 || r.Price > 10_000_000) throw ApiException.BadRequest("السعر غير صالح");
        if (r.ComparePrice is < 0) throw ApiException.BadRequest("السعر قبل الخصم غير صالح");
        p.Description = Text.Truncate(r.Description?.Trim(), 10000);
        p.Price = r.Price;
        p.ComparePrice = r.ComparePrice is > 0 ? r.ComparePrice : null;
        p.Category = OptionalText(r.Category, 80);
        var images = (r.Images ?? []).Select(u => u.Trim()).Where(u => u.Length > 0).Take(10).ToList();
        foreach (var u in images)
            if (!u.StartsWith(uploadPrefix, StringComparison.Ordinal) && !(Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
                throw ApiException.BadRequest("رابط صورة غير صالح");
        p.ImagesJson = JsonSerializer.Serialize(images);
        p.Stock = r.Stock is null ? null : Math.Clamp(r.Stock.Value, 0, 1_000_000);
        p.IsActive = r.IsActive;
        p.SortOrder = r.SortOrder;
    }

    public static List<string> Images(string json) => JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static List<OrderLine> Lines(string json) => JsonSerializer.Deserialize<List<OrderLine>>(json, Json) ?? [];

    public static string RequireText(string? value, string label, int max)
    {
        var s = value?.Trim() ?? "";
        if (s.Length < 2) throw ApiException.BadRequest($"{label} مطلوب");
        return Text.Truncate(s, max);
    }

    public static string RequirePhone(string? phone)
    {
        var s = phone?.Trim() ?? "";
        var digits = Digits(s);
        if (digits.Length is < 7 or > 15) throw ApiException.BadRequest("رقم الهاتف غير صالح");
        return Text.Truncate(s, 30);
    }

    public static string? OptionalText(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : Text.Truncate(value.Trim(), max);

    public static string? OptionalEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim().ToLowerInvariant();
        return s.Contains('@') && !s.Contains(' ') ? Text.Truncate(s, 256) : throw ApiException.BadRequest("البريد الإلكتروني غير صالح");
    }

    public static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
}
