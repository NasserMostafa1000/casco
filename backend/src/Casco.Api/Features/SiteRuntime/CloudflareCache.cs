using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.SiteRuntime;

public class CloudflareOptions
{
    public string ApiToken { get; set; } = "";
    public string ZoneId { get; set; } = "";
}

/// <summary>Removes deleted uploads from the Cloudflare cache in front of the R2 bucket.</summary>
public class CloudflareCachePurge(IHttpClientFactory http, IOptions<CloudflareOptions> options, ILogger<CloudflareCachePurge> logger)
{
    public const string HttpClientName = "cloudflare";

    /// <summary>Host plus path, no scheme. Null when the address is not an absolute URL.</summary>
    public static string? CachePrefix(string? publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.EndsWith('/') ? uri.AbsolutePath : uri.AbsolutePath + "/";
        return uri.Host + path;
    }

    public async Task PurgeAsync(string? folderUrl, IReadOnlyList<string> fileUrls, CancellationToken ct = default)
    {
        var prefix = CachePrefix(folderUrl);
        if (prefix is null && fileUrls.Count == 0) return;
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.ApiToken) || string.IsNullOrWhiteSpace(opt.ZoneId))
        {
            logger.LogWarning("Site files were deleted from storage, but the Cloudflare cache was not purged because Cloudflare:ApiToken or Cloudflare:ZoneId is empty.");
            return;
        }

        if (prefix is not null && await SendAsync(opt, new { prefixes = new[] { prefix } }, ct)) return;

        foreach (var batch in fileUrls.Distinct(StringComparer.Ordinal).Chunk(30))
            await SendAsync(opt, new { files = batch }, ct);
    }

    private async Task<bool> SendAsync(CloudflareOptions opt, object body, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.cloudflare.com/client/v4/zones/{opt.ZoneId}/purge_cache");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opt.ApiToken);
            req.Content = JsonContent.Create(body);
            using var res = await client.SendAsync(req, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            var ok = res.IsSuccessStatusCode && text.Contains("\"success\":true", StringComparison.Ordinal);
            if (!ok)
                logger.LogError("Cloudflare cache purge failed ({Status}): {Body}", (int)res.StatusCode, Text.Truncate(text, 300));
            return ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogError(ex, "Cloudflare cache purge failed");
            return false;
        }
    }
}
