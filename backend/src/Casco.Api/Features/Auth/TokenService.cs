using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Casco.Api.Features.Auth;

/// <summary>Identity carried by one access token. <see cref="Jti"/> is what logout revokes.</summary>
public readonly record struct IssuedSession(Guid UserId, string Jti, DateTime ExpiresAt);

public class TokenService(IOptions<JwtOptions> options)
{
    public const string AppAudience = "casco-app";
    private readonly JwtOptions _opt = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(_opt.Key));

    public string CreateUserToken(User user) => Create(new Dictionary<string, object>
    {
        [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
        [JwtRegisteredClaimNames.Email] = user.Email,
        [ClaimTypes.Role] = user.Role
    }, AppAudience, TimeSpan.FromDays(_opt.ExpiryDays));

    public string CreateSiteUserToken(SiteUser user) => Create(new Dictionary<string, object>
    {
        [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
        [JwtRegisteredClaimNames.Email] = user.Email
    }, SiteAudience(user.ProjectId), TimeSpan.FromDays(30));

    public static string SiteAudience(Guid projectId) => $"casco-site:{projectId:N}";

    public Task<IssuedSession?> ReadAppSessionAsync(string? authorizationHeader) =>
        ReadSessionAsync(authorizationHeader, AppAudience);

    public Task<IssuedSession?> ReadSiteSessionAsync(string? authorizationHeader, Guid projectId) =>
        ReadSessionAsync(authorizationHeader, SiteAudience(projectId));

    private async Task<IssuedSession?> ReadSessionAsync(string? authorizationHeader, string audience)
    {
        if (string.IsNullOrEmpty(authorizationHeader) || !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return null;
        var result = await _handler.ValidateTokenAsync(authorizationHeader["Bearer ".Length..].Trim(), new TokenValidationParameters
        {
            ValidIssuer = _opt.Issuer,
            ValidAudience = audience,
            IssuerSigningKey = SigningKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        });
        if (!result.IsValid || !TryReadSession(result, out var session)) return null;
        return session;
    }

    private static bool TryReadSession(TokenValidationResult result, out IssuedSession session)
    {
        session = default;
        var claims = result.Claims;
        if (!claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub)
            && !claims.TryGetValue(ClaimTypes.NameIdentifier, out sub))
            return false;
        if (!Guid.TryParse(sub?.ToString(), out var userId)) return false;
        if (!claims.TryGetValue(JwtRegisteredClaimNames.Jti, out var jtiObj) || jtiObj is not string jti || jti.Length is 0 or > 64)
            return false;
        if (!TryExpiry(claims, out var expires)) return false;
        session = new IssuedSession(userId, jti, expires);
        return true;
    }

    private static bool TryExpiry(IDictionary<string, object> claims, out DateTime expires)
    {
        expires = default;
        if (!claims.TryGetValue(JwtRegisteredClaimNames.Exp, out var value) || value is null) return false;
        if (value is DateTime dt)
        {
            expires = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
            return true;
        }
        if (value is DateTimeOffset dto)
        {
            expires = dto.UtcDateTime;
            return true;
        }
        if (!long.TryParse(value.ToString(), out var unix)) return false;
        expires = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
        return true;
    }

    /// <summary>Signed, expiring token embedded in preview URLs (iframes cannot send auth headers).</summary>
    public string CreatePreviewToken(Guid projectId, TimeSpan lifetime)
    {
        var exp = DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds();
        return $"{exp}.{PreviewSignature(projectId, exp)}";
    }

    public bool ValidatePreviewToken(Guid projectId, string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 2 || !long.TryParse(parts[0], out var exp)) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(PreviewSignature(projectId, exp)), Encoding.ASCII.GetBytes(parts[1]));
    }

    private string PreviewSignature(Guid projectId, long exp)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_opt.Key), Encoding.UTF8.GetBytes($"preview:{projectId:N}:{exp}"));
        return Convert.ToHexString(mac, 0, 16).ToLowerInvariant();
    }

    private string Create(Dictionary<string, object> claims, string audience, TimeSpan lifetime)
    {
        // Unique id so logout can reject this token without invalidating a session on another device.
        claims[JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N");
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _opt.Issuer,
            Audience = audience,
            Claims = claims,
            Expires = DateTime.UtcNow.Add(lifetime),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256)
        });
    }
}

/// <summary>Remembers logged-out token ids until they expire. Shared by both app servers through the database.</summary>
public static class TokenRevocation
{
    public static Task<bool> IsRevokedAsync(AppDbContext db, string jti, CancellationToken ct = default) =>
        db.RevokedTokens.AsNoTracking().AnyAsync(t => t.Jti == jti, ct);

    public static async Task RevokeAsync(AppDbContext db, string jti, DateTime expiresAt, CancellationToken ct = default)
    {
        if (await db.RevokedTokens.AnyAsync(t => t.Jti == jti, ct)) return;
        db.RevokedTokens.Add(new RevokedToken { Jti = jti, ExpiresAt = expiresAt });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            var added = db.ChangeTracker.Entries<RevokedToken>().FirstOrDefault(e => e.Entity.Jti == jti);
            if (added is not null) added.State = EntityState.Detached;
        }
    }
}
