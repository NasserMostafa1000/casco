using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Auth;

public record RegisterRequest(string Name, string Email, string Password, string? TurnstileToken = null, string? Ttclid = null, string? Ttp = null, string? ReferralCode = null);
public record LoginRequest(string Email, string Password);
public record ExternalLoginRequest(string Provider, string IdToken, string? Name = null, string? Ttclid = null, string? Ttp = null, string? ReferralCode = null);

public static class AuthEndpoints
{
    private static readonly PasswordHasher<object> Hasher = new();

    public static string HashPassword(string password) => Hasher.HashPassword(null!, password);

    public static bool VerifyPassword(string hash, string password) =>
        Hasher.VerifyHashedPassword(null!, hash, password) != PasswordVerificationResult.Failed;

    public static void ValidateCredentials(string? email, string? password)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || email.Length > 256)
            throw ApiException.BadRequest("البريد الإلكتروني غير صالح");
        if (string.IsNullOrEmpty(password) || password.Length < 8 || password.Length > 128)
            throw ApiException.BadRequest("كلمة المرور يجب أن تكون 8 أحرف على الأقل");
    }

    private static bool IsAdminEmail(AppOptions app, string email) =>
        app.AdminEmails.Contains(email, StringComparer.OrdinalIgnoreCase);

    private static async Task<User> CreateUserAsync(AppDbContext db, CreditService credits, AppOptions app, BillingOptions billing,
        string email, string? name, string? referralCode, Action<User> configure)
    {
        var user = new User
        {
            Email = email,
            Name = Text.Truncate(string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name.Trim(), 120),
            Role = IsAdminEmail(app, email) ? Roles.Admin : Roles.User
        };
        configure(user);
        await ShareReward.AttachReferrerAsync(db, user, referralCode);
        db.Users.Add(user);
        db.Subscriptions.Add(new Subscription { UserId = user.Id, Plan = PlanKeys.Free });
        await db.SaveChangesAsync();

        if (billing.SignupBonusCredits > 0)
            await credits.GrantAsync(user.Id, billing.SignupBonusCredits, CreditBuckets.Topup, CreditEntryTypes.SignupBonus, "signup");
        return user;
    }

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Public keys the login page needs; nothing secret.
        app.MapGet("/api/auth/config", (IOptions<ExternalAuthOptions> ext, IOptions<TurnstileOptions> turnstile) => Results.Ok(new
        {
            googleClientId = ext.Value.Google.WebClientId,
            appleClientId = ext.Value.Apple.WebClientId,
            turnstileSiteKey = turnstile.Value.Enabled ? turnstile.Value.SiteKey : null
        }));

        var g = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        g.MapPost("/register", async (RegisterRequest req, HttpContext http, AppDbContext db, TokenService tokens, TurnstileVerifier turnstile,
            CreditService credits, TikTokEvents tiktok, IOptions<AppOptions> appOpt, IOptions<BillingOptions> billingOpt) =>
        {
            ValidateCredentials(req.Email, req.Password);
            await turnstile.EnsureHumanAsync(req.TurnstileToken, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
            var email = Text.NormalizeEmail(req.Email);
            if (await db.Users.AnyAsync(u => u.Email == email))
                throw ApiException.Conflict("هذا البريد مسجل بالفعل", "email_taken");

            var user = await CreateUserAsync(db, credits, appOpt.Value, billingOpt.Value, email, req.Name, req.ReferralCode,
                u => u.PasswordHash = HashPassword(req.Password));
            tiktok.Registration(user, http, req.Ttclid, req.Ttp);
            return Results.Ok(new { token = tokens.CreateUserToken(user), id = user.Id });
        });

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, TokenService tokens, IOptions<AppOptions> appOpt) =>
        {
            var email = Text.NormalizeEmail(req.Email ?? "");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is { PasswordHash: "" })
                throw new ApiException(401, "هذا الحساب مسجل عبر Google أو Apple، استخدم زر الدخول الخاص به", "use_external_login");
            if (user is null || !VerifyPassword(user.PasswordHash, req.Password ?? ""))
                throw new ApiException(401, "البريد أو كلمة المرور غير صحيحة", "invalid_credentials");

            await PromoteAdminAsync(db, appOpt.Value, user);
            return Results.Ok(new { token = tokens.CreateUserToken(user) });
        });

        g.MapPost("/external", async (ExternalLoginRequest req, HttpContext http, AppDbContext db, TokenService tokens,
            ExternalTokenValidator validator, CreditService credits, TikTokEvents tiktok, IOptions<AppOptions> appOpt, IOptions<BillingOptions> billingOpt) =>
        {
            var provider = (req.Provider ?? "").Trim().ToLowerInvariant();
            var identity = await validator.ValidateAsync(provider, req.IdToken ?? "", http.RequestAborted);
            var isGoogle = provider == ExternalProviders.Google;

            var user = isGoogle
                ? await db.Users.FirstOrDefaultAsync(u => u.GoogleId == identity.Subject)
                : await db.Users.FirstOrDefaultAsync(u => u.AppleId == identity.Subject);
            var created = false;
            if (user is null)
            {
                // Linking by e-mail is only safe when the provider has verified the address.
                if (string.IsNullOrEmpty(identity.Email) || !identity.EmailVerified || identity.Email.Length > 256)
                    throw ApiException.BadRequest("حسابك لا يحتوي على بريد إلكتروني مؤكد، استخدم التسجيل بالبريد", "email_not_verified");

                user = await db.Users.FirstOrDefaultAsync(u => u.Email == identity.Email);
                if (user is not null)
                {
                    if (isGoogle) user.GoogleId = identity.Subject; else user.AppleId = identity.Subject;
                    await db.SaveChangesAsync();
                }
                else
                {
                    user = await CreateUserAsync(db, credits, appOpt.Value, billingOpt.Value, identity.Email, identity.Name ?? req.Name, req.ReferralCode, u =>
                    {
                        if (isGoogle) u.GoogleId = identity.Subject; else u.AppleId = identity.Subject;
                    });
                    created = true;
                    tiktok.Registration(user, http, req.Ttclid, req.Ttp);
                }
            }

            await PromoteAdminAsync(db, appOpt.Value, user);
            return Results.Ok(new { token = tokens.CreateUserToken(user), created, id = user.Id });
        });

        g.MapPost("/logout", async (HttpContext http, AppDbContext db, TokenService tokens) =>
        {
            var session = await tokens.ReadAppSessionAsync(http.Request.Headers.Authorization);
            if (session is null || session.Value.UserId != http.User.UserId())
                throw new ApiException(401, "غير مسجل الدخول", "unauthorized");
            await TokenRevocation.RevokeAsync(db, session.Value.Jti, session.Value.ExpiresAt, http.RequestAborted);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task PromoteAdminAsync(AppDbContext db, AppOptions app, User user)
    {
        if (IsAdminEmail(app, user.Email) && user.Role != Roles.Admin)
        {
            user.Role = Roles.Admin;
            await db.SaveChangesAsync();
        }
    }
}
