using System.Text.Json;
using System.Text.RegularExpressions;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public record TopupPackPrice(string Id, int Credits, decimal Price);

public record OperatingExpensePrice(string Name, decimal MonthlyUsd);

/// <summary>Everything an admin can change about pricing, in dollars (the options store minor units).</summary>
public record PricingSettings(
    decimal ProMonthlyPrice,
    decimal ProYearlyPrice,
    int ProMonthlyCredits,
    int ProMaxProjects,
    int FreeMaxProjects,
    int SignupBonusCredits,
    decimal HostingStaticMonthly,
    decimal HostingBackendMonthly,
    int HostingYearlyPricedMonths,
    int HostingGraceDays,
    List<TopupPackPrice> TopupPacks,
    decimal UsdPerCredit,
    decimal InfraMonthlyUsd,
    decimal PaymentFeePercent,
    decimal DomainMonthlyUsd = 0,
    decimal EmailMonthlyUsd = 0,
    List<OperatingExpensePrice>? OperatingExpenses = null)
{
    public static PricingSettings From(BillingOptions b) => new(
        b.Pro.MonthlyPriceMinor / 100m, b.Pro.YearlyPriceMinor / 100m, b.Pro.MonthlyCredits, b.Pro.MaxProjects,
        b.Free.MaxProjects, b.SignupBonusCredits,
        b.Hosting.StaticMonthlyMinor / 100m, b.Hosting.BackendMonthlyMinor / 100m, b.Hosting.YearlyPricedMonths, b.Hosting.GraceDays,
        b.TopupPacks.Select(p => new TopupPackPrice(p.Id, p.Credits, p.PriceMinor / 100m)).ToList(),
        b.UsdPerCredit, b.Economics.PlatformMonthlyUsd, b.Economics.PaymentFeePercent,
        0, 0, []);

    public static List<OperatingExpensePrice> NamedExpenses(EconomicsOptions e)
    {
        if (e.OperatingExpenses.Count > 0)
            return e.OperatingExpenses.Select(x => new OperatingExpensePrice(x.Name, x.MonthlyUsd)).ToList();
        var list = new List<OperatingExpensePrice>();
        if (e.InfraMonthlyUsd > 0) list.Add(new("السيرفرات", e.InfraMonthlyUsd));
        if (e.DomainMonthlyUsd > 0) list.Add(new("الدومين", e.DomainMonthlyUsd));
        if (e.EmailMonthlyUsd > 0) list.Add(new("البريد", e.EmailMonthlyUsd));
        return list;
    }
}

/// <summary>
/// Admin-editable prices stored in AppSettings on top of the "Billing" configuration.
/// Every service reads <see cref="IOptions{BillingOptions}"/>, whose value is a single shared instance for the
/// app's lifetime, so overrides are written into that instance and take effect immediately without a restart.
/// </summary>
public partial class PricingService(IOptions<BillingOptions> options, IServiceScopeFactory scopes, ILogger<PricingService> logger)
{
    public const string SettingKey = "billing.pricing";
    private readonly PricingSettings _defaults = PricingSettings.From(options.Value);
    private readonly Lock _gate = new();

    public PricingSettings Defaults => _defaults;
    public PricingSettings Current => PricingSettings.From(options.Value);
    public DateTime? UpdatedAt { get; private set; }

    [GeneratedRegex("^[a-z0-9_-]{1,32}$")]
    private static partial Regex PackId();

    public async Task LoadAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == SettingKey);
        if (setting is null) return;
        try
        {
            Apply(Validate(JsonSerializer.Deserialize<PricingSettings>(setting.Value)!));
            UpdatedAt = setting.UpdatedAt;
        }
        catch (Exception e) when (e is JsonException or ApiException or NullReferenceException)
        {
            logger.LogError("Saved prices are invalid ({Error}); using the configured defaults", e.Message);
        }
    }

    public async Task<PricingSettings> SaveAsync(PricingSettings settings)
    {
        var valid = Validate(settings);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var json = JsonSerializer.Serialize(valid);
        var now = DateTime.UtcNow;
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == SettingKey);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = SettingKey, Value = json, UpdatedAt = now });
        else { setting.Value = json; setting.UpdatedAt = now; }
        await db.SaveChangesAsync();
        Apply(valid);
        UpdatedAt = now;
        return Current;
    }

    public async Task<PricingSettings> ResetAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AppSettings.Where(s => s.Key == SettingKey).ExecuteDeleteAsync();
        Apply(_defaults);
        UpdatedAt = null;
        return Current;
    }

    private void Apply(PricingSettings s)
    {
        var b = options.Value;
        lock (_gate)
        {
            b.Pro.MonthlyPriceMinor = ToMinor(s.ProMonthlyPrice);
            b.Pro.YearlyPriceMinor = ToMinor(s.ProYearlyPrice);
            b.Pro.MonthlyCredits = s.ProMonthlyCredits;
            b.Pro.MaxProjects = s.ProMaxProjects;
            b.Free.MaxProjects = s.FreeMaxProjects;
            b.SignupBonusCredits = s.SignupBonusCredits;
            b.Hosting.StaticMonthlyMinor = ToMinor(s.HostingStaticMonthly);
            b.Hosting.BackendMonthlyMinor = ToMinor(s.HostingBackendMonthly);
            b.Hosting.YearlyPricedMonths = s.HostingYearlyPricedMonths;
            b.Hosting.GraceDays = s.HostingGraceDays;
            b.TopupPacks = s.TopupPacks.Select(p => new TopupPack { Id = p.Id, Credits = p.Credits, PriceMinor = ToMinor(p.Price) }).ToList();
            b.UsdPerCredit = s.UsdPerCredit;
            b.Economics.PaymentFeePercent = s.PaymentFeePercent;
        }
    }

    private static int ToMinor(decimal dollars) => (int)Math.Round(dollars * 100m, MidpointRounding.AwayFromZero);

    public static PricingSettings Validate(PricingSettings s)
    {
        static void Range(decimal value, decimal min, decimal max, string name)
        {
            if (value < min || value > max) throw ApiException.BadRequest($"{name} يجب أن يكون بين {min:0.####} و {max:0.####}");
        }

        Range(s.ProMonthlyPrice, 1, 1000, "سعر Pro الشهري");
        Range(s.ProYearlyPrice, 1, 10000, "سعر Pro السنوي");
        Range(s.ProMonthlyCredits, 0, 1_000_000, "نقاط Pro الشهرية");
        Range(s.ProMaxProjects, 1, 1000, "عدد مواقع Pro");
        Range(s.FreeMaxProjects, 1, 100, "عدد مواقع الخطة المجانية");
        Range(s.SignupBonusCredits, 0, 100_000, "نقاط التسجيل");
        Range(s.HostingStaticMonthly, 1, 1000, "سعر استضافة الموقع العادي");
        Range(s.HostingBackendMonthly, 1, 1000, "سعر استضافة الباك إند");
        Range(s.HostingYearlyPricedMonths, 1, 12, "عدد الشهور المدفوعة في السنوي");
        Range(s.HostingGraceDays, 0, 30, "أيام السماح");
        Range(s.UsdPerCredit, 0.0001m, 1, "قيمة النقطة بالدولار");
        Range(s.PaymentFeePercent, 0, 30, "نسبة رسوم الدفع");
        if (s.HostingBackendMonthly < s.HostingStaticMonthly)
            throw ApiException.BadRequest("استضافة الباك إند لا يمكن أن تكون أرخص من الاستضافة العادية");

        var packs = s.TopupPacks ?? [];
        if (packs.Count > 10) throw ApiException.BadRequest("الحد الأقصى 10 باقات شحن");
        var normalized = new List<TopupPackPrice>();
        foreach (var p in packs)
        {
            var id = (p.Id ?? "").Trim().ToLowerInvariant();
            if (!PackId().IsMatch(id)) throw ApiException.BadRequest($"معرّف الباقة \"{p.Id}\" غير صالح (حروف إنجليزية صغيرة وأرقام فقط)");
            if (normalized.Any(x => x.Id == id)) throw ApiException.BadRequest($"معرّف الباقة \"{id}\" مكرر");
            Range(p.Credits, 1, 10_000_000, $"نقاط الباقة {id}");
            Range(p.Price, 1, 10000, $"سعر الباقة {id}");
            normalized.Add(new TopupPackPrice(id, p.Credits, Math.Round(p.Price, 2)));
        }

        return s with
        {
            ProMonthlyPrice = Math.Round(s.ProMonthlyPrice, 2),
            ProYearlyPrice = Math.Round(s.ProYearlyPrice, 2),
            HostingStaticMonthly = Math.Round(s.HostingStaticMonthly, 2),
            HostingBackendMonthly = Math.Round(s.HostingBackendMonthly, 2),
            TopupPacks = normalized,
            OperatingExpenses = [],
            DomainMonthlyUsd = 0,
            EmailMonthlyUsd = 0
        };
    }
}
