using System.Text.RegularExpressions;
using Casco.Api.Domain;
using Casco.Api.Features.Backend;
using Casco.Api.Features.Publishing;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

/// <summary>
/// Per-site hosting on Casco servers: "static" serves the pages and contact forms,
/// "backend" adds accounts, database, store, bookings, courses, ads and server functions (prices are admin-editable).
/// </summary>
public partial class HostingService(AppDbContext db, IMemoryCache cache, SiteResolver resolver, IOptions<BillingOptions> billing,
    IOptions<AppOptions> app, TimeProvider clock)
{
    private const string BackendModules = "auth|db|store|cart|bookings|courses|ads|fn";

    [GeneratedRegex($@"\bCasco\s*\.\s*({BackendModules})\b")]
    private static partial Regex BackendUsage();

    /// <summary>Matches <c>C = window.Casco</c> / <c>const api = Casco</c> so aliased SDK calls are detected too.</summary>
    [GeneratedRegex(@"([A-Za-z_$][\w$]*)\s*=\s*(?:window\s*\.\s*)?Casco\b(?!\s*\.)")]
    private static partial Regex SdkAlias();

    public HostingOptions Options => billing.Value.Hosting;

    /// <summary>Backend SDK modules referenced by one page or script, directly or through an alias of the SDK object.</summary>
    public static IEnumerable<string> UsedModules(string content)
    {
        foreach (Match m in BackendUsage().Matches(content)) yield return m.Groups[1].Value;
        foreach (var alias in SdkAlias().Matches(content).Select(m => m.Groups[1].Value).Where(a => a != "Casco").Distinct())
            foreach (Match m in Regex.Matches(content, $@"(?<![\w$.]){Regex.Escape(alias)}\s*\.\s*({BackendModules})\b"))
                yield return m.Groups[1].Value;
    }

    private static bool IsClientCode(string path) =>
        !BackendConfig.IsBackendFile(path) && (path.EndsWith(".html", StringComparison.Ordinal) || path.EndsWith(".js", StringComparison.Ordinal));

    /// <summary>Backend hosting is needed as soon as the site uses anything beyond static pages and contact forms.</summary>
    public static string RequiredTier(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (path, content) in files)
        {
            if (BackendConfig.IsBackendFile(path)) return HostingTiers.Backend;
            if (IsClientCode(path) && UsedModules(content).Any()) return HostingTiers.Backend;
        }
        return HostingTiers.Static;
    }

    /// <summary>Backend modules the site uses (drives which dashboard tabs are shown).</summary>
    public static List<string> Features(IReadOnlyDictionary<string, string> files)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            if (!IsClientCode(path)) continue;
            foreach (var module in UsedModules(content))
                found.Add(module switch { "cart" => "store", "fn" => "db", var v => v });
        }
        if (files.TryGetValue(BackendConfig.FileName, out var json) && BackendConfig.Parse(json, out _).Collections.Count > 0) found.Add("db");
        return [.. found];
    }

    public async Task<string> RequiredTierAsync(Project project)
    {
        var versionId = project.CurrentVersionId ?? project.PublishedVersionId;
        if (versionId is null) return HostingTiers.Static;
        var json = await db.ProjectVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.FilesJson).FirstOrDefaultAsync();
        return json is null ? HostingTiers.Static : RequiredTier(SiteFiles.Parse(json));
    }

    public bool IsActive(Project project, string requiredTier = HostingTiers.Static) =>
        HostingTiers.Rank(project.HostingTier) >= HostingTiers.Rank(requiredTier)
        && project.HostingPaidUntil is { } until && until.AddDays(Options.GraceDaysFor(project.HostingTier)) > clock.GetUtcNow().UtcDateTime;

    public object Prices() => new
    {
        currency = billing.Value.Currency,
        @static = new { monthly = Options.PriceMinor(HostingTiers.Static, BillingIntervals.Monthly) / 100m, yearly = Options.PriceMinor(HostingTiers.Static, BillingIntervals.Yearly) / 100m },
        backend = new { monthly = Options.PriceMinor(HostingTiers.Backend, BillingIntervals.Monthly) / 100m, yearly = Options.PriceMinor(HostingTiers.Backend, BillingIntervals.Yearly) / 100m },
        graceDays = Options.GraceDays
    };

    public async Task<object> StatusAsync(Project project)
    {
        var required = await RequiredTierAsync(project);
        var now = clock.GetUtcNow().UtcDateTime;
        return new
        {
            requiredTier = required,
            tier = project.HostingTier,
            paidUntil = project.HostingPaidUntil,
            active = IsActive(project, HostingTiers.Static),
            enough = IsActive(project, required),
            inGrace = project.HostingPaidUntil is { } u && u <= now && IsActive(project, HostingTiers.Static),
            daysLeft = project.HostingPaidUntil is { } until ? Math.Max(0, (int)Math.Ceiling((until - now).TotalDays)) : 0,
            suspended = project.IsAdminSuspended,
            suspensionReason = project.AdminSuspensionReason,
            suspendedAt = project.AdminSuspendedAt,
            prices = Prices()
        };
    }

    public ApiException HostingRequired(string requiredTier)
    {
        var message = requiredTier == HostingTiers.React
            ? $"رفع تطبيق React يحتاج استضافة سنوية على Casco (${Options.ReactYearlyMinor / 100m:0.##} في السنة)."
            : requiredTier == HostingTiers.Backend
                ? $"نشر هذا الموقع يحتاج استضافة باك إند على سيرفرات Casco (${Options.MonthlyMinor(requiredTier) / 100m:0.##} شهرياً) لأنه يستخدم حسابات أو قاعدة بيانات أو طلبات أو حجوزات."
                : $"نشر الموقع يحتاج استضافة على سيرفرات Casco (${Options.MonthlyMinor(requiredTier) / 100m:0.##} شهرياً).";
        return new ApiException(402, message, "hosting_required") { Details = new { requiredTier, prices = Prices() } };
    }

    /// <summary>
    /// Adds <paramref name="months"/> of hosting. Unused time on another tier is converted by price,
    /// so upgrading static → backend (or the reverse) never loses paid days.
    /// </summary>
    public static DateTime ExtendUntil(DateTime now, string? currentTier, DateTime? paidUntil, string newTier, int months, HostingOptions options)
    {
        var start = now;
        if (paidUntil is { } until && until > now && HostingTiers.Rank(currentTier) > 0)
        {
            var remaining = until - now;
            if (currentTier != newTier)
                remaining = TimeSpan.FromTicks((long)(remaining.Ticks * (double)options.MonthlyMinor(currentTier!) / options.MonthlyMinor(newTier)));
            start = now + remaining;
        }
        return start.AddMonths(months);
    }

    public async Task ActivateAsync(Guid projectId, string tier, int months)
    {
        if (HostingTiers.Rank(tier) == 0) throw ApiException.BadRequest("نوع الاستضافة غير صالح");
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId) ?? throw ApiException.NotFound("الموقع غير موجود");
        if (tier == HostingTiers.React)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var start = project.HostingTier == HostingTiers.React && project.HostingPaidUntil is { } until && until > now ? until : now;
            project.HostingPaidUntil = start.AddYears(Math.Max(1, months / 12));
            project.HostingTier = HostingTiers.React;
            await db.SaveChangesAsync();
            Invalidate(project);
            return;
        }
        project.HostingPaidUntil = ExtendUntil(clock.GetUtcNow().UtcDateTime, project.HostingTier, project.HostingPaidUntil, tier, Math.Clamp(months, 1, 36), Options);
        project.HostingTier = tier;
        await db.SaveChangesAsync();
        Invalidate(project);
    }

    public void Invalidate(Project project)
    {
        SiteContext.Invalidate(cache, project.SiteKey);
        resolver.Invalidate($"{project.Slug}.{app.Value.SitesDomain}", project.CustomDomain);
    }
}
