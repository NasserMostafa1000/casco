using Casco.Api.Domain;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public record PlanInfo(
    string Key,
    string? Interval,
    DateTime? PeriodEnd,
    bool IsPro,
    int MaxProjects,
    string DefaultTier,
    bool CanUsePremium,
    bool CanCustomDomain,
    bool CanMultiPage,
    bool ShowBadge,
    DateTime? CreditsRefillAt = null);

/// <summary>
/// Ziina has no recurring billing, so each payment buys a period (1 or 12 months).
/// Credits are granted monthly for both intervals and plan credits never roll over.
/// </summary>
public class SubscriptionService(AppDbContext db, CreditService credits, IOptions<BillingOptions> options, TimeProvider clock)
{
    private readonly BillingOptions _opt = options.Value;

    public async Task<Subscription> GetOrCreateAsync(Guid userId)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub is not null) return sub;
        sub = new Subscription { UserId = userId, Plan = PlanKeys.Free };
        db.Subscriptions.Add(sub);
        await db.SaveChangesAsync();
        return sub;
    }

    /// <summary>Applies expiry and due monthly credit grants. Safe to call on every request.</summary>
    public async Task<Subscription> RefreshAsync(Guid userId)
    {
        var sub = await GetOrCreateAsync(userId);
        var now = clock.GetUtcNow().UtcDateTime;

        if (sub.Plan == PlanKeys.Pro && (sub.CurrentPeriodEnd is null || sub.CurrentPeriodEnd <= now))
        {
            sub.Plan = PlanKeys.Free;
            sub.Interval = null;
            sub.NextCreditGrantAt = null;
            sub.UpdatedAt = now;
            await db.SaveChangesAsync();
            await credits.ExpirePlanCreditsAsync(userId, "subscription_expired");
            return sub;
        }

        if (sub.Plan == PlanKeys.Pro && sub.NextCreditGrantAt is { } due && due <= now)
        {
            var next = due;
            while (next <= now) next = next.AddMonths(1);
            sub.NextCreditGrantAt = next < sub.CurrentPeriodEnd ? next : null;
            sub.UpdatedAt = now;
            await db.SaveChangesAsync();

            var reference = $"grant:{due:yyyyMMdd}";
            await credits.ExpirePlanCreditsAsync(userId, reference);
            await credits.GrantAsync(userId, _opt.Pro.MonthlyCredits, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, reference);
        }
        return sub;
    }

    public async Task ActivateProAsync(Guid userId, string interval, string reference)
    {
        var sub = await GetOrCreateAsync(userId);
        var now = clock.GetUtcNow().UtcDateTime;
        var months = interval == BillingIntervals.Yearly ? 12 : 1;

        if (sub.IsProActive(now))
        {
            var oldEnd = sub.CurrentPeriodEnd!.Value;
            sub.CurrentPeriodEnd = oldEnd.AddMonths(months);
            sub.NextCreditGrantAt ??= oldEnd;
        }
        else
        {
            sub.Plan = PlanKeys.Pro;
            sub.CurrentPeriodStart = now;
            sub.CurrentPeriodEnd = now.AddMonths(months);
            sub.NextCreditGrantAt = now;
        }
        sub.Interval = interval;
        sub.UpdatedAt = now;
        await db.SaveChangesAsync();
        await RefreshAsync(userId);
    }

    public async Task<PlanInfo> GetPlanAsync(Guid userId)
    {
        var sub = await RefreshAsync(userId);
        return ToPlanInfo(sub);
    }

    public PlanInfo ToPlanInfo(Subscription sub)
    {
        var isPro = sub.IsProActive(clock.GetUtcNow().UtcDateTime);
        return isPro
            ? new PlanInfo(PlanKeys.Pro, sub.Interval, sub.CurrentPeriodEnd, true, _opt.Pro.MaxProjects, "standard", true, true, true, false,
                sub.NextCreditGrantAt ?? sub.CurrentPeriodEnd)
            : new PlanInfo(PlanKeys.Free, null, null, false, _opt.Free.MaxProjects, "cheap", false, false, false, true);
    }

    public static bool CanUseTemplate(PlanInfo plan, SiteTemplate template) =>
        plan.IsPro || template.Plan == PlanKeys.Free;
}
