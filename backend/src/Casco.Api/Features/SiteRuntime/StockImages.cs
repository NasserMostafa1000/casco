using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.SiteRuntime;

public class StockImageOptions
{
    /// <summary>Free key from pexels.com/api. Without it photos come from loremflickr (keyword-tagged Flickr photos).</summary>
    public string PexelsApiKey { get; set; } = "";
}

/// <summary>
/// Photos that match a site's business: {BaseUrl}/{W}x{H}/{english-keywords}?i=N redirects to a stock photo found by
/// those keywords. Generated sites and templates use these URLs instead of random placeholder photos.
/// </summary>
public class StockImages(HttpClient http, IMemoryCache cache, IOptions<StockImageOptions> options, ILogger<StockImages> logger)
{
    /// <summary>Set at startup from App:PublicUrl; the AI prompt and the {{STOCK}} template token point here.</summary>
    public static string BaseUrl { get; set; } = "https://api.casco.studio/stock";

    public const int MaxSide = 2400;
    private const int MaxWords = 5;

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/stock/{size}/{query}", async (string size, string query, int? i, StockImages stock, HttpContext ctx) =>
        {
            if (!TryParseSize(size, out var w, out var h)) return Results.NotFound();
            var words = Keywords(query);
            if (words.Length == 0) return Results.NotFound();
            var image = await stock.LoadAsync(words, w, h, Math.Clamp(i ?? 0, 0, 100), ctx.RequestAborted);
            if (image is null) return Results.NotFound();
            ctx.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.File(image.Value.Bytes, image.Value.Type);
        });

    public static bool TryParseSize(string size, out int w, out int h)
    {
        w = h = 0;
        var parts = size.ToLowerInvariant().Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out w) && int.TryParse(parts[1], out h)
               && w is >= 16 and <= MaxSide && h is >= 16 and <= MaxSide;
    }

    public static string[] Keywords(string query) =>
        query.ToLowerInvariant()
            .Split(['-', ',', '+', ' ', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string(word.Where(char.IsLetterOrDigit).ToArray()))
            .Where(word => word.Length is > 0 and <= 30)
            .Distinct()
            .Take(MaxWords)
            .ToArray();

    public async Task<string> ResolveAsync(string[] words, int w, int h, int index, CancellationToken ct)
    {
        var orientation = w > h * 1.2 ? "landscape" : h > w * 1.2 ? "portrait" : "square";
        // A long, very specific query can find nothing: drop the last (least important) words until something matches.
        for (var take = words.Length; take > 0; take--)
        {
            var query = string.Join(' ', words.Take(take));
            if (!string.IsNullOrWhiteSpace(options.Value.PexelsApiKey))
            {
                var photos = await SearchPexelsAsync(query, orientation, ct);
                if (photos.Count > 0)
                    return $"{photos[index % photos.Count]}?auto=compress&cs=tinysrgb&fit=crop&w={w}&h={h}";
            }
            var open = await SearchOpenverseAsync(query, ct);
            if (open.Count > 0) return open[index % open.Count];
        }
        return FallbackUrl(words, w, h, index);
    }

    /// <summary>Downloads the photo and returns the bytes, so the browser never follows a third-party link that can 404.</summary>
    public async Task<(byte[] Bytes, string Type)?> LoadAsync(string[] words, int w, int h, int index, CancellationToken ct)
    {
        var cacheKey = $"stockimg:{w}x{h}:{string.Join('-', words)}:{index}";
        if (cache.TryGetValue(cacheKey, out (byte[] Bytes, string Type) hit)) return hit;

        var primary = await ResolveAsync(words, w, h, index, ct);
        var image = await DownloadAsync(primary, ct);
        if (image is null && !primary.Contains("picsum.photos", StringComparison.Ordinal))
            image = await DownloadAsync(FallbackUrl(words, w, h, index), ct);
        if (image is null) return null;
        cache.Set(cacheKey, image.Value, TimeSpan.FromDays(7));
        return image;
    }

    private async Task<(byte[] Bytes, string Type)?> DownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            using var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return null;
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return null;
            var bytes = await res.Content.ReadAsByteArrayAsync(ct);
            return bytes.Length == 0 ? null : (bytes, type);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning("Stock photo download failed: {Error}", e.Message);
            return null;
        }
    }

    private static string FallbackUrl(string[] words, int w, int h, int index)
    {
        var seed = StableHash(string.Join('-', words) + ":" + index).ToString();
        return $"https://picsum.photos/seed/{seed}/{w}/{h}";
    }

    /// <summary>Free keyword search (commercial licenses) used when no Pexels key is set. Picsum ignores the words, so it is only the last resort.</summary>
    private async Task<IReadOnlyList<string>> SearchOpenverseAsync(string query, CancellationToken ct)
    {
        var key = $"openverse:{query}";
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached)) return cached!;

        IReadOnlyList<string> photos = [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.openverse.org/v1/images/?q={Uri.EscapeDataString(query)}&license_type=commercial&page_size=20");
            req.Headers.TryAddWithoutValidation("User-Agent", "Casco/1.0 (https://casco.studio)");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Openverse search failed with {Status} for \"{Query}\"", (int)res.StatusCode, query);
                return photos;
            }
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("results", out var results)) return photos;
            photos = results.EnumerateArray()
                .Select(PhotoUrl)
                .OfType<string>()
                .Distinct()
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Openverse search failed for \"{Query}\": {Error}", query, e.Message);
            return photos;
        }
        cache.Set(key, photos, photos.Count > 0 ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1));
        return photos;
    }

    private static string? PhotoUrl(JsonElement photo)
    {
        if (!photo.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String) return null;
        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url)) return null;
        var width = photo.TryGetProperty("width", out var w) && w.TryGetInt32(out var n) ? n : 0;
        if (width is > 0 and < 500) return null;
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        if (path.Contains(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        return url;
    }

    private async Task<IReadOnlyList<string>> SearchPexelsAsync(string query, string orientation, CancellationToken ct)
    {
        var key = $"stock:{orientation}:{query}";
        if (cache.TryGetValue(key, out IReadOnlyList<string>? cached)) return cached!;

        IReadOnlyList<string> photos = [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.pexels.com/v1/search?query={Uri.EscapeDataString(query)}&orientation={orientation}&per_page=30");
            req.Headers.TryAddWithoutValidation("Authorization", options.Value.PexelsApiKey);
            using var res = await http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                logger.LogWarning("Pexels search failed with {Status} for \"{Query}\"", (int)res.StatusCode, query);
                return photos;
            }
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            photos = doc.RootElement.GetProperty("photos").EnumerateArray()
                .Select(p => p.GetProperty("src").GetProperty("original").GetString())
                .OfType<string>()
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Pexels search failed for \"{Query}\": {Error}", query, e.Message);
            return photos;
        }
        cache.Set(key, photos, photos.Count > 0 ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1));
        return photos;
    }

    private static int StableHash(string s)
    {
        var hash = 17;
        foreach (var b in Encoding.UTF8.GetBytes(s)) hash = unchecked(hash * 31 + b);
        return hash & int.MaxValue;
    }

    private static readonly Regex ImageAttr = new(
        """(?<pre>\b(?:src|poster)\s*=\s*)(?<q>["'])(?<url>[^"']+)(?<end>\k<q>)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CssUrl = new(
        """(?<pre>url\(\s*)(?<q>["']?)(?<url>[^"')]+)(?<end>\k<q>\s*\))""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SizeInUrl = new(@"(\d{2,4})x(\d{2,4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Turns internet photo links the model invented into a stock URL this server can actually serve.</summary>
    public static string FixImages(string content)
    {
        var n = 0;
        content = ImageAttr.Replace(content, m => Rewrite(m, ref n));
        return CssUrl.Replace(content, m => Rewrite(m, ref n));
    }

    public static bool IsValidStockUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 3 && parts[0] == "stock" && TryParseSize(parts[1], out _, out _) && Keywords(parts[2]).Length > 0;
    }

    private static string Rewrite(Match m, ref int n)
    {
        var url = m.Groups["url"].Value.Trim();
        if (!ShouldRewrite(url)) return m.Value;
        var next = Canonical(url, n++);
        return m.Groups["pre"].Value + m.Groups["q"].Value + next + m.Groups["end"].Value;
    }

    private static bool ShouldRewrite(string url)
    {
        if (url.Length == 0 || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase))
            return false;
        if (IsValidStockUrl(url) || IsUpload(url)) return false;
        return LooksLikePhoto(url);
    }

    private static bool IsUpload(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.AbsolutePath.Contains("/uploads/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePhoto(string url)
    {
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            return false;
        if (path.Contains("/stock/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.HostNameType == UriHostNameType.Unknown) return false;
        var host = uri.Host;
        return host.Contains("unsplash", StringComparison.OrdinalIgnoreCase)
               || host.Contains("pexels", StringComparison.OrdinalIgnoreCase)
               || host.Contains("picsum", StringComparison.OrdinalIgnoreCase)
               || host.Contains("loremflickr", StringComparison.OrdinalIgnoreCase)
               || host.Contains("placeholder", StringComparison.OrdinalIgnoreCase)
               || host.Contains("placehold", StringComparison.OrdinalIgnoreCase);
    }

    private static string Canonical(string original, int index)
    {
        var w = 1200;
        var h = 800;
        var size = SizeInUrl.Match(original);
        if (size.Success && TryParseSize(size.Value, out var pw, out var ph))
        {
            w = pw;
            h = ph;
        }
        var last = original.Split('?', '#')[0].Split('/').LastOrDefault() ?? "";
        var dot = last.LastIndexOf('.');
        if (dot > 0) last = last[..dot];
        var words = Keywords(last);
        if (words.Length == 0) words = ["business"];
        var query = index > 0 ? $"?i={index}" : "";
        return $"{BaseUrl}/{w}x{h}/{string.Join('-', words)}{query}";
    }
}
