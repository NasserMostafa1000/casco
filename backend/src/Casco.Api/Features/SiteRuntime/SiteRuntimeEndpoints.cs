using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.Auth;
using Casco.Api.Features.Notifications;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Casco.Api.Features.SiteRuntime;

public record SiteRegisterRequest(string? Name, string Email, string Password);
public record SiteLoginRequest(string Email, string Password);
public record CreateAdRequest(string Title, string? Description, decimal Price, string? Currency, string? Category,
    string? City, string? Phone, string? Whatsapp, List<string>? Images);

/// <summary>
/// Public API used by generated websites through the Casco SDK. Sites are identified by their public SiteKey.
/// Contact forms work on static hosting; everything else is part of the backend hosting tier.
/// </summary>
public static class SiteRuntimeEndpoints
{
    public static void MapSiteRuntimeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/s/{siteKey}").RequireCors("sites").RequireRateLimiting("site");

        g.MapPost("/forms/{formName}", async (string siteKey, string formName, JsonElement body, SiteContext site, SiteNotifier notifier) =>
        {
            var project = await site.ResolveAsync(siteKey);
            if (body.ValueKind != JsonValueKind.Object) throw ApiException.BadRequest("بيانات غير صالحة");

            var data = new Dictionary<string, string>();
            foreach (var prop in body.EnumerateObject().Take(30))
            {
                if (prop.Name == "_hp") { if (prop.Value.ToString().Length > 0) return Results.Ok(new { ok = true }); continue; }
                data[Text.Truncate(prop.Name, 60)] = Text.Truncate(prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString(), 5000);
            }
            if (data.Count == 0) throw ApiException.BadRequest("النموذج فارغ");

            site.Db.FormSubmissions.Add(new FormSubmission
            {
                ProjectId = project.Id,
                FormName = Text.Truncate(formName, 50),
                DataJson = JsonSerializer.Serialize(data)
            });
            await site.Db.SaveChangesAsync();
            notifier.Notify(project, SiteEventKind.Message, string.Join("\n", data.Select(kv => $"{kv.Key}: {kv.Value}")));
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("forms");

        g.MapPost("/auth/register", async (string siteKey, SiteRegisterRequest req, HttpContext http, SiteContext site, TokenService tokens) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            AuthEndpoints.ValidateCredentials(req.Email, req.Password);
            var db = site.Db;
            var email = Text.NormalizeEmail(req.Email);
            if (await db.SiteUsers.AnyAsync(u => u.ProjectId == project.Id && u.Email == email))
                throw ApiException.Conflict("هذا البريد مسجل بالفعل، سجّل الدخول");
            await site.EnsureTrialCapacityAsync(project, () => db.SiteUsers.CountAsync(u => u.ProjectId == project.Id));
            var user = new SiteUser
            {
                ProjectId = project.Id,
                Email = email,
                Name = Text.Truncate(string.IsNullOrWhiteSpace(req.Name) ? email.Split('@')[0] : req.Name.Trim(), 120),
                PasswordHash = AuthEndpoints.HashPassword(req.Password)
            };
            db.SiteUsers.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(new { token = tokens.CreateSiteUserToken(user), user = new { user.Id, user.Name, user.Email } });
        }).RequireRateLimiting("auth");

        g.MapPost("/auth/login", async (string siteKey, SiteLoginRequest req, HttpContext http, SiteContext site, TokenService tokens) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var email = Text.NormalizeEmail(req.Email ?? "");
            var user = await site.Db.SiteUsers.FirstOrDefaultAsync(u => u.ProjectId == project.Id && u.Email == email);
            if (user is null || !AuthEndpoints.VerifyPassword(user.PasswordHash, req.Password ?? ""))
                throw new ApiException(401, "البريد أو كلمة المرور غير صحيحة");
            return Results.Ok(new { token = tokens.CreateSiteUserToken(user), user = new { user.Id, user.Name, user.Email } });
        }).RequireRateLimiting("auth");

        g.MapPost("/auth/logout", async (string siteKey, HttpContext http, SiteContext site, TokenService tokens) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var session = await tokens.ReadSiteSessionAsync(http.Request.Headers.Authorization, project.Id);
            if (session is null || await TokenRevocation.IsRevokedAsync(site.Db, session.Value.Jti, http.RequestAborted))
                throw new ApiException(401, "سجّل الدخول أولاً", "login_required");
            await TokenRevocation.RevokeAsync(site.Db, session.Value.Jti, session.Value.ExpiresAt, http.RequestAborted);
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        g.MapGet("/auth/me", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var user = await site.RequireUserAsync(http, project);
            return Results.Ok(new { user.Id, user.Name, user.Email });
        });

        // ---------- Courses ----------
        g.MapGet("/courses", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var items = await site.Db.Courses.Where(c => c.ProjectId == project.Id && c.IsPublished)
                .OrderBy(c => c.SortOrder).ThenByDescending(c => c.CreatedAt)
                .Select(c => new { c.Id, c.Title, c.Description, c.ImageUrl, c.Price, c.Currency, c.Instructor, c.Category, lessonsCount = c.Lessons.Count })
                .ToListAsync();
            return Results.Ok(new { items });
        });

        g.MapGet("/courses/{id:guid}", async (string siteKey, Guid id, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var db = site.Db;
            var course = await db.Courses.Include(c => c.Lessons)
                .FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == project.Id && c.IsPublished)
                ?? throw ApiException.NotFound("الكورس غير موجود");

            var userId = await site.SiteUserIdAsync(http, project);
            var enrollment = userId is null ? null
                : await db.Enrollments.FirstOrDefaultAsync(e => e.CourseId == id && e.SiteUserId == userId);
            var unlockedAll = enrollment?.Status == "active";

            return Results.Ok(new
            {
                course.Id, course.Title, course.Description, course.ImageUrl, course.Price, course.Currency,
                course.Instructor, course.Category, course.PaymentLink,
                enrollment = enrollment is null ? null : new { enrollment.Status },
                lessons = course.Lessons.OrderBy(l => l.SortOrder).Select(l =>
                {
                    var open = unlockedAll || l.IsFreePreview;
                    return new
                    {
                        l.Id, l.Title, l.DurationMinutes, l.IsFreePreview,
                        locked = !open,
                        videoUrl = open ? l.VideoUrl : null,
                        content = open ? l.Content : null
                    };
                })
            });
        });

        g.MapPost("/courses/{id:guid}/enroll", async (string siteKey, Guid id, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var db = site.Db;
            var user = await site.RequireUserAsync(http, project);
            var course = await db.Courses.FirstOrDefaultAsync(c => c.Id == id && c.ProjectId == project.Id && c.IsPublished)
                         ?? throw ApiException.NotFound("الكورس غير موجود");
            var enrollment = await db.Enrollments.FirstOrDefaultAsync(e => e.CourseId == id && e.SiteUserId == user.Id);
            if (enrollment is null)
            {
                enrollment = new Enrollment { CourseId = id, SiteUserId = user.Id, Status = course.Price <= 0 ? "active" : "pending" };
                db.Enrollments.Add(enrollment);
                await db.SaveChangesAsync();
            }
            return Results.Ok(new { enrollment.Status, paymentLink = enrollment.Status == "pending" ? course.PaymentLink : null });
        });

        g.MapGet("/my/courses", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var user = await site.RequireUserAsync(http, project);
            var db = site.Db;
            var items = await (from e in db.Enrollments
                               join c in db.Courses on e.CourseId equals c.Id
                               where e.SiteUserId == user.Id && c.ProjectId == project.Id
                               orderby e.CreatedAt descending
                               select new { c.Id, c.Title, c.ImageUrl, e.Status }).ToListAsync();
            return Results.Ok(new { items });
        });

        // ---------- Classified ads ----------
        g.MapGet("/ads", async (string siteKey, string? q, string? category, string? city, int? page, int? pageSize, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var size = Math.Clamp(pageSize ?? 12, 1, 50);
            var p = Math.Max(1, page ?? 1);
            var query = site.Db.Ads.Where(a => a.ProjectId == project.Id && a.Status == "approved");
            if (!string.IsNullOrWhiteSpace(q)) query = query.Where(a => a.Title.Contains(q) || a.Description.Contains(q));
            if (!string.IsNullOrWhiteSpace(category)) query = query.Where(a => a.Category == category);
            if (!string.IsNullOrWhiteSpace(city)) query = query.Where(a => a.City == city);

            var total = await query.CountAsync();
            var rows = await query.OrderByDescending(a => a.CreatedAt).Skip((p - 1) * size).Take(size)
                .Select(a => new { a.Id, a.Title, a.Price, a.Currency, a.Category, a.City, a.ImagesJson, a.CreatedAt }).ToListAsync();
            var items = rows.Select(a => new { a.Id, a.Title, a.Price, a.Currency, a.Category, a.City, image = FirstImage(a.ImagesJson), a.CreatedAt });
            return Results.Ok(new { items, total, page = p, pageSize = size });
        });

        g.MapGet("/ads/{id:guid}", async (string siteKey, Guid id, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var db = site.Db;
            var ad = await db.Ads.FirstOrDefaultAsync(a => a.Id == id && a.ProjectId == project.Id) ?? throw ApiException.NotFound("الإعلان غير موجود");
            if (ad.Status != "approved")
            {
                var viewer = await site.SiteUserIdAsync(http, project);
                if (viewer != ad.SiteUserId) throw ApiException.NotFound("الإعلان غير موجود");
            }
            else
            {
                ad.Views++;
                await db.SaveChangesAsync();
            }
            var seller = await db.SiteUsers.Where(u => u.Id == ad.SiteUserId).Select(u => u.Name).FirstOrDefaultAsync();
            return Results.Ok(new
            {
                ad.Id, ad.Title, ad.Description, ad.Price, ad.Currency, ad.Category, ad.City, ad.Phone,
                whatsapp = ad.WhatsApp, images = Images(ad.ImagesJson), ad.CreatedAt, sellerName = seller, ad.Views, ad.Status
            });
        });

        g.MapPost("/ads", async (string siteKey, CreateAdRequest req, HttpContext http, SiteContext site, UploadService uploads) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var db = site.Db;
            var user = await site.RequireUserAsync(http, project);
            if (string.IsNullOrWhiteSpace(req.Title) || req.Title.Trim().Length < 3) throw ApiException.BadRequest("اكتب عنواناً واضحاً للإعلان");
            if (req.Price < 0) throw ApiException.BadRequest("السعر غير صالح");
            var todayCount = await db.Ads.CountAsync(a => a.SiteUserId == user.Id && a.CreatedAt > DateTime.UtcNow.AddDays(-1));
            if (todayCount >= 20) throw ApiException.BadRequest("وصلت للحد اليومي لنشر الإعلانات");
            await site.EnsureTrialCapacityAsync(project, () => db.Ads.CountAsync(a => a.ProjectId == project.Id));

            var prefix = uploads.PublicPrefix(project.Id);
            var images = (req.Images ?? []).Where(u => u.StartsWith(prefix, StringComparison.Ordinal)).Take(5).ToList();
            var ad = new Ad
            {
                ProjectId = project.Id,
                SiteUserId = user.Id,
                Title = Text.Truncate(req.Title.Trim(), 150),
                Description = Text.Truncate(req.Description?.Trim(), 5000),
                Price = req.Price,
                Currency = Text.Truncate(string.IsNullOrWhiteSpace(req.Currency) ? "AED" : req.Currency.Trim().ToUpperInvariant(), 3),
                Category = Text.Truncate(req.Category?.Trim(), 60),
                City = Text.Truncate(req.City?.Trim(), 60),
                Phone = Text.Truncate(req.Phone?.Trim(), 30),
                WhatsApp = Text.Truncate(req.Whatsapp?.Trim(), 30),
                ImagesJson = JsonSerializer.Serialize(images),
                Status = project.AdsRequireApproval ? "pending" : "approved"
            };
            db.Ads.Add(ad);
            await db.SaveChangesAsync();
            return Results.Ok(new { ad.Id, ad.Status });
        });

        g.MapGet("/my/ads", async (string siteKey, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var user = await site.RequireUserAsync(http, project);
            var rows = await site.Db.Ads.Where(a => a.ProjectId == project.Id && a.SiteUserId == user.Id).OrderByDescending(a => a.CreatedAt)
                .Select(a => new { a.Id, a.Title, a.Price, a.Currency, a.Status, a.ImagesJson, a.CreatedAt }).ToListAsync();
            return Results.Ok(new { items = rows.Select(a => new { a.Id, a.Title, a.Price, a.Currency, a.Status, image = FirstImage(a.ImagesJson), a.CreatedAt }) });
        });

        g.MapDelete("/ads/{id:guid}", async (string siteKey, Guid id, HttpContext http, SiteContext site) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            var user = await site.RequireUserAsync(http, project);
            var ad = await site.Db.Ads.FirstOrDefaultAsync(a => a.Id == id && a.ProjectId == project.Id && a.SiteUserId == user.Id)
                     ?? throw ApiException.NotFound("الإعلان غير موجود");
            site.Db.Ads.Remove(ad);
            await site.Db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapPost("/uploads", async (string siteKey, IFormFile file, HttpContext http, SiteContext site, UploadService uploads) =>
        {
            var project = await site.ResolveAsync(siteKey);
            site.EnsureBackend(project, http);
            await site.RequireUserAsync(http, project);
            return Results.Ok(new { url = await uploads.SaveImageAsync(project.Id, file) });
        }).DisableAntiforgery().RequireRateLimiting("uploads");
    }

    private static List<string> Images(string json) =>
        JsonSerializer.Deserialize<List<string>>(json) ?? [];

    private static string? FirstImage(string json) => Images(json).FirstOrDefault();
}
