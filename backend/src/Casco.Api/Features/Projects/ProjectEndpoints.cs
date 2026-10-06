using System.IO.Compression;
using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Auth;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Projects;

public record CreateProjectRequest(string Name, string Description, string? TemplateKey, string? Language = null, bool Generate = true);
public record UpdateProjectRequest(string? Name, string? Description);
/// <param name="Continue">The "كمل البناء" button: continue the site's unfinished big build.</param>
public record SendMessageRequest(string? Content, bool Premium = false, List<string>? Images = null, bool Continue = false);
public record GenerateSiteRequest(List<string>? Images = null, string? Language = null);
public record FixRequest(List<string> Errors, bool Premium = false);
public record ChatQuestion(string Prompt, List<ChatQuestionOption> Options);
public record ChatQuestionOption(string Label, bool Recommended);

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").RequireAuthorization();

        g.MapGet("/", async (HttpContext http, AppDbContext db, IOptions<AppOptions> appOpt, IOptions<BillingOptions> billing) =>
        {
            var userId = http.User.UserId();
            var now = DateTime.UtcNow;
            var projects = await db.Projects.Where(p => p.UserId == userId).OrderByDescending(p => p.UpdatedAt)
                .Select(p => new { p.Id, p.Name, p.TemplateKey, p.Slug, p.PublishedAt, p.UpdatedAt, p.CreatedAt, p.CustomDomain, p.CustomDomainVerified, p.IsAdminSuspended, p.HostingTier, p.HostingPaidUntil })
                .ToListAsync();
            return Results.Ok(projects.Select(p =>
            {
                var grace = billing.Value.Hosting.GraceDaysFor(p.HostingTier);
                var online = p.HostingTier != null && p.HostingPaidUntil is { } end && end.AddDays(grace) > now;
                var daysLeft = p.HostingPaidUntil is { } until && until > now ? (int)Math.Ceiling((until - now).TotalDays) : (int?)null;
                return new
                {
                    p.Id, p.Name, p.TemplateKey, p.Slug, p.PublishedAt, p.UpdatedAt, p.CreatedAt, suspended = p.IsAdminSuspended,
                    hostingTier = p.HostingTier, hostingDaysLeft = daysLeft, hostingOnline = online,
                    siteUrl = p.PublishedAt is null ? null : SiteUrl(appOpt.Value, p.Slug, p.CustomDomain, p.CustomDomainVerified)
                };
            }));
        });

        g.MapPost("/", async (CreateProjectRequest req, HttpContext http, ProjectService projects) =>
        {
            var userId = http.User.UserId();
            await projects.RememberLanguageAsync(userId, http.Request.Headers["X-Casco-Lang"].ToString());
            var (project, taskId, notice) = await projects.CreateAsync(userId, req.Name, req.Description, req.TemplateKey, req.Language, req.Generate);
            return Results.Ok(new { id = project.Id, taskId, notice });
        }).RequireRateLimiting("ai");

        g.MapPost("/{id:guid}/generate", async (Guid id, GenerateSiteRequest req, HttpContext http, ProjectService projects, UploadService uploads) =>
        {
            var userId = http.User.UserId();
            await projects.RememberLanguageAsync(userId, http.Request.Headers["X-Casco-Lang"].ToString());
            var images = (req.Images ?? []).Distinct().Take(10).ToList();
            if (!await uploads.AreOwnerImagesAsync(id, images, http.RequestAborted)) throw ApiException.BadRequest("إحدى الصور غير موجودة، ارفعها مرة أخرى");
            var taskId = await projects.StartInitialGenerateAsync(id, userId, images, req.Language);
            return Results.Ok(new { taskId });
        }).RequireRateLimiting("ai");

        g.MapGet("/{id:guid}", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects,
            TokenService tokens, IOptions<AppOptions> appOpt, TemplateCatalog templates) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            var versions = await db.ProjectVersions.Where(v => v.ProjectId == id).OrderByDescending(v => v.Number)
                .Select(v => new { v.Id, v.Number, v.Summary, v.Prompt, v.Model, v.Parts, v.FilesChanged, v.CreatedAt }).Take(50).ToListAsync();
            var messages = (await db.ChatMessages.Where(m => m.ProjectId == id).OrderByDescending(m => m.Id).Take(100)
                    .Select(m => new { m.Id, m.Role, m.Content, m.Credits, m.CreatedAt, m.TaskId, m.ImagesJson, m.QuestionsJson }).ToListAsync())
                .Select(m => new
                {
                    m.Id, m.Role, m.Content, m.Credits, m.CreatedAt, m.TaskId,
                    images = m.ImagesJson is null ? null : JsonSerializer.Deserialize<List<string>>(m.ImagesJson),
                    questions = m.QuestionsJson is null ? null : JsonSerializer.Deserialize<List<ChatQuestion>>(m.QuestionsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                }).ToList();
            var activeTask = await db.AgentTasks
                .Where(t => t.ProjectId == id && (t.Status == TaskStatuses.Queued || t.Status == TaskStatuses.Running))
                .Select(t => new { t.Id, t.Status, t.Kind }).FirstOrDefaultAsync();
            // The latest task stopped before finishing a big build: the editor offers "كمل البناء".
            var lastTask = activeTask is not null ? null : await db.AgentTasks.Where(t => t.ProjectId == id).OrderByDescending(t => t.CreatedAt)
                .Select(t => new { t.Id, t.Status, t.Remaining }).FirstOrDefaultAsync();
            var continuable = lastTask is { Status: TaskStatuses.Succeeded, Remaining: not null }
                ? new { taskId = lastTask.Id, remaining = lastTask.Remaining, pagesLeft = Agent.AgentTaskRunner.PagesLeft(lastTask.Remaining) }
                : null;
            var fileMap = project.CurrentVersionId is { } vid
                ? SiteFiles.Parse((await db.ProjectVersions.FindAsync(vid))!.FilesJson)
                : new SortedDictionary<string, string>();
            var files = fileMap.Keys.ToList();
            var opt = appOpt.Value;
            var previewToken = tokens.CreatePreviewToken(project.Id, TimeSpan.FromHours(12));

            return Results.Ok(new
            {
                project.Id, project.Name, project.Description, project.TemplateKey, project.Slug, project.SiteKey,
                project.CustomDomain, project.CustomDomainVerified, project.AdsRequireApproval,
                project.CurrentVersionId, project.PublishedVersionId, project.PublishedAt,
                suspension = project.IsAdminSuspended
                    ? new { reason = project.AdminSuspensionReason, at = project.AdminSuspendedAt, supportWhatsApp = SiteDataEndpoints.SupportWhatsApp }
                    : null,
                template = templates.Get(project.TemplateKey) is { } t ? new { t.Key, t.Name, t.Modules } : null,
                files,
                features = HostingService.Features(fileMap),
                requiredTier = HostingService.RequiredTier(fileMap),
                previewBase = $"{opt.PublicUrl.TrimEnd('/')}/preview/{project.Id:N}/{previewToken}/",
                siteUrl = SiteUrl(opt, project.Slug, project.CustomDomain, project.CustomDomainVerified),
                subdomainUrl = opt.SiteUrl(project.Slug),
                siteSuffix = opt.SiteSuffix,
                cnameTarget = opt.CnameTarget,
                versions,
                messages = messages.AsEnumerable().Reverse(),
                activeTask,
                continuable
            });
        });

        g.MapGet("/{id:guid}/hints", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects, SubscriptionService subs, AiClient ai, CancellationToken ct) =>
        {
            var userId = http.User.UserId();
            var project = await projects.GetOwnedAsync(id, userId);
            if (!(await subs.GetPlanAsync(userId)).IsPro) return Results.Ok(new { hints = Array.Empty<string>() });
            if (!string.IsNullOrWhiteSpace(project.ChatHints))
                return Results.Ok(new { hints = ReadHints(project.ChatHints) });
            var lang = ProjectService.SiteLanguage(http.Request.Headers["X-Casco-Lang"].ToString()) ?? "Arabic";
            var result = await ai.CompleteAsync(AiTiers.Cheap, new ChatRequest(
            [
                new("system", """
                    You write short edit ideas for one specific business website. Return JSON only: {"hints":["..."]}.
                    Six hints. Each hint is one sentence the owner can send in the chat, in the given language, under 90 characters.
                    Ideas must fit this business (a cafe: menu, hours, reservations, the coffee; a clinic: booking, the visit). Never generic tips that fit every website.
                    """),
                new("user", $"Language: {lang}\nName: {project.Name}\nType: {project.TemplateKey}\nDescription: {project.Description}")
            ], 400), new AiCallContext(userId, project.Id, null, "hints", PlanKeys.Pro), ct);
            var hints = ReadHints(result.Content);
            if (hints.Count > 0)
            {
                project.ChatHints = JsonSerializer.Serialize(hints);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(new { hints });
        });

        g.MapPatch("/{id:guid}", async (Guid id, UpdateProjectRequest req, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            if (!string.IsNullOrWhiteSpace(req.Name)) project.Name = Text.Truncate(req.Name.Trim(), 120);
            if (!string.IsNullOrWhiteSpace(req.Description)) project.Description = Text.Truncate(req.Description.Trim(), 2000);
            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}", async (Guid id, HttpContext http, ProjectService projects, UploadService uploads, HostingService hosting,
            IOptions<AppOptions> appOpt) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            await projects.DeleteAsync(project, appOpt.Value.DataPath);
            hosting.Invalidate(project);
            await uploads.DeleteProjectAsync(project.Id);
            return Results.NoContent();
        });

        g.MapGet("/{id:guid}/versions/{versionId:guid}", async (Guid id, Guid versionId, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var version = await db.ProjectVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.ProjectId == id)
                          ?? throw ApiException.NotFound("النسخة غير موجودة");
            return Results.Ok(new
            {
                version.Id, version.Number, version.Summary, version.Prompt, version.Model, version.Parts, version.FilesChanged, version.CreatedAt,
                files = SiteFiles.Parse(version.FilesJson)
            });
        });

        g.MapPost("/{id:guid}/versions/{versionId:guid}/restore", async (Guid id, Guid versionId, HttpContext http, AppDbContext db, ProjectService projects) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            var source = await db.ProjectVersions.FirstOrDefaultAsync(v => v.Id == versionId && v.ProjectId == id)
                         ?? throw ApiException.NotFound("النسخة غير موجودة");
            var number = await db.ProjectVersions.Where(v => v.ProjectId == id).MaxAsync(v => v.Number) + 1;
            var copy = new ProjectVersion
            {
                ProjectId = id, Number = number, FilesJson = source.FilesJson, Summary = $"استرجاع النسخة {source.Number}",
                Prompt = source.Prompt, Model = source.Model, CreatedBy = http.User.UserId()
            };
            db.ProjectVersions.Add(copy);
            project.CurrentVersionId = copy.Id;
            project.UpdatedAt = DateTime.UtcNow;
            db.ChatMessages.Add(new ChatMessage { ProjectId = id, Role = "assistant", Content = $"تم استرجاع النسخة رقم {source.Number}." });
            await db.SaveChangesAsync();
            return Results.Ok(new { versionId = copy.Id, number });
        });

        g.MapPost("/{id:guid}/messages", async (Guid id, SendMessageRequest req, HttpContext http, ProjectService projects, UploadService uploads) =>
        {
            var userId = http.User.UserId();
            await projects.RememberLanguageAsync(userId, http.Request.Headers["X-Casco-Lang"].ToString());
            var project = await projects.GetOwnedAsync(id, userId);
            if (project.TemplateKey == HostingTiers.React) throw ApiException.BadRequest("تطبيق React يُدار من صفحة الرفع، وليس من محرر المواقع");
            var images = (req.Images ?? []).Distinct().Take(10).ToList();
            if (!await uploads.AreOwnerImagesAsync(id, images, http.RequestAborted)) throw ApiException.BadRequest("إحدى الصور غير موجودة، ارفعها مرة أخرى");
            var taskId = await projects.StartTaskAsync(project, userId, TaskKinds.Edit, req.Content ?? "", req.Premium, images: images, resume: req.Continue);
            return Results.Ok(new { taskId });
        }).RequireRateLimiting("ai");

        // Image library of the site owner (used in chat requests).
        g.MapGet("/{id:guid}/images", async (Guid id, HttpContext http, ProjectService projects, UploadService uploads) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(await uploads.ListOwnerImagesAsync(id, http.RequestAborted));
        });

        g.MapPost("/{id:guid}/images", async (Guid id, IFormFile file, HttpContext http, ProjectService projects, UploadService uploads) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(new { url = await uploads.SaveImageAsync(id, file, UploadService.OwnerPrefix, http.RequestAborted, allowVideo: true) });
        }).DisableAntiforgery().RequireRateLimiting("uploads")
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(UploadService.MaxVideoBytes + 1024 * 1024))
          .WithFormOptions(multipartBodyLengthLimit: UploadService.MaxVideoBytes + 1024 * 1024);

        g.MapDelete("/{id:guid}/images/{name}", async (Guid id, string name, HttpContext http, ProjectService projects, UploadService uploads) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            return await uploads.DeleteOwnerImageAsync(id, name, http.RequestAborted) ? Results.NoContent() : throw ApiException.NotFound("الصورة غير موجودة");
        });

        g.MapPost("/{id:guid}/fix", async (Guid id, FixRequest req, HttpContext http, ProjectService projects) =>
        {
            var userId = http.User.UserId();
            await projects.RememberLanguageAsync(userId, http.Request.Headers["X-Casco-Lang"].ToString());
            var project = await projects.GetOwnedAsync(id, userId);
            var errors = (req.Errors ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct().Take(12).Select(e => "- " + Text.Truncate(e, 800));
            var prompt = """
                The preview is broken. Fix these JavaScript errors in the React modules that the pages actually load.
                A blank page is a thrown exception in the module the page loads, or in a file that module imports. Open those files and fix the exception.
                "Failed to load" with an HTTP status means that file was not found. A file that already downloaded is not missing: do not rewrite it for a download error.
                Do not ask which page. Do not edit a script no page loads. Rewrite the fixed file with a newline after every statement, and keep the design:
                """ + "\n" + string.Join("\n", errors);
            var taskId = await projects.StartTaskAsync(project, userId, TaskKinds.Fix, prompt, req.Premium,
                chatMessage: "لقيت خطأ في الصفحة، هصلحه.");
            return Results.Ok(new { taskId });
        }).RequireRateLimiting("ai");

        g.MapGet("/{id:guid}/export", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects, SubscriptionService subs) =>
        {
            var userId = http.User.UserId();
            var project = await projects.GetOwnedAsync(id, userId);
            if (!(await subs.GetPlanAsync(userId)).IsPro) throw ApiException.Payment("تحميل ملفات الموقع متاح لمشتركي Pro");
            var version = await db.ProjectVersions.FindAsync(project.CurrentVersionId) ?? throw ApiException.NotFound();
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (path, content) in SiteFiles.Parse(version.FilesJson))
                {
                    await using var w = new StreamWriter(zip.CreateEntry(path).Open());
                    await w.WriteAsync(content);
                }
            }
            ms.Position = 0;
            return Results.File(ms, "application/zip", $"{project.Slug}.zip");
        });

        app.MapGet("/api/tasks/{id:guid}", async (Guid id, HttpContext http, AppDbContext db) =>
        {
            var userId = http.User.UserId();
            var task = await db.AgentTasks.Where(t => t.Id == id && t.UserId == userId)
                .Select(t => new { t.Id, t.ProjectId, t.Kind, t.Status, t.Error, t.CreditsCharged, t.ResultVersionId, t.CreatedAt, t.StartedAt, t.CompletedAt })
                .FirstOrDefaultAsync() ?? throw ApiException.NotFound("المهمة غير موجودة");
            return Results.Ok(task);
        }).RequireAuthorization();
    }

    private static List<string> ReadHints(string json)
    {
        try
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            var slice = start >= 0 && end > start ? json[start..(end + 1)] : json;
            using var doc = JsonDocument.Parse(slice);
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement
                : doc.RootElement.TryGetProperty("hints", out var hints) ? hints : default;
            if (arr.ValueKind != JsonValueKind.Array) return [];
            return arr.EnumerateArray().Select(x => (x.GetString() ?? "").Trim()).Where(s => s.Length > 1).Take(8).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string SiteUrl(AppOptions opt, string slug, string? customDomain, bool verified) =>
        verified && !string.IsNullOrEmpty(customDomain) ? $"https://{customDomain}" : opt.SiteUrl(slug);
}
