using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.Projects;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Casco.Api.Features.SiteRuntime;

public record CourseRequest(string Title, string? Description, string? ImageUrl, decimal Price, string? Currency,
    string? Instructor, string? Category, string? PaymentLink, bool IsPublished = true, int SortOrder = 0);
public record LessonRequest(string Title, string? VideoUrl, string? Content, bool IsFreePreview, int DurationMinutes, int SortOrder);
public record AdStatusRequest(string Status);
public record SiteSettingsRequest(bool? AdsRequireApproval, SiteSettings? Settings);

/// <summary>Dashboard API for site owners to manage the data behind their websites.</summary>
public static class SiteDataEndpoints
{
    /// <summary>Casco technical support (online payments and custom integrations).</summary>
    public const string SupportWhatsApp = "+971569166263";

    public static void MapSiteDataEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{id:guid}/data").RequireAuthorization();

        // ---------- Form submissions ----------
        g.MapGet("/submissions", async (Guid id, int? page, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var p = Math.Max(1, page ?? 1);
            var query = db.FormSubmissions.Where(f => f.ProjectId == id);
            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(f => f.CreatedAt).Skip((p - 1) * 30).Take(30).ToListAsync();
            return Results.Ok(new
            {
                total,
                unread = await query.CountAsync(f => !f.IsRead),
                items = rows.Select(f => new { f.Id, f.FormName, data = JsonSerializer.Deserialize<Dictionary<string, string>>(f.DataJson), f.IsRead, f.CreatedAt })
            });
        });

        g.MapPost("/submissions/{sid:guid}/read", async (Guid id, Guid sid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            await db.FormSubmissions.Where(f => f.Id == sid && f.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(f => f.IsRead, true));
            return Results.NoContent();
        });

        g.MapDelete("/submissions/{sid:guid}", async (Guid id, Guid sid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            await db.FormSubmissions.Where(f => f.Id == sid && f.ProjectId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        // ---------- Courses & lessons ----------
        g.MapGet("/courses", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var courses = await db.Courses.Include(c => c.Lessons).Where(c => c.ProjectId == id)
                .OrderBy(c => c.SortOrder).ThenByDescending(c => c.CreatedAt).ToListAsync();
            return Results.Ok(courses.Select(c => new
            {
                c.Id, c.Title, c.Description, c.ImageUrl, c.Price, c.Currency, c.Instructor, c.Category, c.PaymentLink, c.IsPublished, c.SortOrder,
                lessons = c.Lessons.OrderBy(l => l.SortOrder).Select(l => new { l.Id, l.Title, l.VideoUrl, l.Content, l.IsFreePreview, l.DurationMinutes, l.SortOrder })
            }));
        });

        g.MapPost("/courses", async (Guid id, CourseRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var course = new Course { ProjectId = id };
            Apply(course, req);
            db.Courses.Add(course);
            await db.SaveChangesAsync();
            return Results.Ok(new { course.Id });
        });

        g.MapPut("/courses/{cid:guid}", async (Guid id, Guid cid, CourseRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == cid && c.ProjectId == id) ?? throw ApiException.NotFound();
            Apply(course, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/courses/{cid:guid}", async (Guid id, Guid cid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == cid && c.ProjectId == id) ?? throw ApiException.NotFound();
            await db.Enrollments.Where(e => e.CourseId == cid).ExecuteDeleteAsync();
            db.Courses.Remove(course);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/courses/{cid:guid}/lessons", async (Guid id, Guid cid, LessonRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (!await db.Courses.AnyAsync(c => c.Id == cid && c.ProjectId == id)) throw ApiException.NotFound();
            var lesson = new Lesson { CourseId = cid };
            Apply(lesson, req);
            db.Lessons.Add(lesson);
            await db.SaveChangesAsync();
            return Results.Ok(new { lesson.Id });
        });

        g.MapPut("/lessons/{lid:guid}", async (Guid id, Guid lid, LessonRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var lesson = await (from l in db.Lessons join c in db.Courses on l.CourseId equals c.Id
                                where l.Id == lid && c.ProjectId == id select l).FirstOrDefaultAsync() ?? throw ApiException.NotFound();
            Apply(lesson, req);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/lessons/{lid:guid}", async (Guid id, Guid lid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var lesson = await (from l in db.Lessons join c in db.Courses on l.CourseId equals c.Id
                                where l.Id == lid && c.ProjectId == id select l).FirstOrDefaultAsync() ?? throw ApiException.NotFound();
            db.Lessons.Remove(lesson);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // ---------- Enrollments & site users ----------
        g.MapGet("/enrollments", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var rows = await (from e in db.Enrollments
                              join c in db.Courses on e.CourseId equals c.Id
                              join u in db.SiteUsers on e.SiteUserId equals u.Id
                              where c.ProjectId == id
                              orderby e.CreatedAt descending
                              select new { e.Id, e.Status, e.CreatedAt, courseTitle = c.Title, c.Price, c.Currency, studentName = u.Name, studentEmail = u.Email })
                .Take(500).ToListAsync();
            return Results.Ok(rows);
        });

        g.MapPost("/enrollments/{eid:guid}/approve", async (Guid id, Guid eid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var enrollment = await (from e in db.Enrollments join c in db.Courses on e.CourseId equals c.Id
                                    where e.Id == eid && c.ProjectId == id select e).FirstOrDefaultAsync() ?? throw ApiException.NotFound();
            enrollment.Status = "active";
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/enrollments/{eid:guid}", async (Guid id, Guid eid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var enrollment = await (from e in db.Enrollments join c in db.Courses on e.CourseId equals c.Id
                                    where e.Id == eid && c.ProjectId == id select e).FirstOrDefaultAsync() ?? throw ApiException.NotFound();
            db.Enrollments.Remove(enrollment);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/users", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(await db.SiteUsers.Where(u => u.ProjectId == id).OrderByDescending(u => u.CreatedAt)
                .Select(u => new { u.Id, u.Name, u.Email, u.CreatedAt }).Take(1000).ToListAsync());
        });

        // ---------- Ads moderation ----------
        g.MapGet("/ads", async (Guid id, string? status, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var query = db.Ads.Where(a => a.ProjectId == id);
            if (!string.IsNullOrEmpty(status)) query = query.Where(a => a.Status == status);
            var rows = await query.OrderByDescending(a => a.CreatedAt).Take(300).ToListAsync();
            return Results.Ok(rows.Select(a => new
            {
                a.Id, a.Title, a.Description, a.Price, a.Currency, a.Category, a.City, a.Phone, whatsapp = a.WhatsApp,
                a.Status, a.Views, a.CreatedAt, images = JsonSerializer.Deserialize<List<string>>(a.ImagesJson)
            }));
        });

        g.MapPost("/ads/{aid:guid}/status", async (Guid id, Guid aid, AdStatusRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            if (req.Status is not ("approved" or "rejected" or "pending")) throw ApiException.BadRequest("حالة غير صالحة");
            var updated = await db.Ads.Where(a => a.Id == aid && a.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, req.Status));
            return updated == 0 ? throw ApiException.NotFound() : Results.NoContent();
        });

        g.MapDelete("/ads/{aid:guid}", async (Guid id, Guid aid, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            await db.Ads.Where(a => a.Id == aid && a.ProjectId == id).ExecuteDeleteAsync();
            return Results.NoContent();
        });

        g.MapGet("/settings", async (Guid id, HttpContext http, ProjectService projects) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(new
            {
                adsRequireApproval = project.AdsRequireApproval,
                settings = SiteSettings.Parse(project.SettingsJson),
                paymentMethods = PaymentMethods.All.Select(m => new { id = m, label = Commerce.StoreEndpoints.PaymentLabel(m) }),
                onlinePayment = new
                {
                    available = false,
                    message = "الدفع الإلكتروني بالبطاقات يحتاج رخصة شركة وحساب بنكي بنفس اسم الرخصة، ومبرمج للتأكد من أن عمليات الدفع تتم بأمان. تواصل مع الدعم الفني لـ Casco لتفعيله لموقعك.",
                    supportWhatsApp = SupportWhatsApp
                }
            });
        });

        g.MapPut("/settings", async (Guid id, SiteSettingsRequest req, HttpContext http, AppDbContext db, ProjectService projects, IMemoryCache cache) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            if (req.AdsRequireApproval is { } approval) project.AdsRequireApproval = approval;
            if (req.Settings is not null) project.SettingsJson = req.Settings.Sanitize().Serialize();
            await db.SaveChangesAsync();
            SiteContext.Invalidate(cache, project.SiteKey);
            return Results.NoContent();
        });

        g.MapPost("/uploads", async (Guid id, IFormFile file, HttpContext http, ProjectService projects, UploadService uploads) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(new { url = await uploads.SaveImageAsync(id, file) });
        }).DisableAntiforgery().RequireRateLimiting("uploads");
    }

    private static void Apply(Course c, CourseRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title)) throw ApiException.BadRequest("عنوان الكورس مطلوب");
        if (r.Price < 0) throw ApiException.BadRequest("السعر غير صالح");
        c.Title = Text.Truncate(r.Title.Trim(), 200);
        c.Description = Text.Truncate(r.Description?.Trim(), 5000);
        c.ImageUrl = SafeUrl(r.ImageUrl);
        c.Price = r.Price;
        c.Currency = Text.Truncate(string.IsNullOrWhiteSpace(r.Currency) ? "USD" : r.Currency.Trim().ToUpperInvariant(), 3);
        c.Instructor = Text.Truncate(r.Instructor?.Trim(), 120);
        c.Category = Text.Truncate(r.Category?.Trim(), 80);
        c.PaymentLink = SafeUrl(r.PaymentLink);
        c.IsPublished = r.IsPublished;
        c.SortOrder = r.SortOrder;
    }

    private static void Apply(Lesson l, LessonRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Title)) throw ApiException.BadRequest("عنوان الدرس مطلوب");
        l.Title = Text.Truncate(r.Title.Trim(), 200);
        l.VideoUrl = SafeUrl(r.VideoUrl);
        l.Content = Text.Truncate(r.Content?.Trim(), 20000);
        l.IsFreePreview = r.IsFreePreview;
        l.DurationMinutes = Math.Clamp(r.DurationMinutes, 0, 10000);
        l.SortOrder = r.SortOrder;
    }

    /// <summary>Only http(s) URLs are stored, so a javascript: link can never reach a published page.</summary>
    private static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.Trim();
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? Text.Truncate(url, 1000)
            : throw ApiException.BadRequest("الرابط يجب أن يبدأ بـ https://");
    }
}
