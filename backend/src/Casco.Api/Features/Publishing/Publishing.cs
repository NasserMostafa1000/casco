using System.Net;
using System.Text;
using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.Admin;
using Casco.Api.Features.Auth;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Projects;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Publishing;

public static class HtmlInjector
{
    private const string ErrorReporter = """
<script>(function(){function send(m){try{parent.postMessage({type:"casco-error",message:String(m).slice(0,800)},"*")}catch(e){}}
window.addEventListener("error",function(e){var t=e.target;if(t&&t.tagName==="SCRIPT"&&t.src){if(e.message){send(e.message+" @ "+t.src.split("/").pop());return}var src=t.src;setTimeout(function(){fetch(src).then(function(r){if(!r.ok)send("Failed to load "+src.split("/").pop()+" (HTTP "+r.status+")")}).catch(function(){send("Failed to load "+src.split("/").pop())})},400);return}if(t&&t.tagName)return;if(e.message)send(e.message+(e.filename?" @ "+e.filename.split("/").pop()+":"+e.lineno:""))},true);
window.addEventListener("unhandledrejection",function(e){var r=e.reason;if(r&&r.status)return;send("Unhandled promise: "+(r&&(r.stack||r.message)||r))});
var orig=console.error;console.error=function(){try{var m=Array.prototype.map.call(arguments,function(a){return a&&a.stack?a.stack:String(a)}).join(" ");if(/Minified React|TypeError|SyntaxError|ReferenceError|is not a function|is not defined|Uncaught|Invalid/i.test(m))send(m)}catch(x){}return orig.apply(console,arguments)};
function blank(){var root=document.getElementById("root");if(!root)return;var mod=document.querySelector("script[type=module][src]");if(!mod)return;if(root.childElementCount>0||(root.textContent||"").trim())return;var page=location.pathname.split("/").pop()||"index.html";send("Blank page "+page+". Module "+(mod.getAttribute("src")||"")+" did not render into #root. Fix that module and the files it imports.")}
function watch(){var mod=document.querySelector("script[type=module][src]");if(mod&&!mod.__casco){mod.__casco=1;mod.addEventListener("load",function(){setTimeout(blank,2000)})}}
document.addEventListener("DOMContentLoaded",function(){watch();setTimeout(blank,2000)});
window.addEventListener("load",function(){watch();setTimeout(blank,4000)});
document.addEventListener("click",function(e){var a=e.target.closest&&e.target.closest("a[href]");if(a){try{parent.postMessage({type:"casco-nav",href:a.getAttribute("href")},"*")}catch(x){}}},true);})();</script>
""";

    private const string Badge = """
<a href="{{HOME}}" target="_blank" rel="noopener" style="position:fixed;bottom:14px;left:14px;z-index:2147483647;background:#111827;color:#fff;font:600 12px system-ui,sans-serif;padding:8px 12px;border-radius:999px;text-decoration:none;box-shadow:0 4px 14px rgba(0,0,0,.25)">⚡ صُنع بواسطة Casco</a>
""";

    public static string Inject(string html, string apiBase, string siteKey, bool preview, bool badge, string? homeUrl = null)
    {
        var config = JsonSerializer.Serialize(new { api = apiBase.TrimEnd('/'), siteKey, preview });
        var head = $"<script>window.__CASCO__={config};</script>\n<script src=\"{WebUtility.HtmlEncode(apiBase.TrimEnd('/'))}/sdk/casco.js?v=2\"></script>\n";
        if (preview) head = "<meta name=\"robots\" content=\"noindex, nofollow\">\n" + ErrorReporter + "\n" + head;

        var headClose = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        html = headClose >= 0 ? html.Insert(headClose, head) : head + html;

        if (badge)
        {
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            var badgeHtml = Badge.Replace("{{HOME}}", WebUtility.HtmlEncode((homeUrl ?? apiBase).TrimEnd('/')));
            html = bodyClose >= 0 ? html.Insert(bodyClose, badgeHtml) : html + badgeHtml;
        }
        return html;
    }
}

/// <param name="Active">False once the site's hosting has lapsed beyond the grace period, or while an admin suspends it.</param>
/// <param name="Spa">A React build: unknown paths without an extension fall back to index.html.</param>
public record SiteResolution(Guid ProjectId, string Directory, bool Active, bool Spa);

public class SiteResolver(IServiceScopeFactory scopes, IMemoryCache cache, IOptions<AppOptions> options, IOptions<BillingOptions> billing)
{
    private readonly AppOptions _opt = options.Value;

    public string SiteDirectory(Guid projectId) => Path.GetFullPath(Path.Combine(_opt.DataPath, "sites", projectId.ToString("N")));

    public bool IsPlatformHost(string host) =>
        _opt.Hosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    public async Task<SiteResolution?> ResolveAsync(string host)
    {
        host = host.ToLowerInvariant();
        var key = "site-host:" + host;
        if (cache.TryGetValue(key, out SiteResolution? cached)) return cached;

        SiteResolution? result = null;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var published = db.Projects.Where(p => p.PublishedAt != null);
            var suffix = "." + _opt.SitesDomain.ToLowerInvariant();
            var slug = host.EndsWith(suffix) ? host[..^suffix.Length] : null;
            var match = await published
                .Where(p => (slug != null && p.Slug == slug) || (p.CustomDomain == host && p.CustomDomainVerified))
                .Select(p => new { p.Id, p.HostingTier, p.HostingPaidUntil, p.IsAdminSuspended, p.TemplateKey })
                .FirstOrDefaultAsync();
            if (match is not null)
            {
                var active = !match.IsAdminSuspended && HostingTiers.Rank(match.HostingTier) > 0 && match.HostingPaidUntil is { } end
                             && end.AddDays(billing.Value.Hosting.GraceDaysFor(match.HostingTier)) > DateTime.UtcNow;
                result = new SiteResolution(match.Id, SiteDirectory(match.Id), active, match.TemplateKey == HostingTiers.React);
            }
        }
        cache.Set(key, result, TimeSpan.FromSeconds(result is null ? 15 : 60));
        return result;
    }

    public void Invalidate(params string?[] hosts)
    {
        foreach (var h in hosts.Where(h => !string.IsNullOrEmpty(h))) cache.Remove("site-host:" + h!.ToLowerInvariant());
    }
}

/// <summary>Serves published customer sites for any host that is not the platform itself.</summary>
public class SiteHostingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, SiteResolver resolver)
    {
        var host = ctx.Request.Host.Host;
        if (resolver.IsPlatformHost(host))
        {
            await next(ctx);
            return;
        }

        var site = await resolver.ResolveAsync(host);
        if (site is null)
        {
            // Unknown host: let platform-wide routes (SDK, uploads, runtime API) work, everything else is 404.
            var path = ctx.Request.Path.Value ?? "";
            if (path.StartsWith("/sdk/") || path.StartsWith("/u/") || path.StartsWith("/api/s/") || path.StartsWith("/internal/"))
            {
                await next(ctx);
                return;
            }
            ctx.Response.StatusCode = 404;
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(NotFoundPage);
            return;
        }

        var requested = Uri.UnescapeDataString(ctx.Request.Path.Value ?? "/").TrimStart('/');
        if (requested.StartsWith("sdk/") || requested.StartsWith("u/") || requested.StartsWith("api/s/"))
        {
            await next(ctx);
            return;
        }

        if (!site.Active)
        {
            ctx.Response.StatusCode = 503;
            ctx.Response.Headers.RetryAfter = "3600";
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.WriteAsync(PausedPage);
            return;
        }

        var file = FindFile(site.Directory, requested);
        if (file is null && requested.Equals("robots.txt", StringComparison.OrdinalIgnoreCase))
        {
            var origin = $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}";
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            await ctx.Response.WriteAsync($"User-agent: *\nAllow: /\n\nSitemap: {origin}/sitemap.xml\n");
            return;
        }
        if (file is null && requested.Equals("sitemap.xml", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.ContentType = "application/xml; charset=utf-8";
            ctx.Response.Headers.CacheControl = "public, max-age=3600";
            await ctx.Response.WriteAsync(Sitemap(site.Directory, $"{ctx.Request.Scheme}://{ctx.Request.Host.Value}"));
            return;
        }
        if (file is null && site.Spa && !Path.HasExtension(requested))
            file = FindFile(site.Directory, "index.html");
        if (file is null)
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            file = FindFile(site.Directory, "404.html");
            if (file is null)
            {
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.WriteAsync(NotFoundPage);
                return;
            }
        }

        ctx.Response.ContentType = SiteFiles.ContentType(file);
        ctx.Response.Headers.CacheControl = SiteFiles.IsHtml(file) ? "public, max-age=60" : "public, max-age=3600";
        ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await ctx.Response.SendFileAsync(file);
    }

    private static string Sitemap(string root, string origin)
    {
        var urls = new StringBuilder();
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.html", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.StartsWith('.') || rel.Contains("/.", StringComparison.Ordinal) || rel.Equals("404.html", StringComparison.OrdinalIgnoreCase)) continue;
                var loc = rel.Equals("index.html", StringComparison.OrdinalIgnoreCase) ? origin + "/" : origin + "/" + rel;
                urls.Append("  <url><loc>").Append(WebUtility.HtmlEncode(loc)).Append("</loc></url>\n");
            }
        }
        return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n{urls}</urlset>\n";
    }

    private static string? FindFile(string root, string requested)
    {
        if (string.IsNullOrEmpty(requested) || requested.EndsWith('/')) requested += "index.html";
        foreach (var candidate in new[] { requested, requested + ".html", requested.TrimEnd('/') + "/index.html" })
        {
            var full = Path.GetFullPath(Path.Combine(root, candidate));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private const string NotFoundPage = """
<!doctype html><html lang="ar" dir="rtl"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex, nofollow"><title>الصفحة غير موجودة</title><body style="font-family:system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0;background:#f8fafc;color:#0f172a">
<div style="text-align:center"><h1 style="font-size:48px;margin:0">404</h1><p>الصفحة أو الموقع غير موجود.</p></div></body></html>
""";

    private const string PausedPage = """
<!doctype html><html lang="ar" dir="rtl"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="robots" content="noindex, nofollow"><title>الموقع متوقف مؤقتاً</title><body style="font-family:system-ui,sans-serif;display:grid;place-items:center;min-height:100vh;margin:0;background:#f8fafc;color:#0f172a">
<div style="text-align:center;padding:24px"><h1 style="font-size:28px;margin:0 0 8px">الموقع متوقف مؤقتاً</h1><p style="color:#475569">سيعود الموقع للعمل قريباً.</p></div></body></html>
""";
}

public class PublishService(AppDbContext db, SiteResolver resolver, SubscriptionService subscriptions, HostingService hosting, IOptions<AppOptions> options,
    ILogger<PublishService> logger)
{
    private readonly AppOptions _opt = options.Value;

    public async Task<string> PublishAsync(Project project)
    {
        SiteSuspension.EnsureNotSuspended(project);
        if (project.TemplateKey == HostingTiers.React)
            throw ApiException.BadRequest("تطبيق React يُنشر برفع ملف dist من صفحة الاستضافة بعد دفع الاشتراك السنوي.");
        var version = await db.ProjectVersions.FindAsync(project.CurrentVersionId) ?? throw ApiException.BadRequest("لا توجد نسخة للنشر");
        var files = SiteFiles.Parse(version.FilesJson);
        var required = HostingService.RequiredTier(files);
        if (!hosting.IsActive(project, required)) throw hosting.HostingRequired(required);
        var plan = await subscriptions.GetPlanAsync(project.UserId);

        var root = Path.GetFullPath(Path.Combine(_opt.DataPath, "sites"));
        Directory.CreateDirectory(root);
        var final = resolver.SiteDirectory(project.Id);
        var temp = Path.Combine(root, $".tmp-{project.Id:N}-{Text.RandomToken(4)}");
        Directory.CreateDirectory(temp);

        foreach (var (path, content) in files)
        {
            if (Backend.BackendConfig.IsBackendFile(path)) continue;
            var target = Path.GetFullPath(Path.Combine(temp, path));
            if (!target.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var output = SiteFiles.IsHtml(path)
                ? HtmlInjector.Inject(content, _opt.PublicUrl, project.SiteKey, preview: false, badge: plan.ShowBadge, homeUrl: _opt.FrontendBase)
                : content;
            await File.WriteAllTextAsync(target, output);
        }

        // Swap directories so visitors never see a half-written site.
        string? old = null;
        if (Directory.Exists(final))
        {
            old = Path.Combine(root, $".old-{project.Id:N}-{Text.RandomToken(4)}");
            Directory.Move(final, old);
        }
        Directory.Move(temp, final);
        if (old is not null) await DirectoryCleanup.DeleteAsync(old, logger);

        project.PublishedVersionId = version.Id;
        project.PublishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        hosting.Invalidate(project);
        return ProjectEndpoints.SiteUrl(_opt, project.Slug, project.CustomDomain, project.CustomDomainVerified);
    }

    public async Task UnpublishAsync(Project project)
    {
        project.PublishedAt = null;
        project.PublishedVersionId = null;
        await db.SaveChangesAsync();
        await DirectoryCleanup.DeleteAsync(resolver.SiteDirectory(project.Id), logger);
        hosting.Invalidate(project);
    }
}

public record SlugRequest(string Slug);
public record DomainRequest(string? Domain);

public static class PublishingEndpoints
{
    public static void MapPublishingEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/projects/{id:guid}").RequireAuthorization();

        g.MapPost("/publish", async (Guid id, HttpContext http, ProjectService projects, PublishService publisher) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            return Results.Ok(new { url = await publisher.PublishAsync(project) });
        });

        g.MapGet("/hosting", async (Guid id, HttpContext http, ProjectService projects, HostingService hosting) =>
            Results.Ok(await hosting.StatusAsync(await projects.GetOwnedAsync(id, http.User.UserId()))));

        g.MapPost("/unpublish", async (Guid id, HttpContext http, ProjectService projects, PublishService publisher) =>
        {
            await publisher.UnpublishAsync(await projects.GetOwnedAsync(id, http.User.UserId()));
            return Results.NoContent();
        });

        // The editor checks the name as the owner types it on first publish: {name}.{SitesDomain}.
        g.MapGet("/slug/check", async (Guid id, string? slug, HttpContext http, AppDbContext db, ProjectService projects, IOptions<AppOptions> appOpt) =>
        {
            await projects.GetOwnedAsync(id, http.User.UserId());
            var s = (slug ?? "").Trim().ToLowerInvariant();
            var (code, message) = await SlugProblemAsync(db, id, s);
            var suggestion = code is "slug_taken" or "slug_reserved" ? await SuggestSlugAsync(db, id, s) : null;
            return Results.Ok(new { slug = s, available = code is null, code, message, url = appOpt.Value.SiteUrl(s), suggestion });
        });

        g.MapPut("/slug", async (Guid id, SlugRequest req, HttpContext http, AppDbContext db, ProjectService projects,
            SiteResolver resolver, IOptions<AppOptions> appOpt) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            var slug = (req.Slug ?? "").Trim().ToLowerInvariant();
            var (code, message) = await SlugProblemAsync(db, id, slug);
            if (code is not null) throw code == "slug_taken" ? ApiException.Conflict(message!, code) : ApiException.BadRequest(message!, code);
            var oldHost = $"{project.Slug}.{appOpt.Value.SitesDomain}";
            project.Slug = slug;
            await db.SaveChangesAsync();
            resolver.Invalidate(oldHost, $"{slug}.{appOpt.Value.SitesDomain}");
            return Results.Ok(new { slug, url = appOpt.Value.SiteUrl(slug) });
        });

        g.MapPut("/domain", async (Guid id, DomainRequest req, HttpContext http, AppDbContext db, ProjectService projects,
            HostingService hosting, SiteResolver resolver) =>
        {
            var userId = http.User.UserId();
            var project = await projects.GetOwnedAsync(id, userId);
            var old = project.CustomDomain;
            var domain = req.Domain?.Trim().ToLowerInvariant().Replace("https://", "").Replace("http://", "").TrimEnd('/');
            if (string.IsNullOrEmpty(domain))
            {
                project.CustomDomain = null;
                project.CustomDomainVerified = false;
            }
            else
            {
                if (!hosting.IsActive(project)) throw hosting.HostingRequired(await hosting.RequiredTierAsync(project));
                if (Uri.CheckHostName(domain) != UriHostNameType.Dns || !domain.Contains('.'))
                    throw ApiException.BadRequest("اسم الدومين غير صالح");
                if (await db.Projects.AnyAsync(p => p.CustomDomain == domain && p.Id != id)) throw ApiException.Conflict("هذا الدومين مربوط بموقع آخر");
                project.CustomDomain = domain;
                project.CustomDomainVerified = false;
            }
            await db.SaveChangesAsync();
            resolver.Invalidate(old, domain);
            return Results.Ok(new { project.CustomDomain, project.CustomDomainVerified });
        });

        g.MapPost("/domain/verify", async (Guid id, HttpContext http, AppDbContext db, ProjectService projects,
            SiteResolver resolver, IOptions<AppOptions> appOpt) =>
        {
            var project = await projects.GetOwnedAsync(id, http.User.UserId());
            if (string.IsNullOrEmpty(project.CustomDomain)) throw ApiException.BadRequest("أضف الدومين أولاً");
            var opt = appOpt.Value;

            var expected = new HashSet<string>(opt.ServerIps);
            try
            {
                foreach (var ip in await Dns.GetHostAddressesAsync(opt.CnameTarget)) expected.Add(ip.ToString());
            }
            catch (Exception) { /* CNAME target may not resolve in development */ }

            string[] actual;
            try { actual = (await Dns.GetHostAddressesAsync(project.CustomDomain)).Select(a => a.ToString()).ToArray(); }
            catch (Exception) { actual = []; }

            project.CustomDomainVerified = actual.Length > 0 && actual.Any(expected.Contains);
            await db.SaveChangesAsync();
            resolver.Invalidate(project.CustomDomain);
            return Results.Ok(new
            {
                verified = project.CustomDomainVerified,
                message = project.CustomDomainVerified
                    ? "تم ربط الدومين بنجاح. قد يستغرق تفعيل شهادة الأمان دقيقة."
                    : $"لم نجد إعدادات DNS الصحيحة بعد. أضف سجل CNAME يشير إلى {opt.CnameTarget} (أو سجل A إلى {string.Join(", ", opt.ServerIps)}) ثم أعد المحاولة. قد يستغرق انتشار DNS حتى ساعة."
            });
        });

        // Caddy on-demand TLS asks here before issuing a certificate for a hostname.
        app.MapGet("/internal/caddy/ask", async (string domain, SiteResolver resolver) =>
            await resolver.ResolveAsync(domain) is null ? Results.NotFound() : Results.Ok()).DisableRateLimiting();

        app.MapGet("/preview/{projectId:guid}/{token}/{**path}", async (Guid projectId, string token, string? path, string? v,
            AppDbContext db, TokenService tokens, IOptions<AppOptions> appOpt, HttpContext http) =>
        {
            if (!tokens.ValidatePreviewToken(projectId, token)) return Results.Text("رابط المعاينة منتهي، أعد تحميل الصفحة", statusCode: 403);
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
            if (project is null) return Results.NotFound();

            // v is a version id on the page URL. Asset URLs often use ?v=3 as a cache buster, which must not reject the file.
            var versionId = Guid.TryParse(v, out var parsedVersion) ? parsedVersion : project.CurrentVersionId;
            var version = await db.ProjectVersions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == versionId && x.ProjectId == projectId);
            if (version is null) return Results.NotFound();

            var files = SiteFiles.Parse(version.FilesJson);
            var requested = string.IsNullOrEmpty(path) ? "index.html" : path;
            if (Backend.BackendConfig.IsBackendFile(requested)) return Results.NotFound();
            if (!files.TryGetValue(requested, out var content) && !files.TryGetValue(requested + ".html", out content))
                return Results.Text("<!doctype html><meta charset=utf-8><body dir=rtl style='font-family:sans-serif;padding:40px'>هذه الصفحة غير موجودة في الموقع بعد.</body>", "text/html; charset=utf-8", statusCode: 404);

            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            // Previews live on the app origin: keep them sandboxed even when opened outside the editor iframe.
            // allow-same-origin so React module scripts can load. The preview host is not the app host, and the API does not use cookies.
            http.Response.Headers.ContentSecurityPolicy = "sandbox allow-scripts allow-forms allow-popups allow-modals allow-same-origin";
            if (SiteFiles.IsHtml(requested) || !requested.Contains('.'))
            {
                var html = HtmlInjector.Inject(content, appOpt.Value.PublicUrl, project.SiteKey, preview: true, badge: false);
                return Results.Text(html, "text/html; charset=utf-8");
            }
            return Results.Text(content, SiteFiles.ContentType(requested));
        });
    }

    private static async Task<(string? Code, string? Message)> SlugProblemAsync(AppDbContext db, Guid projectId, string slug)
    {
        if (Text.ReservedSlugs.Contains(slug)) return ("slug_reserved", "هذا الاسم محجوز، اختر اسماً آخر");
        if (!Text.IsValidSlug(slug)) return ("slug_invalid", "الرابط يجب أن يكون 3-40 حرفاً إنجليزياً صغيراً أو أرقاماً أو شرطة (-)");
        if (await db.Projects.AnyAsync(p => p.Slug == slug && p.Id != projectId)) return ("slug_taken", "هذا الرابط مستخدم، اختر غيره");
        return (null, null);
    }

    private static async Task<string> SuggestSlugAsync(AppDbContext db, Guid projectId, string slug)
    {
        var baseSlug = Text.Slugify(slug);
        if (baseSlug.Length > 34) baseSlug = baseSlug[..34].Trim('-');
        if (baseSlug.Length < 3) baseSlug = "site";
        var candidates = Enumerable.Range(2, 8).Select(i => $"{baseSlug}-{i}").Append($"{baseSlug}-{Text.RandomToken(2)}").ToList();
        var taken = await db.Projects.Where(p => candidates.Contains(p.Slug) && p.Id != projectId).Select(p => p.Slug).ToListAsync();
        return candidates.First(c => !taken.Contains(c) && Text.IsValidSlug(c));
    }
}
