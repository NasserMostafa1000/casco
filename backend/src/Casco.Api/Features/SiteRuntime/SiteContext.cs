using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Features.Auth;
using Casco.Api.Features.Backend;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.SiteRuntime;

public static class PaymentMethods
{
    public const string CashOnDelivery = "cod";
    public const string WhatsApp = "whatsapp";
    public const string BankTransfer = "transfer";
    public const string Pickup = "pickup";

    public static readonly string[] All = [CashOnDelivery, WhatsApp, BankTransfer, Pickup];
}

public class WorkingDay
{
    /// <summary>0 = Sunday … 6 = Saturday.</summary>
    public int Day { get; set; }
    public string Open { get; set; } = "09:00";
    public string Close { get; set; } = "17:00";
    public bool Closed { get; set; }
}

public class BookingSettings
{
    public string TimeZone { get; set; } = "Asia/Dubai";
    public int SlotMinutes { get; set; } = 30;
    public int DaysAhead { get; set; } = 30;
    /// <summary>How many bookings may overlap (number of staff / rooms).</summary>
    public int Capacity { get; set; } = 1;
    public int MinNoticeMinutes { get; set; } = 60;
    public bool AutoConfirm { get; set; }
    public List<WorkingDay> Week { get; set; } = Enumerable.Range(0, 7)
        .Select(d => new WorkingDay { Day = d, Closed = d == 5 }).ToList();
    /// <summary>yyyy-MM-dd dates when nothing can be booked (holidays).</summary>
    public List<string> ClosedDates { get; set; } = [];
}

public class SiteSettings
{
    /// <summary>Owner number (international format) used for WhatsApp order/booking links.</summary>
    public string? WhatsApp { get; set; }
    /// <summary>Where new orders/bookings/messages are e-mailed (defaults to the account e-mail).</summary>
    public string? NotifyEmail { get; set; }
    public string Currency { get; set; } = "AED";
    public decimal ShippingFee { get; set; }
    /// <summary>Orders at or above this subtotal ship free (0 = never).</summary>
    public decimal FreeShippingOver { get; set; }
    public List<string> PaymentMethods { get; set; } = [SiteRuntime.PaymentMethods.CashOnDelivery, SiteRuntime.PaymentMethods.WhatsApp];
    public string? BankDetails { get; set; }
    public BookingSettings Booking { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static SiteSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<SiteSettings>(json, Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>Clamps owner input to safe values (throws for things the owner must fix).</summary>
    public SiteSettings Sanitize()
    {
        var digits = new string((WhatsApp ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length is > 0 and < 8) throw ApiException.BadRequest("رقم واتساب غير صالح، اكتبه بالصيغة الدولية مثل 971501234567");
        WhatsApp = digits.Length == 0 ? null : digits[..Math.Min(digits.Length, 15)];
        NotifyEmail = string.IsNullOrWhiteSpace(NotifyEmail) ? null : Text.Truncate(NotifyEmail.Trim(), 256);
        if (NotifyEmail is not null && (!NotifyEmail.Contains('@') || NotifyEmail.Contains(' '))) throw ApiException.BadRequest("بريد التنبيهات غير صالح");
        Currency = Text.Truncate(string.IsNullOrWhiteSpace(Currency) ? "AED" : Currency.Trim().ToUpperInvariant(), 3);
        ShippingFee = Math.Clamp(ShippingFee, 0, 100000);
        FreeShippingOver = Math.Clamp(FreeShippingOver, 0, 10000000);
        PaymentMethods = (PaymentMethods ?? []).Where(m => SiteRuntime.PaymentMethods.All.Contains(m)).Distinct().ToList();
        if (PaymentMethods.Count == 0) PaymentMethods = [SiteRuntime.PaymentMethods.CashOnDelivery];
        BankDetails = string.IsNullOrWhiteSpace(BankDetails) ? null : Text.Truncate(BankDetails.Trim(), 1000);

        var b = Booking ??= new BookingSettings();
        try { TimeZoneInfo.FindSystemTimeZoneById(b.TimeZone); }
        catch (Exception) { throw ApiException.BadRequest("المنطقة الزمنية غير معروفة (مثال: Asia/Dubai)"); }
        b.SlotMinutes = Math.Clamp(b.SlotMinutes, 5, 480);
        b.DaysAhead = Math.Clamp(b.DaysAhead, 1, 365);
        b.Capacity = Math.Clamp(b.Capacity, 1, 100);
        b.MinNoticeMinutes = Math.Clamp(b.MinNoticeMinutes, 0, 60 * 24 * 14);
        var week = new List<WorkingDay>();
        for (var d = 0; d < 7; d++)
        {
            var day = b.Week?.FirstOrDefault(w => w.Day == d) ?? new WorkingDay { Day = d, Closed = true };
            if (!TimeOnly.TryParse(day.Open, out var open) || !TimeOnly.TryParse(day.Close, out var close))
                throw ApiException.BadRequest("مواعيد العمل يجب أن تكون بالصيغة 09:00");
            if (!day.Closed && close <= open) throw ApiException.BadRequest("وقت الإغلاق يجب أن يكون بعد وقت الفتح");
            week.Add(new WorkingDay { Day = d, Open = open.ToString("HH:mm"), Close = close.ToString("HH:mm"), Closed = day.Closed });
        }
        b.Week = week;
        b.ClosedDates = (b.ClosedDates ?? []).Where(x => DateOnly.TryParseExact(x, "yyyy-MM-dd", out _)).Distinct().Take(366).ToList();
        return this;
    }
}

/// <summary>Cached, read-only view of a site for the public runtime API.</summary>
public record SiteRef(
    Guid Id,
    Guid OwnerId,
    string SiteKey,
    bool AdsRequireApproval,
    Guid? CurrentVersionId,
    Guid? PublishedVersionId,
    bool Published,
    string? HostingTier,
    DateTime? HostingPaidUntil,
    string SettingsJson,
    bool IsAdminSuspended = false)
{
    private SiteSettings? _settings;
    public SiteSettings Settings => _settings ??= SiteSettings.Parse(SettingsJson);
}

/// <summary>Per-request helpers shared by all public site endpoints.</summary>
public class SiteContext(AppDbContext db, IMemoryCache cache, TokenService tokens, IOptions<BillingOptions> billing, TimeProvider clock)
{
    /// <summary>Until backend hosting is paid, each data type accepts only this many rows (enough to try the site).</summary>
    public const int TrialLimit = 25;

    public AppDbContext Db => db;

    public static void Invalidate(IMemoryCache cache, string siteKey) => cache.Remove("sitekey:" + siteKey);

    /// <summary>Every public site API resolves the site here, so an admin-suspended site is refused for all of them (preview included).</summary>
    public async Task<SiteRef> ResolveAsync(string siteKey)
    {
        var key = "sitekey:" + siteKey;
        if (!cache.TryGetValue(key, out SiteRef? site) || site is null)
        {
            site = await db.Projects.AsNoTracking().Where(p => p.SiteKey == siteKey)
                       .Select(p => new SiteRef(p.Id, p.UserId, p.SiteKey, p.AdsRequireApproval, p.CurrentVersionId, p.PublishedVersionId, p.PublishedAt != null,
                           p.HostingTier, p.HostingPaidUntil, p.SettingsJson, p.IsAdminSuspended))
                       .FirstOrDefaultAsync()
                   ?? throw ApiException.NotFound("الموقع غير موجود");
            cache.Set(key, site, TimeSpan.FromSeconds(30));
        }
        if (site.IsAdminSuspended) throw Admin.SiteSuspension.VisitorError();
        return site;
    }

    public static bool IsPreview(HttpContext http) => http.Request.Headers["X-Casco-Preview"] == "1";

    /// <summary>The editor preview runs the latest draft; the live site runs what was published.</summary>
    public static Guid? VersionFor(SiteRef site, HttpContext http) =>
        IsPreview(http) || site.PublishedVersionId is null ? site.CurrentVersionId : site.PublishedVersionId;

    public bool IsBackendActive(SiteRef site) =>
        site.HostingTier == HostingTiers.Backend && site.HostingPaidUntil is { } until
        && until.AddDays(billing.Value.Hosting.GraceDays) > clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Backend features of a live site stop when its backend hosting is unpaid. Unpublished sites (and the editor preview)
    /// keep working in trial mode so owners can test before paying.
    /// </summary>
    public void EnsureBackend(SiteRef site, HttpContext http)
    {
        if (IsBackendActive(site)) return;
        if (site.Published && !IsPreview(http))
            throw ApiException.Payment("خدمات هذا الموقع متوقفة مؤقتاً. صاحب الموقع يحتاج لتفعيل استضافة الباك إند.", "backend_inactive");
    }

    /// <summary>Throws once a trial site reached <see cref="TrialLimit"/> rows of some kind.</summary>
    public async Task EnsureTrialCapacityAsync(SiteRef site, Func<Task<int>> count)
    {
        if (IsBackendActive(site)) return;
        if (await count() >= TrialLimit)
            throw ApiException.Payment($"وضع التجربة يسمح بـ {TrialLimit} عنصر فقط. فعّل استضافة الباك إند لاستقبال المزيد.", "backend_trial_limit");
    }

    public async Task<Guid?> SiteUserIdAsync(HttpContext http, SiteRef site)
    {
        var session = await tokens.ReadSiteSessionAsync(http.Request.Headers.Authorization, site.Id);
        if (session is null || await TokenRevocation.IsRevokedAsync(db, session.Value.Jti, http.RequestAborted))
            return null;
        return session.Value.UserId;
    }

    public async Task<SiteUser> RequireUserAsync(HttpContext http, SiteRef site)
    {
        var id = await SiteUserIdAsync(http, site) ?? throw new ApiException(401, "سجّل الدخول أولاً", "login_required");
        return await db.SiteUsers.FirstOrDefaultAsync(u => u.Id == id && u.ProjectId == site.Id)
               ?? throw new ApiException(401, "سجّل الدخول أولاً", "login_required");
    }

    /// <summary>Backend config of the site's current version (immutable per version, so cached by version id).</summary>
    public async Task<BackendConfig> ConfigAsync(Guid? versionId)
    {
        if (versionId is null) return BackendConfig.Empty;
        var key = "backend:" + versionId;
        if (cache.TryGetValue(key, out BackendConfig? cached) && cached is not null) return cached;
        var files = await LoadFilesAsync(versionId.Value);
        var config = BackendConfig.FromFiles(files);
        cache.Set(key, config, TimeSpan.FromMinutes(10));
        return config;
    }

    public async Task<string?> FunctionsCodeAsync(Guid? versionId)
    {
        if (versionId is null) return null;
        var files = await LoadFilesAsync(versionId.Value);
        return files.GetValueOrDefault(BackendConfig.FunctionsFile);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadFilesAsync(Guid versionId)
    {
        var key = "files:" + versionId;
        if (cache.TryGetValue(key, out IReadOnlyDictionary<string, string>? cached) && cached is not null) return cached;
        var json = await db.ProjectVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.FilesJson).FirstOrDefaultAsync();
        IReadOnlyDictionary<string, string> files = json is null ? new Dictionary<string, string>() : SiteFiles.Parse(json);
        // Only backend files are kept: HTML pages are not needed here.
        files = files.Where(f => BackendConfig.IsBackendFile(f.Key)).ToDictionary(f => f.Key, f => f.Value);
        cache.Set(key, files, TimeSpan.FromMinutes(10));
        return files;
    }

    public static string WhatsAppLink(string? number, string text)
    {
        var digits = new string((number ?? "").Where(char.IsDigit).ToArray());
        return digits.Length < 8 ? "" : $"https://wa.me/{digits}?text={Uri.EscapeDataString(text)}";
    }
}
