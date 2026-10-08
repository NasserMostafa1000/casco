using System.Security.Cryptography;
using System.Text;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Auth;

public record DeviceStartResponse(string DeviceCode, string UserCode, string VerificationUrl, int Interval, int ExpiresIn);
public record DevicePollRequest(string DeviceCode);
public record DeviceUserCodeRequest(string UserCode);

public static class DesktopAuth
{
    public const int LifetimeSeconds = 600;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string HashDeviceCode(string deviceCode)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(deviceCode.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static void MapDesktopAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var start = app.MapGroup("/api/auth/device").RequireRateLimiting("auth");
        start.MapPost("/start", StartAsync);

        app.MapPost("/api/auth/device/poll", PollAsync).RequireRateLimiting("device");
        app.MapPost("/api/auth/device/enter", EnterAsync).RequireRateLimiting("device");

        var approved = app.MapGroup("/api/auth/device").RequireAuthorization().RequireRateLimiting("auth");
        approved.MapPost("/approve", ApproveAsync);
        approved.MapPost("/deny", DenyAsync);
        approved.MapPost("/browser", BrowserAsync);
    }

    private static async Task<IResult> StartAsync(AppDbContext db, IOptions<AppOptions> appOpt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.DesktopLogins.Where(x => x.ExpiresAt < now || (x.Status == "consumed" && x.CreatedAt < now.AddHours(-1)))
            .ExecuteDeleteAsync(ct);

        string userCode;
        do
        {
            userCode = NewUserCode();
        } while (await db.DesktopLogins.AnyAsync(x => x.UserCode == userCode, ct));

        var deviceCode = NewDeviceCode();
        db.DesktopLogins.Add(new DesktopLogin
        {
            UserCode = userCode,
            DeviceCodeHash = HashDeviceCode(deviceCode),
            ExpiresAt = now.AddSeconds(LifetimeSeconds)
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new DeviceStartResponse(
            deviceCode,
            userCode,
            $"{appOpt.Value.FrontendBase}/desktop?code={Uri.EscapeDataString(userCode)}",
            2,
            LifetimeSeconds));
    }

    private static async Task<IResult> PollAsync(DevicePollRequest req, AppDbContext db, TokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceCode) || req.DeviceCode.Length > 128)
            throw ApiException.BadRequest("رمز الجهاز غير صالح", "invalid_device_code");

        var row = await db.DesktopLogins.FirstOrDefaultAsync(x => x.DeviceCodeHash == HashDeviceCode(req.DeviceCode), ct);
        if (row is null || row.ExpiresAt <= DateTime.UtcNow || row.Status is "consumed" or "denied")
            return Results.Ok(new { status = row?.Status == "denied" ? "denied" : "expired" });
        if (row.Status != "approved" || row.UserId is null)
            return Results.Ok(new { status = "pending" });

        var user = await db.Users.FindAsync([row.UserId.Value], ct) ?? throw new ApiException(401, "الحساب غير موجود", "unauthorized");
        row.Status = "consumed";
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status = "approved", token = tokens.CreateUserToken(user) });
    }

    private static async Task<IResult> BrowserAsync(HttpContext http, AppDbContext db, IOptions<AppOptions> appOpt, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        string userCode;
        do
        {
            userCode = NewUserCode();
        } while (await db.DesktopLogins.AnyAsync(x => x.UserCode == userCode, ct));

        var secret = NewDeviceCode();
        db.DesktopLogins.Add(new DesktopLogin
        {
            UserCode = userCode,
            DeviceCodeHash = HashDeviceCode(secret),
            UserId = http.User.UserId(),
            Status = "handoff",
            ExpiresAt = now.AddMinutes(10)
        });
        await db.SaveChangesAsync(ct);
        var url = $"{appOpt.Value.FrontendBase.TrimEnd('/')}/desktop?enter={Uri.EscapeDataString(secret)}&next={Uri.EscapeDataString("/app/billing")}";
        return Results.Ok(new { url });
    }

    private static async Task<IResult> EnterAsync(DevicePollRequest req, AppDbContext db, TokenService tokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceCode) || req.DeviceCode.Length > 128)
            throw ApiException.BadRequest("رمز الجهاز غير صالح", "invalid_device_code");

        var row = await db.DesktopLogins.FirstOrDefaultAsync(x => x.DeviceCodeHash == HashDeviceCode(req.DeviceCode), ct);
        if (row is null || row.ExpiresAt <= DateTime.UtcNow || row.Status != "handoff" || row.UserId is null)
            throw ApiException.BadRequest("رابط الدخول انتهى. ارجع لـ Casco Studio وحاول تاني.", "device_expired");

        var user = await db.Users.FindAsync([row.UserId.Value], ct) ?? throw ApiException.BadRequest("الحساب غير موجود", "unauthorized");
        row.Status = "consumed";
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { token = tokens.CreateUserToken(user) });
    }

    private static Task<IResult> ApproveAsync(DeviceUserCodeRequest req, HttpContext http, AppDbContext db, CancellationToken ct) =>
        DecideAsync(req, http, db, "approved", ct);

    private static Task<IResult> DenyAsync(DeviceUserCodeRequest req, HttpContext http, AppDbContext db, CancellationToken ct) =>
        DecideAsync(req, http, db, "denied", ct);

    private static async Task<IResult> DecideAsync(DeviceUserCodeRequest req, HttpContext http, AppDbContext db, string status, CancellationToken ct)
    {
        var code = (req.UserCode ?? "").Trim().ToUpperInvariant();
        var row = await db.DesktopLogins.FirstOrDefaultAsync(x => x.UserCode == code && x.Status == "pending", ct);
        if (row is null || row.ExpiresAt <= DateTime.UtcNow)
            throw ApiException.BadRequest("طلب الدخول انتهى أو غير موجود. ابدأ من Casco Studio تاني.", "device_expired");
        row.Status = status;
        row.UserId = status == "approved" ? http.User.UserId() : null;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { status });
    }

    private static string NewUserCode()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    private static string NewDeviceCode() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
