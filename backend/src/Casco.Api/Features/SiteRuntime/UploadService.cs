using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.SiteRuntime;

public class UploadService(IUploadStorage storage, IOptions<AppOptions> options, CloudflareCachePurge? cdn = null)
{
    public const long MaxBytes = 3 * 1024 * 1024;
    private readonly AppOptions _opt = options.Value;

    public const int MaxFilesPerProject = 500;
    /// <summary>File-name prefix of images uploaded by the site owner (vs. images uploaded by site visitors).</summary>
    public const string OwnerPrefix = "o-";

    public string PublicPrefix(Guid projectId)
    {
        var key = ProjectPrefix(projectId);
        return storage.PublicObjectUrl(key) ?? $"{_opt.PublicUrl.TrimEnd('/')}/u/{projectId:N}/";
    }

    private static string ProjectPrefix(Guid projectId) => projectId.ToString("N") + "/";

    private static bool IsSafeName(string name) =>
        name.Length is > 0 and <= 100 && name.IndexOfAny(['/', '\\']) < 0 && !name.Contains("..");

    /// <summary>Owner images of a project, newest first.</summary>
    public async Task<List<string>> ListOwnerImagesAsync(Guid projectId, CancellationToken ct = default) =>
        (await storage.ListAsync(ProjectPrefix(projectId), ct))
            .Where(o => o.Name.StartsWith(OwnerPrefix, StringComparison.Ordinal))
            .OrderByDescending(o => o.LastModified)
            .Select(o => PublicPrefix(projectId) + o.Name)
            .ToList();

    public async Task<bool> AreOwnerImagesAsync(Guid projectId, IReadOnlyCollection<string> urls, CancellationToken ct = default)
    {
        if (urls.Count == 0) return true;
        var owned = (await ListOwnerImagesAsync(projectId, ct)).ToHashSet(StringComparer.Ordinal);
        return urls.All(owned.Contains);
    }

    public async Task<bool> DeleteOwnerImageAsync(Guid projectId, string name, CancellationToken ct = default)
    {
        if (!name.StartsWith(OwnerPrefix, StringComparison.Ordinal) || !IsSafeName(name)) return false;
        var key = ProjectPrefix(projectId) + name;
        if (!await storage.ExistsAsync(key, ct)) return false;
        await storage.DeleteAsync(key, ct);
        return true;
    }

    public async Task DeleteProjectAsync(Guid projectId, CancellationToken ct = default)
    {
        var prefix = ProjectPrefix(projectId);
        var names = await storage.ListAsync(prefix, ct);
        await storage.DeletePrefixAsync(prefix, ct);
        if (cdn is null) return;
        var files = names.Select(o => storage.PublicObjectUrl(prefix + o.Name)).OfType<string>().ToList();
        await cdn.PurgeAsync(storage.PublicObjectUrl(prefix), files, ct);
    }

    /// <summary>Stores an image (or, when allowed, a video) after checking its magic bytes (the declared content type is not trusted).</summary>
    public async Task<string> SaveImageAsync(Guid projectId, IFormFile file, string namePrefix = "", CancellationToken ct = default, bool allowVideo = false)
    {
        if (file.Length == 0) throw ApiException.BadRequest("الملف فارغ");
        if (file.Length > (allowVideo ? MaxVideoBytes : MaxBytes))
            throw ApiException.BadRequest(allowVideo ? "حجم الفيديو يجب ألا يتجاوز 50 ميجابايت" : "حجم الصورة يجب ألا يتجاوز 3 ميجابايت");
        if ((await storage.ListAsync(ProjectPrefix(projectId), ct)).Count >= MaxFilesPerProject)
            throw ApiException.BadRequest("وصلت للحد الأقصى من الصور لهذا الموقع، احذف صوراً غير مستخدمة");

        await using var content = file.OpenReadStream();
        var header = new byte[12];
        var read = await content.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        if (read < 4) throw ApiException.BadRequest("ملف غير صالح");
        header = header[..read];

        var ext = DetectImageExtension(header);
        if (ext is not null && file.Length > MaxBytes) throw ApiException.BadRequest("حجم الصورة يجب ألا يتجاوز 3 ميجابايت");
        if (ext is null && allowVideo) ext = DetectVideoExtension(header);
        if (ext is null)
            throw ApiException.BadRequest(allowVideo ? "مسموح فقط بصور JPG أو PNG أو WEBP أو GIF أو فيديو MP4 أو WEBM أو MOV" : "مسموح فقط بصور JPG أو PNG أو WEBP أو GIF");

        var name = $"{namePrefix}{Guid.NewGuid():N}{ext}";
        content.Position = 0;
        await storage.PutAsync(ProjectPrefix(projectId) + name, content, Sites.SiteFiles.ContentType(name), ct);
        return PublicPrefix(projectId) + name;
    }

    public const long MaxVideoBytes = 50 * 1024 * 1024;

    public static bool IsVideo(string nameOrUrl) => Path.GetExtension(nameOrUrl).ToLowerInvariant() is ".mp4" or ".webm" or ".mov";

    private static readonly HashSet<string> ImageBrands = ["heic", "heix", "hevc", "heim", "heis", "mif1", "msf1", "avif", "avis"];

    public static string? DetectVideoExtension(byte[] h)
    {
        if (h.Length >= 12 && h[4] == 'f' && h[5] == 't' && h[6] == 'y' && h[7] == 'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(h, 8, 4);
            if (ImageBrands.Contains(brand)) return null;
            return brand == "qt  " ? ".mov" : ".mp4";
        }
        if (h.Length >= 4 && h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3) return ".webm";
        return null;
    }

    public static string? DetectImageExtension(byte[] h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF) return ".jpg";
        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47) return ".png";
        if (h.Length >= 6 && h[0] == 'G' && h[1] == 'I' && h[2] == 'F') return ".gif";
        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P') return ".webp";
        return null;
    }

    public static void MapUploadFiles(IEndpointRouteBuilder app)
    {
        app.MapGet("/u/{projectId}/{file}", async (string projectId, string file, IUploadStorage storage, HttpContext http) =>
        {
            if (!Guid.TryParseExact(projectId, "N", out _) || !IsSafeName(file) || DetectedType(file) is not { } type)
                return Results.NotFound();
            var key = $"{projectId}/{file}";
            // Files on Cloudflare are loaded from the CDN. This address only redirects, so the file never passes through the server.
            if (storage.PublicObjectUrl(key) is { } cdn)
            {
                http.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.Redirect(cdn, permanent: true);
            }
            var content = await storage.OpenReadAsync(key, http.RequestAborted);
            if (content is null) return Results.NotFound();
            var video = IsVideo(file);
            // Browsers (Safari above all) only play videos that answer byte-range requests, which need a seekable stream.
            if (video && !content.CanSeek)
            {
                var buffer = new MemoryStream();
                await using (content) await content.CopyToAsync(buffer, http.RequestAborted);
                buffer.Position = 0;
                content = buffer;
            }
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            return Results.Stream(content, type, enableRangeProcessing: video);
        });
    }

    private static string? DetectedType(string file) =>
        Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".png" or ".gif" or ".webp" or ".mp4" or ".webm" or ".mov"
            ? Sites.SiteFiles.ContentType(file) : null;
}
