using System.IO.Compression;
using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Projects;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Publishing;

/// <summary>Unpacks a React production build (the dist folder, as a zip) onto a paid yearly host.</summary>
public static class ReactDist
{
    public const long MaxZipBytes = 30L * 1024 * 1024;
    public const long MaxUnpackedBytes = 80L * 1024 * 1024;

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".js", ".mjs", ".css", ".json", ".map", ".txt", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico",
        ".woff", ".woff2", ".ttf", ".eot", ".wasm", ".xml", ".webmanifest", ".mp4", ".webm"
    };

    /// <summary>Writes the zip into <paramref name="destination"/> (created empty). Returns the file count.</summary>
    public static int Extract(Stream zipStream, string destination)
    {
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        var files = new List<(ZipArchiveEntry Entry, string Path)>();
        foreach (var entry in zip.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var path = entry.FullName.Replace('\\', '/').TrimStart('/');
            if (path.Split('/').Any(p => p.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase) || p.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) || p.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) || p.StartsWith("._", StringComparison.Ordinal)))
                continue;
            if (path.Length == 0 || path.Split('/').Any(p => p is "" or "." or ".."))
                throw ApiException.BadRequest("ملف داخل الأرشيف بمسار غير مسموح");
            files.Add((entry, path));
        }
        if (files.Count == 0) throw ApiException.BadRequest("الأرشيف فاضي");
        if (files.Count > 2000) throw ApiException.BadRequest("الأرشيف فيه ملفات أكتر من المسموح");

        var prefix = "";
        if (!files.Any(f => f.Path.Equals("index.html", StringComparison.OrdinalIgnoreCase)))
        {
            var root = files.Select(f => f.Path.Split('/')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (root.Count == 1 && files.Any(f => f.Path.Equals(root[0] + "/index.html", StringComparison.OrdinalIgnoreCase)))
                prefix = root[0] + "/";
        }
        if (!files.Any(f => f.Path.Equals(prefix + "index.html", StringComparison.OrdinalIgnoreCase)))
            throw ApiException.BadRequest("ملف dist لازم يحتوي على index.html. اضغط مجلد dist نفسه (أو محتوياته) في ملف zip.");

        Directory.CreateDirectory(destination);
        long total = 0;
        foreach (var (entry, path) in files)
        {
            var relative = path[prefix.Length..];
            var ext = Path.GetExtension(relative);
            if (!Allowed.Contains(ext)) throw ApiException.BadRequest($"نوع الملف غير مسموح في تطبيق React: {relative}");
            total += entry.Length;
            if (total > MaxUnpackedBytes) throw ApiException.BadRequest("حجم ملفات dist أكبر من 80 ميجابايت");
            var target = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.Ordinal) && target != destination)
                throw ApiException.BadRequest("ملف داخل الأرشيف بمسار غير مسموح");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
        return files.Count;
    }
}

public static class ReactHostEndpoints
{
    public static void MapReactHost(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects").RequireAuthorization();

        g.MapPost("/react", async (ReactCreateRequest req, HttpContext http, ProjectService projects, IOptions<AppOptions> appOpt) =>
        {
            var project = await projects.CreateReactAsync(http.User.UserId(), req.Name);
            return Results.Ok(new { id = project.Id, slug = project.Slug, url = appOpt.Value.SiteUrl(project.Slug) });
        });

        g.MapGet("/{id:guid}/react", async (Guid id, HttpContext http, ProjectService projects, HostingService hosting, IOptions<AppOptions> appOpt, IOptions<BillingOptions> billing) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            if (project.TemplateKey != HostingTiers.React) throw ApiException.NotFound("هذا ليس تطبيق React");
            var now = DateTime.UtcNow;
            var until = project.HostingPaidUntil;
            var active = hosting.IsActive(project, HostingTiers.React);
            var daysLeft = until is { } end && end > now ? (int)Math.Ceiling((end - now).TotalDays) : 0;
            return Results.Ok(new
            {
                project.Id,
                project.Name,
                project.Slug,
                project.PublishedAt,
                paidUntil = until,
                daysLeft,
                active,
                stopped = project.PublishedAt != null && !active,
                price = billing.Value.Hosting.ReactYearlyMinor / 100m,
                siteUrl = project.PublishedAt is null ? null : ProjectEndpoints.SiteUrl(appOpt.Value, project.Slug, project.CustomDomain, project.CustomDomainVerified),
                subdomainUrl = appOpt.Value.SiteUrl(project.Slug)
            });
        });

        g.MapPost("/{id:guid}/react/dist", async (Guid id, IFormFile file, HttpContext http, ProjectService projects, HostingService hosting,
            SiteResolver resolver, AppDbContext db, IOptions<AppOptions> appOpt, ILoggerFactory logs) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            if (project.TemplateKey != HostingTiers.React) throw ApiException.BadRequest("هذا ليس تطبيق React");
            if (!hosting.IsActive(project, HostingTiers.React)) throw hosting.HostingRequired(HostingTiers.React);
            if (file is null || file.Length == 0) throw ApiException.BadRequest("اختر ملف zip لمجلد dist");
            if (file.Length > ReactDist.MaxZipBytes) throw ApiException.BadRequest("حجم الملف يجب ألا يتجاوز 30 ميجابايت");

            var logger = logs.CreateLogger("ReactDist");
            var root = Path.GetFullPath(Path.Combine(appOpt.Value.DataPath, "sites"));
            Directory.CreateDirectory(root);
            var final = resolver.SiteDirectory(project.Id);
            var temp = Path.Combine(root, $".tmp-{project.Id:N}-{Text.RandomToken(4)}");
            try
            {
                await using var stream = file.OpenReadStream();
                ReactDist.Extract(stream, temp);
                string? old = null;
                if (Directory.Exists(final))
                {
                    old = Path.Combine(root, $".old-{project.Id:N}-{Text.RandomToken(4)}");
                    Directory.Move(final, old);
                }
                Directory.Move(temp, final);
                if (old is not null) await DirectoryCleanup.DeleteAsync(old, logger);
            }
            catch
            {
                await DirectoryCleanup.DeleteAsync(temp, logger);
                throw;
            }

            project.PublishedAt ??= DateTime.UtcNow;
            project.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            hosting.Invalidate(project);
            return Results.Ok(new { url = ProjectEndpoints.SiteUrl(appOpt.Value, project.Slug, project.CustomDomain, project.CustomDomainVerified) });
        }).DisableAntiforgery().RequireRateLimiting("uploads")
          .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(ReactDist.MaxZipBytes + 1024 * 1024))
          .WithFormOptions(multipartBodyLengthLimit: ReactDist.MaxZipBytes + 1024 * 1024);
    }
}

public record ReactCreateRequest(string Name);
