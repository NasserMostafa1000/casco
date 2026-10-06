using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics;

namespace Casco.Api.Infrastructure;

public class ApiException(int status, string message, string? code = null) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
    /// <summary>Extra machine-readable details returned to the client (e.g. the hosting tier a publish needs).</summary>
    public object? Details { get; init; }

    public static ApiException NotFound(string message = "غير موجود") => new(404, message, "not_found");
    public static ApiException BadRequest(string message, string? code = null) => new(400, message, code ?? "bad_request");
    public static ApiException Forbidden(string message = "غير مسموح") => new(403, message, "forbidden");
    public static ApiException Payment(string message, string code = "upgrade_required") => new(402, message, code);
    public static ApiException Conflict(string message, string? code = null) => new(409, message, code ?? "conflict");
}

public static class DirectoryCleanup
{
    /// <summary>
    /// Best-effort recursive delete. Indexers/antivirus on Windows can hold just-written files or folders
    /// for a few seconds; the caller's database change has already happened, so this never throws for IO errors.
    /// </summary>
    public static async Task<bool> DeleteAsync(string dir, ILogger? logger = null, CancellationToken ct = default)
    {
        for (var attempt = 1; Directory.Exists(dir); attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < 4)
                {
                    await Task.Delay(150 * attempt, ct);
                    continue;
                }
                var filesLeft = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count() : 0;
                if (filesLeft > 0)
                    logger?.LogError(ex, "Could not delete {Count} file(s) under {Dir}; remove them manually", filesLeft, dir);
                else
                    logger?.LogWarning("Folder {Dir} is empty but locked; leaving it for later cleanup ({Error})", dir, ex.Message);
                return false;
            }
        }
        return true;
    }
}

public static class ErrorHandling
{
    public static void UseApiErrors(this WebApplication app)
    {
        app.UseExceptionHandler(errorApp => errorApp.Run(async ctx =>
        {
            var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, message, code) = ex switch
            {
                ApiException api => (api.Status, api.Message, api.Code),
                BadHttpRequestException bad => (400, bad.Message, "bad_request"),
                _ => (500, "حدث خطأ غير متوقع", "server_error")
            };
            if (status == 500)
                ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Errors")
                    .LogError(ex, "Unhandled error on {Method} {Host}{Path}", ctx.Request.Method, ctx.Request.Host.Value, ctx.Request.Path.Value);
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsJsonAsync(new { message, code });
        }));

        // Expected client errors are answered here so the exception handler above doesn't log them as failures.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (ApiException api) when (!ctx.Response.HasStarted)
            {
                ctx.Response.Clear();
                ctx.Response.StatusCode = api.Status;
                await ctx.Response.WriteAsJsonAsync(new { message = api.Message, code = api.Code, details = api.Details });
            }
        });
    }
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal user)
    {
        var id = user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
        return Guid.TryParse(id, out var g) ? g : throw new ApiException(401, "غير مسجل الدخول", "unauthorized");
    }

    public static bool IsAdmin(this ClaimsPrincipal user) => user.IsInRole(Domain.Roles.Admin);
}

public static partial class Text
{
    [GeneratedRegex("[^a-z0-9-]+")] private static partial Regex NonSlugChars();
    [GeneratedRegex("-{2,}")] private static partial Regex MultiDash();

    public static readonly HashSet<string> ReservedSlugs =
        ["www", "app", "api", "admin", "mail", "preview", "sites", "static", "cdn", "casco", "support", "help", "billing", "dashboard",
         "smtp", "imap", "pop", "ftp", "ns1", "ns2", "mx", "webmail", "email", "auth", "login", "signup", "register", "account",
         "pay", "payment", "payments", "checkout", "webhook", "webhooks", "status", "docs", "blog", "assets", "media", "uploads",
         "dev", "test", "staging", "beta", "internal", "root", "security", "abuse", "legal", "privacy", "terms"];

    public static string Slugify(string input)
    {
        var s = NonSlugChars().Replace(input.Trim().ToLowerInvariant(), "-");
        s = MultiDash().Replace(s, "-").Trim('-');
        if (s.Length > 40) s = s[..40].Trim('-');
        return s;
    }

    public static bool IsValidSlug(string slug) =>
        slug.Length is >= 3 and <= 40 && Slugify(slug) == slug && !ReservedSlugs.Contains(slug);

    public static string RandomToken(int bytes = 12) =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

    public static string Sha256Hex(string input) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max];
}
