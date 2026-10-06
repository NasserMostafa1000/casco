using System.Text.Json;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Casco.Api.Features.Auth;

public class ExternalAuthOptions
{
    public ExternalProviderOptions Google { get; set; } = new();
    public ExternalProviderOptions Apple { get; set; } = new();
}

public class ExternalProviderOptions
{
    /// <summary>Comma-separated OAuth client IDs accepted as token audience. The first one is used by the web button
    /// (for Apple this must be the web Services ID).</summary>
    public string ClientIds { get; set; } = "";

    public string[] ClientIdList => ClientIds.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public string? WebClientId => ClientIdList.FirstOrDefault();
}

public static class ExternalProviders
{
    public const string Google = "google";
    public const string Apple = "apple";
}

public record ExternalIdentity(string Provider, string Subject, string Email, bool EmailVerified, string? Name);

/// <summary>Verifies ID tokens issued to the browser by Google Identity Services / Sign in with Apple JS.</summary>
public class ExternalTokenValidator(IOptions<ExternalAuthOptions> options, ILogger<ExternalTokenValidator> log)
{
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> GoogleConfig =
        new("https://accounts.google.com/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> AppleConfig =
        new("https://appleid.apple.com/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever());

    private readonly JsonWebTokenHandler _handler = new();

    public async Task<ExternalIdentity> ValidateAsync(string provider, string idToken, CancellationToken ct)
    {
        var (opt, config, issuers) = provider switch
        {
            ExternalProviders.Google => (options.Value.Google, GoogleConfig, new[] { "https://accounts.google.com", "accounts.google.com" }),
            ExternalProviders.Apple => (options.Value.Apple, AppleConfig, new[] { "https://appleid.apple.com" }),
            _ => throw ApiException.BadRequest("طريقة تسجيل دخول غير معروفة")
        };
        if (opt.ClientIdList.Length == 0) throw ApiException.BadRequest("طريقة تسجيل الدخول هذه غير مفعلة");
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > 8192) throw Invalid();

        var result = await ValidateWithKeysAsync(idToken, opt, config, issuers, ct);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Provider rotated its signing keys since we cached them.
            config.RequestRefresh();
            result = await ValidateWithKeysAsync(idToken, opt, config, issuers, ct);
        }
        if (!result.IsValid)
        {
            log.LogInformation("Rejected {Provider} token: {Reason}", provider, result.Exception?.Message);
            throw Invalid();
        }

        var claims = result.Claims;
        var subject = Claim(claims, "sub");
        if (string.IsNullOrEmpty(subject)) throw Invalid();
        var email = Text.NormalizeEmail(Claim(claims, "email") ?? "");
        var verified = claims.TryGetValue("email_verified", out var v) && (v is true || string.Equals(v?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
        return new ExternalIdentity(provider, subject, email, verified, Claim(claims, "name"));
    }

    private async Task<TokenValidationResult> ValidateWithKeysAsync(string token, ExternalProviderOptions opt,
        ConfigurationManager<OpenIdConnectConfiguration> config, string[] issuers, CancellationToken ct)
    {
        OpenIdConnectConfiguration oidc;
        try { oidc = await config.GetConfigurationAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Could not load signing keys");
            throw new ApiException(503, "تعذر الاتصال بخدمة تسجيل الدخول، حاول مرة أخرى", "provider_unavailable");
        }
        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuers = issuers,
            ValidAudiences = opt.ClientIdList,
            IssuerSigningKeys = oidc.SigningKeys,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        });
    }

    private static string? Claim(IDictionary<string, object> claims, string name) =>
        claims.TryGetValue(name, out var value) ? value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            null => null,
            _ => value.ToString()
        } : null;

    private static ApiException Invalid() => new(401, "فشل التحقق من حسابك، حاول مرة أخرى", "invalid_external_token");
}

public class TurnstileOptions
{
    public string SiteKey { get; set; } = "";
    public string Secret { get; set; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(Secret) && !string.IsNullOrWhiteSpace(SiteKey);
}

/// <summary>Cloudflare Turnstile bot check (https://developers.cloudflare.com/turnstile/get-started/server-side-validation/).</summary>
public class TurnstileVerifier(HttpClient http, IOptions<TurnstileOptions> options, ILogger<TurnstileVerifier> log)
{
    private record SiteVerifyResponse(bool Success, string[]? ErrorCodes);

    public async Task EnsureHumanAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        var opt = options.Value;
        if (!opt.Enabled) return;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
            throw ApiException.BadRequest("أكمل التحقق من أنك لست روبوتاً", "captcha_required");

        var form = new Dictionary<string, string> { ["secret"] = opt.Secret, ["response"] = token };
        if (!string.IsNullOrEmpty(remoteIp)) form["remoteip"] = remoteIp;
        SiteVerifyResponse? result;
        try
        {
            using var response = await http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", new FormUrlEncodedContent(form), ct);
            result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.KebabCaseLower }, ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Turnstile verification unavailable");
            throw new ApiException(503, "تعذر التحقق الآن، حاول مرة أخرى بعد قليل", "captcha_unavailable");
        }
        if (result?.Success != true)
        {
            log.LogInformation("Turnstile rejected: {Codes}", string.Join(",", result?.ErrorCodes ?? []));
            throw ApiException.BadRequest("انتهت صلاحية التحقق، أعد المحاولة", "captcha_failed");
        }
    }
}
