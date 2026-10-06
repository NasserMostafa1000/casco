using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public class TikTokOptions
{
    public string PixelId { get; set; } = "DAV5303C77U3N3HES4FG";
    public string AccessToken { get; set; } = "";
    /// <summary>When set, TikTok treats every event as a test and leaves it out of campaign data.</summary>
    public string TestEventCode { get; set; } = "";
}

public record TikTokRelayRequest(string? Event, string? EventId, string? Url, string? ContentId, string? ContentName, decimal? Value, string? Currency, string? Ttclid, string? Ttp);

/// <summary>Sends web events to TikTok Events API. Failures never block signup or payment.</summary>
public class TikTokEvents(IHttpClientFactory http, IOptions<TikTokOptions> options, IOptions<AppOptions> app, IMemoryCache cache, ILogger<TikTokEvents> logger)
{
    public const string HttpClientName = "tiktok";

    private static readonly HashSet<string> RelayEvents = new(StringComparer.Ordinal)
    {
        "ViewContent", "CompleteRegistration", "InitiateCheckout", "Purchase"
    };

    public static string Hash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static (string Id, string Name) Describe(Payment payment)
    {
        if (payment.Kind == PaymentKinds.Topup)
            return (string.IsNullOrWhiteSpace(payment.TopupPackId) ? "credits" : payment.TopupPackId, "Casco credits");
        if (payment.Kind == PaymentKinds.Hosting)
        {
            var tier = payment.HostingTier == HostingTiers.Backend ? "backend" : "static";
            var interval = payment.Interval == BillingIntervals.Yearly ? "yearly" : "monthly";
            return ($"hosting-{tier}-{interval}", tier == "backend" ? "Casco hosting with backend" : "Casco hosting");
        }
        var yearly = payment.Interval == BillingIntervals.Yearly;
        return (yearly ? "pro-yearly" : "pro-monthly", yearly ? "Casco Pro yearly" : "Casco Pro monthly");
    }

    public void Registration(User user, HttpContext http, string? ttclid, string? ttp) =>
        Send("CompleteRegistration", "reg_" + user.Id.ToString("D"), Page("/register"), user.Email, user.Id, http, ttclid, ttp,
            new Item("casco-account", "Casco account", null, null));

    public void Checkout(Payment payment, string email, HttpContext http, string? ttclid, string? ttp)
    {
        var click = ClickFrom(http, ttclid, ttp, Page("/app/billing"));
        cache.Set(ClickKey(payment.Id), click, TimeSpan.FromHours(6));
        var (id, name) = Describe(payment);
        Send("InitiateCheckout", "checkout_" + payment.Id.ToString("D"), click.Url, email, payment.UserId, http, click.Ttclid, click.Ttp,
            new Item(id, name, payment.AmountMinor / 100m, payment.Currency), click);
    }

    public void Purchase(Payment payment, string email)
    {
        cache.TryGetValue(ClickKey(payment.Id), out Click? click);
        var (id, name) = Describe(payment);
        var url = click?.Url ?? Page("/app/billing/result");
        Send("Purchase", "purchase_" + payment.Id.ToString("D"), url, email, payment.UserId, null, click?.Ttclid, click?.Ttp,
            new Item(id, name, payment.AmountMinor / 100m, payment.Currency), click);
    }

    public void Relay(TikTokRelayRequest req, HttpContext http, string? email, Guid? userId)
    {
        var name = req.Event?.Trim() ?? "";
        if (!RelayEvents.Contains(name)) return;
        var eventId = Clean(req.EventId, 80);
        if (eventId is null || eventId.Length < 6) return;
        var contentId = Clean(req.ContentId, 64) ?? "casco";
        var contentName = Clean(req.ContentName, 120) ?? "Casco";
        var url = SafeUrl(req.Url) ?? Page("/");
        Send(name, eventId, url, email, userId, http, req.Ttclid, req.Ttp,
            new Item(contentId, contentName, req.Value, req.Currency));
    }

    private void Send(string eventName, string eventId, string url, string? email, Guid? userId, HttpContext? http, string? ttclid, string? ttp, Item item, Click? click = null)
    {
        var opt = options.Value;
        if (string.IsNullOrWhiteSpace(opt.AccessToken) || string.IsNullOrWhiteSpace(opt.PixelId)) return;

        var ip = click?.Ip ?? ClientIp(http);
        var agent = click?.UserAgent ?? Clean(http?.Request.Headers.UserAgent.ToString(), 400);
        var payload = Build(opt, eventName, eventId, url, email, userId, ip, agent, Clean(ttclid, 200), Clean(ttp, 200), item);
        _ = PostAsync(payload, eventName);
    }

    private async Task PostAsync(Dictionary<string, object?> payload, string eventName)
    {
        try
        {
            var client = http.CreateClient(HttpClientName);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://business-api.tiktok.com/open_api/v1.3/event/track/");
            req.Headers.TryAddWithoutValidation("Access-Token", options.Value.AccessToken);
            req.Content = JsonContent.Create(payload);
            using var res = await client.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode || !text.Contains("\"code\":0", StringComparison.Ordinal))
                logger.LogWarning("TikTok {Event} was rejected ({Status}): {Body}", eventName, (int)res.StatusCode, Text.Truncate(text, 300));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "TikTok {Event} was not sent", eventName);
        }
    }

    private static Dictionary<string, object?> Build(TikTokOptions opt, string eventName, string eventId, string url, string? email, Guid? userId, string? ip, string? userAgent, string? ttclid, string? ttp, Item item)
    {
        var user = new Dictionary<string, object?>();
        var normalized = email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(normalized)) user["email"] = Hash(normalized);
        if (userId is { } id && id != Guid.Empty) user["external_id"] = Hash(id.ToString("D"));
        if (ip is not null) user["ip"] = ip;
        if (userAgent is not null) user["user_agent"] = userAgent;
        if (ttclid is not null) user["ttclid"] = ttclid;
        if (ttp is not null) user["ttp"] = ttp;

        var properties = new Dictionary<string, object?>
        {
            ["contents"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["content_id"] = item.Id,
                    ["content_type"] = "product",
                    ["content_name"] = item.Name
                }
            }
        };
        if (item.Value is { } value) properties["value"] = value;
        if (!string.IsNullOrWhiteSpace(item.Currency)) properties["currency"] = item.Currency;

        var data = new Dictionary<string, object?>
        {
            ["event"] = eventName,
            ["event_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["event_id"] = eventId,
            ["user"] = user,
            ["properties"] = properties,
            ["page"] = new Dictionary<string, object?> { ["url"] = url }
        };

        var body = new Dictionary<string, object?>
        {
            ["event_source"] = "web",
            ["event_source_id"] = opt.PixelId.Trim(),
            ["data"] = new object[] { data }
        };
        var test = (opt.TestEventCode ?? "").Trim();
        if (test.Length > 0) body["test_event_code"] = test;
        return body;
    }

    private Click ClickFrom(HttpContext http, string? ttclid, string? ttp, string url) =>
        new(ClientIp(http), Clean(http.Request.Headers.UserAgent.ToString(), 400), Clean(ttclid, 200), Clean(ttp, 200), url);

    private string Page(string path) => app.Value.FrontendBase + path;

    private string? SafeUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        var hosts = app.Value.Hosts;
        if (hosts.Length > 0 && !hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) return null;
        return Clean(uri.GetLeftPart(UriPartial.Path) + uri.Query, 500);
    }

    private static string? ClientIp(HttpContext? http)
    {
        var ip = http?.Connection.RemoteIpAddress;
        if (ip is null) return null;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(ip)) return null;
        return ip.ToString();
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Any(char.IsControl)) return null;
        return text.Length <= max ? text : text[..max];
    }

    private static string ClickKey(Guid paymentId) => "tiktok:" + paymentId.ToString("D");

    private sealed record Item(string Id, string Name, decimal? Value, string? Currency);
    private sealed record Click(string? Ip, string? UserAgent, string? Ttclid, string? Ttp, string Url);
}

public static class TikTokEndpoints
{
    public static void MapTikTokEvents(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/tiktok/events", async (TikTokRelayRequest req, HttpContext http, AppDbContext db, TikTokEvents tiktok) =>
        {
            string? email = null;
            Guid? userId = null;
            var raw = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (Guid.TryParse(raw, out var id))
            {
                userId = id;
                email = await db.Users.Where(u => u.Id == id).Select(u => u.Email).FirstOrDefaultAsync();
            }
            tiktok.Relay(req, http, email, userId);
            return Results.Ok();
        }).RequireRateLimiting("tiktok");
    }
}
