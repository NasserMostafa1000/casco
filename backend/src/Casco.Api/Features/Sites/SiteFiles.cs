using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Casco.Api.Features.Sites;

/// <summary>A website is a small set of text files keyed by relative path.</summary>
public static partial class SiteFiles
{
    [GeneratedRegex(@"^[A-Za-z0-9_\-]+(/[A-Za-z0-9_\-]+){0,2}\.(html|css|js|svg|json|txt|xml)$")]
    private static partial Regex AllowedPath();

    public static SortedDictionary<string, string> Parse(string json) =>
        JsonSerializer.Deserialize<SortedDictionary<string, string>>(json) ?? new(StringComparer.Ordinal);

    public static string Serialize(IDictionary<string, string> files) =>
        JsonSerializer.Serialize(new SortedDictionary<string, string>(files, StringComparer.Ordinal));

    public static bool IsAllowedPath(string path) =>
        path == Backend.BackendConfig.FileName ||
        (!string.IsNullOrWhiteSpace(path) && !path.Contains("..") && AllowedPath().IsMatch(path));

    public static bool IsHtml(string path) => path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

    public static int TotalBytes(IDictionary<string, string> files) =>
        files.Sum(f => System.Text.Encoding.UTF8.GetByteCount(f.Value));

    public static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".svg" => "image/svg+xml",
        ".json" => "application/json; charset=utf-8",
        ".xml" => "application/xml; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".ico" => "image/x-icon",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".wasm" => "application/wasm",
        ".webmanifest" => "application/manifest+json",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        _ => "application/octet-stream"
    };

    public static SortedDictionary<string, string> ReplaceTokens(IDictionary<string, string> files, string brand, string description)
    {
        var safeBrand = WebUtility.HtmlEncode(brand.Trim());
        var safeDescription = WebUtility.HtmlEncode(description.Trim());
        var year = DateTime.UtcNow.Year.ToString();
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            if (Backend.BackendConfig.IsBackendFile(path))
            {
                result[path] = content;
                continue;
            }
            var text = content
                .Replace("{{BRAND}}", safeBrand)
                .Replace("{{DESCRIPTION}}", safeDescription)
                .Replace("{{YEAR}}", year)
                .Replace("{{STOCK}}", SiteRuntime.StockImages.BaseUrl);
            if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                text = SiteRuntime.StockImages.FixImages(text);
            result[path] = text;
        }
        return result;
    }
}
