using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public record ZiinaPaymentIntent(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("amount")] long Amount,
    [property: JsonPropertyName("currency_code")] string? CurrencyCode,
    [property: JsonPropertyName("redirect_url")] string? RedirectUrl);

public class ZiinaClient(HttpClient http, IOptions<ZiinaOptions> options, ILogger<ZiinaClient> logger)
{
    private readonly ZiinaOptions _opt = options.Value;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_opt.ApiKey);
    public bool TestMode => _opt.TestMode;

    public async Task<ZiinaPaymentIntent> CreatePaymentIntentAsync(long amountMinor, string currency, string message,
        string successUrl, string cancelUrl, string failureUrl, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Post, "payment_intent");
        req.Content = JsonContent.Create(new Dictionary<string, object>
        {
            ["amount"] = amountMinor,
            ["currency_code"] = currency,
            ["message"] = message,
            ["success_url"] = successUrl,
            ["cancel_url"] = cancelUrl,
            ["failure_url"] = failureUrl,
            ["test"] = _opt.TestMode
        });
        return await SendAsync<ZiinaPaymentIntent>(req, ct);
    }

    public async Task<ZiinaPaymentIntent> GetPaymentIntentAsync(string id, CancellationToken ct = default)
    {
        using var req = NewRequest(HttpMethod.Get, $"payment_intent/{Uri.EscapeDataString(id)}");
        return await SendAsync<ZiinaPaymentIntent>(req, ct);
    }

    public async Task RegisterWebhookAsync(string url, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_opt.WebhookSecret))
            throw ApiException.BadRequest("اضبط Ziina__WebhookSecret أولاً (نص عشوائي طويل) ثم سجّل الـ Webhook");
        using var req = NewRequest(HttpMethod.Post, "webhook");
        req.Content = JsonContent.Create(new Dictionary<string, string> { ["url"] = url, ["secret"] = _opt.WebhookSecret });
        await SendAsync<object>(req, ct);
    }

    /// <summary>X-Hmac-Signature is the hex SHA-256 HMAC of the raw body using the webhook secret.</summary>
    public static bool VerifySignature(string rawBody, string? signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(signatureHeader)) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(rawBody));
        byte[] provided;
        try { provided = Convert.FromHexString(signatureHeader.Trim()); }
        catch (FormatException) { return false; }
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        if (!IsConfigured) throw new ApiException(503, "بوابة الدفع غير مهيأة بعد", "payments_not_configured");
        var req = new HttpRequestMessage(method, new Uri(new Uri(_opt.BaseUrl.TrimEnd('/') + "/"), path));
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);
        return req;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            logger.LogWarning("Ziina {Method} {Path} failed: {Status} {Body}", req.Method, req.RequestUri?.AbsolutePath, (int)res.StatusCode, Text.Truncate(body, 500));
            throw new ApiException(502, "تعذر التواصل مع بوابة الدفع، حاول لاحقاً", "payment_gateway_error");
        }
        return (await res.Content.ReadFromJsonAsync<T>(ct))!;
    }
}
