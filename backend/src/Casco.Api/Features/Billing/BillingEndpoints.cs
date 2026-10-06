using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.Notifications;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public record CheckoutRequest(string Kind, string? Interval, string? PackId, Guid? ProjectId = null, string? Tier = null, string? Ttclid = null, string? Ttp = null);
/// <param name="Amount">Dollars received outside Ziina for this hosting; recorded as revenue when > 0.</param>
public record GrantHostingRequest(string Tier, int Months, decimal? Amount = null);

public class PaymentFulfillment(AppDbContext db, SubscriptionService subscriptions, CreditService credits, HostingService hosting,
    UserEmails emails, TikTokEvents tiktok, ILogger<PaymentFulfillment> logger)
{
    /// <summary>Idempotent: only a pending payment can be completed, and only once.</summary>
    public async Task<Payment> ApplyStatusAsync(Payment payment, string providerStatus, BillingOptions billing)
    {
        if (payment.Status != PaymentStatuses.Pending) return payment;

        switch (providerStatus)
        {
            case "completed":
                var gate = CreditService.LockFor(payment.UserId);
                await gate.WaitAsync();
                try
                {
                    await db.Entry(payment).ReloadAsync();
                    if (payment.Status != PaymentStatuses.Pending) return payment;
                    payment.Status = PaymentStatuses.Completed;
                    payment.CompletedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
                finally { gate.Release(); }

                if (payment.Kind == PaymentKinds.Subscription)
                {
                    await subscriptions.ActivateProAsync(payment.UserId, payment.Interval ?? BillingIntervals.Monthly, payment.Id.ToString());
                }
                else if (payment.Kind == PaymentKinds.Hosting && payment.ProjectId is { } projectId && payment.HostingTier is { } tier)
                {
                    await hosting.ActivateAsync(projectId, tier, payment.Interval == BillingIntervals.Yearly ? 12 : 1);
                }
                else if (payment.Kind == PaymentKinds.Topup)
                {
                    var amount = payment.TopupCredits ?? billing.TopupPacks.FirstOrDefault(p => p.Id == payment.TopupPackId)?.Credits;
                    if (amount is > 0)
                        await credits.GrantAsync(payment.UserId, amount.Value, CreditBuckets.Topup, CreditEntryTypes.Topup, payment.Id.ToString());
                }
                logger.LogInformation("Payment {PaymentId} completed ({Kind})", payment.Id, payment.Kind);
                await emails.PaymentReceiptAsync(payment);
                var buyer = await db.Users.Where(u => u.Id == payment.UserId).Select(u => u.Email).FirstOrDefaultAsync();
                if (!string.IsNullOrEmpty(buyer)) tiktok.Purchase(payment, buyer);
                break;
            case "failed":
                payment.Status = PaymentStatuses.Failed;
                await db.SaveChangesAsync();
                break;
            case "canceled":
                payment.Status = PaymentStatuses.Canceled;
                await db.SaveChangesAsync();
                break;
        }
        return payment;
    }
}

public static class BillingEndpoints
{
    public static void MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/billing/plans", (IOptions<BillingOptions> opt) =>
        {
            var b = opt.Value;
            return Results.Ok(new
            {
                currency = b.Currency,
                free = new { maxProjects = b.Free.MaxProjects, signupCredits = b.SignupBonusCredits },
                pro = new
                {
                    monthlyPrice = b.Pro.MonthlyPriceMinor / 100m,
                    yearlyPrice = b.Pro.YearlyPriceMinor / 100m,
                    monthlyCredits = b.Pro.MonthlyCredits,
                    maxProjects = b.Pro.MaxProjects
                },
                topups = b.TopupPacks.Select(p => new { id = p.Id, credits = p.Credits, price = p.PriceMinor / 100m }),
                hosting = new
                {
                    staticMonthly = b.Hosting.PriceMinor(HostingTiers.Static, BillingIntervals.Monthly) / 100m,
                    staticYearly = b.Hosting.PriceMinor(HostingTiers.Static, BillingIntervals.Yearly) / 100m,
                    backendMonthly = b.Hosting.PriceMinor(HostingTiers.Backend, BillingIntervals.Monthly) / 100m,
                    backendYearly = b.Hosting.PriceMinor(HostingTiers.Backend, BillingIntervals.Yearly) / 100m,
                    graceDays = b.Hosting.GraceDays,
                    reactYearly = b.Hosting.ReactYearlyMinor / 100m
                },
                wallet = new
                {
                    amount = WalletPay.AmountMinor / 100m,
                    currency = WalletPay.Currency,
                    phone = WalletPay.Phone,
                    methods = new[] { WalletMethods.VodafoneCash, WalletMethods.InstaPay }
                }
            });
        });

        var g = app.MapGroup("/api/billing").RequireAuthorization();

        g.MapPost("/checkout", async (CheckoutRequest req, HttpContext http, AppDbContext db, ZiinaClient ziina, HostingService hosting,
            TikTokEvents tiktok, IOptions<BillingOptions> billingOpt, IOptions<AppOptions> appOpt) =>
        {
            var userId = http.User.UserId();
            var billing = billingOpt.Value;
            var payment = new Payment { UserId = userId, Currency = billing.Currency, IsTest = ziina.TestMode };
            string message;

            if (req.Kind == "subscription")
            {
                var interval = req.Interval == BillingIntervals.Yearly ? BillingIntervals.Yearly : BillingIntervals.Monthly;
                payment.Kind = "subscription";
                payment.Plan = PlanKeys.Pro;
                payment.Interval = interval;
                payment.AmountMinor = interval == BillingIntervals.Yearly ? billing.Pro.YearlyPriceMinor : billing.Pro.MonthlyPriceMinor;
                message = interval == BillingIntervals.Yearly ? "Casco Pro - اشتراك سنوي" : "Casco Pro - اشتراك شهري";
            }
            else if (req.Kind == "topup")
            {
                var pack = billing.TopupPacks.FirstOrDefault(p => p.Id == req.PackId)
                           ?? throw ApiException.BadRequest("باقة الشحن غير موجودة");
                payment.Kind = "topup";
                payment.TopupPackId = pack.Id;
                payment.TopupCredits = pack.Credits;
                payment.AmountMinor = pack.PriceMinor;
                message = $"Casco - شحن {pack.Credits} نقطة";
            }
            else if (req.Kind == PaymentKinds.Hosting)
            {
                var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == req.ProjectId && p.UserId == userId)
                              ?? throw ApiException.NotFound("الموقع غير موجود");
                Admin.SiteSuspension.EnsureNotSuspended(project);
                var react = project.TemplateKey == HostingTiers.React;
                var tier = react ? HostingTiers.React : req.Tier ?? await hosting.RequiredTierAsync(project);
                if (HostingTiers.Rank(tier) == 0) throw ApiException.BadRequest("نوع الاستضافة غير صالح");
                if (!react && HostingTiers.Rank(tier) < HostingTiers.Rank(await hosting.RequiredTierAsync(project)))
                    throw ApiException.BadRequest("هذا الموقع يستخدم الباك إند، لذلك يحتاج استضافة الباك إند", "backend_tier_required");
                var interval = react || req.Interval == BillingIntervals.Yearly ? BillingIntervals.Yearly : BillingIntervals.Monthly;
                payment.Kind = PaymentKinds.Hosting;
                payment.ProjectId = project.Id;
                payment.HostingTier = tier;
                payment.Interval = interval;
                payment.AmountMinor = billing.Hosting.PriceMinor(tier, interval);
                var what = tier == HostingTiers.React ? "استضافة تطبيق React" : tier == HostingTiers.Backend ? "استضافة موقع + باك إند" : "استضافة موقع";
                message = $"Casco - {what} ({project.Name}) - {(interval == BillingIntervals.Yearly ? "سنة" : "شهر")}";
            }
            else throw ApiException.BadRequest("نوع الدفع غير صالح");

            var front = appOpt.Value.FrontendBase;
            var intent = await ziina.CreatePaymentIntentAsync(payment.AmountMinor, payment.Currency, message,
                $"{front}/app/billing/result?pid={payment.Id}",
                $"{front}/app/billing/result?pid={payment.Id}&canceled=1",
                $"{front}/app/billing/result?pid={payment.Id}&failed=1");

            payment.ProviderPaymentId = intent.Id;
            db.Payments.Add(payment);
            await db.SaveChangesAsync();
            var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync() ?? "";
            tiktok.Checkout(payment, email, http, req.Ttclid, req.Ttp);
            return Results.Ok(new { paymentId = payment.Id, redirectUrl = intent.RedirectUrl });
        });

        g.MapPost("/payments/{id:guid}/confirm", async (Guid id, HttpContext http, AppDbContext db, ZiinaClient ziina,
            PaymentFulfillment fulfillment, IOptions<BillingOptions> billingOpt) =>
        {
            var userId = http.User.UserId();
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.Id == id && p.UserId == userId)
                          ?? throw ApiException.NotFound("عملية الدفع غير موجودة");
            if (payment.Status == PaymentStatuses.Pending && payment.ProviderPaymentId is not null)
            {
                var intent = await ziina.GetPaymentIntentAsync(payment.ProviderPaymentId);
                await fulfillment.ApplyStatusAsync(payment, intent.Status, billingOpt.Value);
            }
            return Results.Ok(new { payment.Id, payment.Status, payment.Kind, payment.Interval, payment.ProjectId, payment.HostingTier });
        });

        g.MapGet("/hosting", async (HttpContext http, AppDbContext db, HostingService hosting) =>
        {
            var userId = http.User.UserId();
            var projects = await db.Projects.Where(p => p.UserId == userId).OrderByDescending(p => p.UpdatedAt).ToListAsync();
            var items = new List<object>();
            foreach (var p in projects)
                items.Add(new { projectId = p.Id, p.Name, p.Slug, p.TemplateKey, published = p.PublishedAt != null, status = await hosting.StatusAsync(p) });
            return Results.Ok(new { items, prices = hosting.Prices() });
        });

        g.MapGet("/history", async (HttpContext http, AppDbContext db) =>
        {
            var userId = http.User.UserId();
            var payments = await db.Payments.Where(p => p.UserId == userId && p.Status != PaymentStatuses.Pending)
                .OrderByDescending(p => p.CreatedAt).Take(50)
                .Select(p => new { p.Id, p.Kind, p.Interval, p.TopupPackId, p.ProjectId, p.HostingTier, amount = p.AmountMinor / 100m, p.Currency, p.Status, p.CreatedAt, p.IsTest })
                .ToListAsync();
            var entries = await db.CreditEntries.Where(e => e.UserId == userId)
                .OrderByDescending(e => e.Id).Take(100)
                .Select(e => new { e.Id, e.Amount, e.Bucket, e.Type, e.Reference, e.CreatedAt })
                .ToListAsync();
            return Results.Ok(new { payments, credits = entries });
        });

        g.MapGet("/wallet-transfers", async (HttpContext http, AppDbContext db) =>
        {
            var userId = http.User.UserId();
            var items = await db.WalletTransfers.Where(t => t.UserId == userId)
                .OrderByDescending(t => t.CreatedAt).Take(10)
                .Select(t => new { t.Id, t.Method, amount = t.AmountMinor / 100m, t.Currency, t.Status, t.ReviewNote, t.CreatedAt, t.ReviewedAt })
                .ToListAsync();
            return Results.Ok(new { items });
        });

        g.MapPost("/wallet-transfers", async (HttpContext http, WalletTransferService wallet, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? throw ApiException.BadRequest("ارفع صورة التحويل");
            await using var stream = file.OpenReadStream();
            var row = await wallet.SubmitAsync(http.User.UserId(), form["method"].ToString(), stream, ct);
            return Results.Ok(new { row.Id, row.Status });
        }).DisableAntiforgery().RequireRateLimiting("uploads");

        app.MapPost("/api/billing/ziina/webhook", async (HttpContext http, AppDbContext db, ZiinaClient ziina,
            PaymentFulfillment fulfillment, IOptions<ZiinaOptions> ziinaOpt, IOptions<BillingOptions> billingOpt, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("ZiinaWebhook");
            var opt = ziinaOpt.Value;
            var remoteIp = http.Connection.RemoteIpAddress?.MapToIPv4().ToString();
            if (opt.EnforceIpAllowlist && (remoteIp is null || !opt.AllowedIps.Contains(remoteIp)))
            {
                log.LogWarning("Rejected webhook from {Ip}", remoteIp);
                return Results.StatusCode(403);
            }

            using var reader = new StreamReader(http.Request.Body);
            var raw = await reader.ReadToEndAsync();
            if (!ZiinaClient.VerifySignature(raw, http.Request.Headers["X-Hmac-Signature"], opt.WebhookSecret))
            {
                log.LogWarning("Invalid webhook signature");
                return Results.Unauthorized();
            }

            using var doc = JsonDocument.Parse(raw);
            var evt = doc.RootElement.TryGetProperty("event", out var e) ? e.GetString() : null;
            if (evt != "payment_intent.status.updated" || !doc.RootElement.TryGetProperty("data", out var data))
                return Results.Ok();

            var intentId = data.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (intentId is null) return Results.Ok();
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.ProviderPaymentId == intentId);
            if (payment is null) return Results.Ok();

            // Never trust the payload alone: re-fetch the intent from Ziina.
            var intent = await ziina.GetPaymentIntentAsync(intentId);
            await fulfillment.ApplyStatusAsync(payment, intent.Status, billingOpt.Value);
            return Results.Ok();
        });
    }
}
