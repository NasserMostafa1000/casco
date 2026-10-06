using System.Globalization;
using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Admin;

public record UsageRow(
    Guid? UserId, Guid? ProjectId, Guid? TaskId, string Provider, string Model, string Purpose, string? Plan,
    int InputTokens, int CachedTokens, int OutputTokens, decimal Estimated, decimal? Actual,
    int DurationMs, int RetryCount, bool Success, bool CacheHit, DateTime CreatedAt, int Part = 1)
{
    /// <summary>Best known cost: the provider's real bill when known, otherwise our estimate.</summary>
    public decimal Cost => Actual ?? Estimated;
    /// <summary>Old rows stored "edit#2"; the attempt now lives in RetryCount.</summary>
    public string Kind => Purpose.Split('#')[0];
}

public record CostDistribution(int Users, decimal Total, decimal Average, decimal Median, decimal P90, decimal Max);

/// <summary>Pure aggregation helpers (unit-tested).</summary>
public static class UnitEconomics
{
    public static CostDistribution Distribution(IEnumerable<decimal> perUserCosts)
    {
        var sorted = perUserCosts.OrderBy(c => c).ToList();
        if (sorted.Count == 0) return new CostDistribution(0, 0, 0, 0, 0, 0);
        var total = sorted.Sum();
        return new CostDistribution(sorted.Count, total, total / sorted.Count, Percentile(sorted, 0.5), Percentile(sorted, 0.9), sorted[^1]);
    }

    /// <summary>Linear-interpolated percentile of an ascending list.</summary>
    public static decimal Percentile(IReadOnlyList<decimal> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        var rank = (decimal)p * (sorted.Count - 1);
        var lo = (int)Math.Floor(rank);
        var hi = Math.Min(lo + 1, sorted.Count - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static Dictionary<Guid, decimal> CostPerUser(IEnumerable<UsageRow> rows) =>
        rows.Where(r => r.UserId is not null).GroupBy(r => r.UserId!.Value).ToDictionary(g => g.Key, g => g.Sum(r => r.Cost));

    /// <summary>How many values fall in each [edge, next edge) band; the last band is open-ended.</summary>
    public static List<(decimal From, decimal? To, int Count)> Bands(IEnumerable<decimal> values, params decimal[] edges)
    {
        var list = values.ToList();
        return edges.Select((from, i) =>
        {
            decimal? to = i + 1 < edges.Length ? edges[i + 1] : null;
            return (from, to, list.Count(v => v >= from && (to is null || v < to)));
        }).ToList();
    }

    /// <summary>Revenue minus AI cost minus the user's share of server cost (hosted sites × infra per site).</summary>
    public static decimal GrossMargin(decimal revenue, decimal aiCost, int hostedSites, decimal infraPerSite) =>
        revenue - aiCost - hostedSites * infraPerSite;

    /// <summary>Scales each estimated cost so they add up to the provider's invoice. Null when there is nothing to spread yet.</summary>
    public static decimal? ReconcileFactor(decimal invoiceUsd, decimal estimatedUsd) =>
        estimatedUsd > 0 ? invoiceUsd / estimatedUsd : null;

    public static (DateTime Start, DateTime End) MonthRange(string month)
    {
        if (!DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var start))
            throw ApiException.BadRequest("الشهر يجب أن يكون بالصيغة yyyy-MM");
        start = DateTime.SpecifyKind(start, DateTimeKind.Utc);
        return (start, start.AddMonths(1));
    }
}

/// <summary>
/// Answers "how much does one user cost me?" (not just "what did I pay OpenAI?"): AI cost per user and per plan,
/// what Pro's $18 leaves after AI and free-user costs, and hosting margins after server costs.
/// </summary>
public class EconomicsService(AppDbContext db, IOptions<BillingOptions> billing, TimeProvider clock)
{
    public async Task<object> ReportAsync(string? month)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        month ??= now.ToString("yyyy-MM", CultureInfo.InvariantCulture);
        var (start, end) = UnitEconomics.MonthRange(month);
        var isCurrent = now >= start && now < end;
        var daysInMonth = (end - start).Days;
        var daysElapsed = isCurrent ? Math.Max(1, (now - start).TotalDays) : daysInMonth;
        var opt = billing.Value;

        var rows = await db.AiUsages.AsNoTracking().Where(u => u.CreatedAt >= start && u.CreatedAt < end)
            .Select(u => new UsageRow(u.UserId, u.ProjectId, u.TaskId, u.Provider, u.Model, u.Purpose, u.Plan,
                u.InputTokens, u.CachedInputTokens, u.OutputTokens, u.EstimatedCostUsd, u.ActualCostUsd,
                u.DurationMs, u.RetryCount, u.Success, u.ResponseCacheHit, u.CreatedAt, u.Part))
            .ToListAsync();

        var totalCost = rows.Sum(r => r.Cost);
        var perUser = UnitEconomics.CostPerUser(rows);
        var overall = UnitEconomics.Distribution(perUser.Values);

        // Cost is attributed to the plan the user had when each call was made (a user who upgrades mid-month counts in both);
        // rows from before plans were recorded count as free.
        static string PlanOf(UsageRow r) => r.Plan ?? PlanKeys.Free;
        var planOf = rows.Where(r => r.UserId is not null).GroupBy(r => r.UserId!.Value)
            .ToDictionary(g => g.Key, g => PlanOf(g.OrderBy(r => r.CreatedAt).Last()));
        var byPlan = rows.GroupBy(PlanOf).Select(g =>
        {
            var d = UnitEconomics.Distribution(UnitEconomics.CostPerUser(g).Values);
            return new { plan = g.Key, users = d.Users, cost = g.Sum(r => r.Cost), avgPerUser = d.Average, median = d.Median, p90 = d.P90, max = d.Max };
        }).OrderBy(x => x.plan).ToList();

        // Pro subscribers during the month, including those who never used AI (they lower the true average).
        var proSubscribers = (await db.Subscriptions.AsNoTracking()
                .Where(s => s.Plan == PlanKeys.Pro && s.CurrentPeriodEnd >= start && (s.CurrentPeriodStart == null || s.CurrentPeriodStart < end))
                .Select(s => s.UserId).ToListAsync())
            .Concat(rows.Where(r => r.UserId is not null && PlanOf(r) == PlanKeys.Pro).Select(r => r.UserId!.Value))
            .Distinct().Count();
        var proAiCost = rows.Where(r => PlanOf(r) == PlanKeys.Pro).Sum(r => r.Cost);
        var freeAiCost = totalCost - proAiCost;
        var proPrice = opt.Pro.MonthlyPriceMinor / 100m;
        var fee = proPrice * opt.Economics.PaymentFeePercent / 100m;
        var proAiPerSubscriber = proSubscribers > 0 ? proAiCost / proSubscribers : 0;
        var freePerSubscriber = proSubscribers > 0 ? freeAiCost / proSubscribers : freeAiCost;

        var signupSet = (await db.Users.Where(u => u.CreatedAt >= start && u.CreatedAt < end).Select(u => u.Id).ToListAsync()).ToHashSet();
        var signups = signupSet.Count;
        var newUserCost = perUser.Where(kv => signupSet.Contains(kv.Key)).Sum(kv => kv.Value);

        var payments = await db.Payments.AsNoTracking()
            .Where(p => p.Status == PaymentStatuses.Completed && !p.IsTest && p.CompletedAt >= start && p.CompletedAt < end)
            .Select(p => new { p.UserId, p.Kind, p.AmountMinor }).ToListAsync();
        decimal Revenue(string? kind) => payments.Where(p => kind is null || p.Kind == kind).Sum(p => p.AmountMinor) / 100m;
        var payingUsers = payments.Select(p => p.UserId).Distinct().Count();

        var graceStart = start.AddDays(-opt.Hosting.GraceDays);
        var hosted = await db.Projects.AsNoTracking()
            .Where(p => p.HostingTier != null && p.HostingPaidUntil >= graceStart)
            .GroupBy(p => p.HostingTier).Select(g => new { tier = g.Key, count = g.Count() }).ToListAsync();
        int Sites(string tier) => hosted.FirstOrDefault(h => h.tier == tier)?.count ?? 0;
        var hostedSites = hosted.Sum(h => h.count);
        var accountCosts = await db.AccountEntries.AsNoTracking().Select(e => new { e.Cadence, e.AmountUsd, e.OnDate }).ToListAsync();
        var runRate = AccountsMath.MonthlyRunRate(accountCosts.Select(e => (e.Cadence, e.AmountUsd, e.OnDate)), now);
        var platformMonthly = accountCosts.Count > 0 ? runRate : opt.Economics.PlatformMonthlyUsd;
        var infraPerSite = hostedSites > 0 ? platformMonthly / hostedSites : platformMonthly;
        var staticPrice = opt.Hosting.MonthlyMinor(HostingTiers.Static) / 100m;
        var backendPrice = opt.Hosting.MonthlyMinor(HostingTiers.Backend) / 100m;
        var feePct = opt.Economics.PaymentFeePercent / 100m;

        var userIds = perUser.Keys.ToList();
        var emails = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.Name }).ToDictionaryAsync(u => u.Id);

        var succeeded = rows.Where(r => r.Success && !r.CacheHit).Select(r => (decimal)r.DurationMs).OrderBy(d => d).ToList();

        // One user request = one task, whatever number of parts and fix rounds it took.
        var perTask = rows.Where(r => r.TaskId is not null).GroupBy(r => r.TaskId!.Value)
            .Select(g => new { TaskId = g.Key, ProjectId = g.First().ProjectId, UserId = g.First().UserId, Cost = g.Sum(r => r.Cost), Parts = g.Max(r => r.Part), Calls = g.Count() })
            .ToList();
        var multiPart = perTask.Where(t => t.Parts > 1).ToList();
        var perProject = rows.Where(r => r.ProjectId is not null).GroupBy(r => r.ProjectId!.Value)
            .ToDictionary(g => g.Key, g => (Cost: g.Sum(r => r.Cost), Tasks: g.Where(r => r.TaskId != null).Select(r => r.TaskId).Distinct().Count()));
        var topProjectIds = perProject.OrderByDescending(kv => kv.Value.Cost).Take(10).Select(kv => kv.Key).ToList();
        var topTaskIds = perTask.OrderByDescending(t => t.Cost).Take(10).Select(t => t.TaskId).ToList();
        var namedProjectIds = topProjectIds
            .Concat(perTask.Where(t => topTaskIds.Contains(t.TaskId) && t.ProjectId != null).Select(t => t.ProjectId!.Value)).Distinct().ToList();
        var projectNames = await db.Projects.AsNoTracking().Where(p => namedProjectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.UserId }).ToDictionaryAsync(p => p.Id);
        var taskPrompts = await db.AgentTasks.AsNoTracking().Where(t => topTaskIds.Contains(t.Id)).Select(t => new { t.Id, t.Prompt, t.Kind }).ToDictionaryAsync(t => t.Id);

        // Gross margin per Pro user: what they paid this month minus their AI cost and the server share of their hosted sites.
        var revenueByUser = payments.GroupBy(p => p.UserId).ToDictionary(g => g.Key, g => g.Sum(p => p.AmountMinor) / 100m);
        var hostedByUser = await db.Projects.AsNoTracking()
            .Where(p => p.HostingTier != null && p.HostingPaidUntil >= graceStart)
            .GroupBy(p => p.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
        var proUserIds = planOf.Where(kv => kv.Value == PlanKeys.Pro).Select(kv => kv.Key).ToList();
        var margins = proUserIds.Select(uid => new
        {
            UserId = uid,
            Revenue = revenueByUser.GetValueOrDefault(uid),
            AiCost = perUser.GetValueOrDefault(uid),
            Margin = UnitEconomics.GrossMargin(revenueByUser.GetValueOrDefault(uid), perUser.GetValueOrDefault(uid), hostedByUser.GetValueOrDefault(uid), infraPerSite)
        }).ToList();
        var marginEmails = await db.Users.AsNoTracking().Where(u => proUserIds.Contains(u.Id)).Select(u => new { u.Id, u.Email }).ToDictionaryAsync(u => u.Id, u => u.Email);

        return new
        {
            month,
            from = start,
            to = end,
            isCurrentMonth = isCurrent,
            daysElapsed = Math.Round(daysElapsed, 1),
            daysInMonth,
            ai = new
            {
                cost = totalCost,
                estimatedCost = rows.Sum(r => r.Estimated),
                actualKnownShare = totalCost > 0 ? rows.Where(r => r.Actual is not null).Sum(r => r.Cost) / totalCost : 0,
                projectedMonthCost = isCurrent ? totalCost / (decimal)daysElapsed * daysInMonth : totalCost,
                calls = rows.Count,
                tasks = rows.Where(r => r.TaskId is not null).Select(r => r.TaskId).Distinct().Count(),
                failures = rows.Count(r => !r.Success),
                failedCost = rows.Where(r => !r.Success).Sum(r => r.Cost),
                cacheHits = rows.Count(r => r.CacheHit),
                retries = rows.Count(r => r.RetryCount > 0),
                retryCost = rows.Where(r => r.RetryCount > 0).Sum(r => r.Cost),
                avgDurationMs = succeeded.Count > 0 ? (int)succeeded.Average() : 0,
                p90DurationMs = (int)UnitEconomics.Percentile(succeeded, 0.9),
                inputTokens = rows.Sum(r => (long)r.InputTokens),
                cachedTokens = rows.Sum(r => (long)r.CachedTokens),
                outputTokens = rows.Sum(r => (long)r.OutputTokens)
            },
            perUser = new
            {
                activeUsers = overall.Users,
                average = overall.Average,
                median = overall.Median,
                p90 = overall.P90,
                max = overall.Max,
                totalUsers = await db.Users.CountAsync(),
                signups,
                costPerSignup = signups > 0 ? newUserCost / signups : 0
            },
            byPlan,
            pro = new
            {
                monthlyPrice = proPrice,
                subscribers = proSubscribers,
                aiCost = proAiCost,
                aiCostPerSubscriber = proAiPerSubscriber,
                shareOfPrice = proPrice > 0 ? proAiPerSubscriber / proPrice : 0,
                freeUsersAiCost = freeAiCost,
                freeCostPerSubscriber = freePerSubscriber,
                paymentFee = fee,
                marginPerSubscriber = proPrice - fee - proAiPerSubscriber - freePerSubscriber
            },
            revenue = new
            {
                total = Revenue(null),
                subscriptions = Revenue(PaymentKinds.Subscription),
                hosting = Revenue(PaymentKinds.Hosting),
                topups = Revenue(PaymentKinds.Topup),
                payingUsers,
                perPayingUser = payingUsers > 0 ? Revenue(null) / payingUsers : 0,
                aiCostShare = Revenue(null) > 0 ? totalCost / Revenue(null) : 0
            },
            hosting = new
            {
                staticPrice,
                backendPrice,
                staticSites = Sites(HostingTiers.Static),
                backendSites = Sites(HostingTiers.Backend),
                infraMonthlyUsd = platformMonthly,
                domainMonthlyUsd = 0m,
                emailMonthlyUsd = 0m,
                platformMonthlyUsd = platformMonthly,
                expenses = Array.Empty<object>(),
                infraPerSite,
                staticMargin = staticPrice * (1 - feePct) - infraPerSite,
                backendMargin = backendPrice * (1 - feePct) - infraPerSite,
                monthlyRunRate = Sites(HostingTiers.Static) * staticPrice + Sites(HostingTiers.Backend) * backendPrice
            },
            requests = new
            {
                cost = UnitEconomics.Distribution(perTask.Select(t => t.Cost)),
                multiPart = multiPart.Count,
                multiPartShare = perTask.Count > 0 ? (decimal)multiPart.Count / perTask.Count : 0,
                multiPartAvgCost = multiPart.Count > 0 ? multiPart.Average(t => t.Cost) : 0,
                avgParts = multiPart.Count > 0 ? Math.Round(multiPart.Average(t => t.Parts), 1) : 0,
                maxParts = perTask.Count > 0 ? perTask.Max(t => t.Parts) : 0,
                bands = UnitEconomics.Bands(perTask.Select(t => t.Cost), 0m, 0.01m, 0.05m, 0.2m, 0.5m, 1m)
                    .Select(b => new { from = b.From, to = b.To, count = b.Count }),
                top = perTask.OrderByDescending(t => t.Cost).Take(10).Select(t => new
                {
                    taskId = t.TaskId,
                    project = t.ProjectId is { } pid ? projectNames.GetValueOrDefault(pid)?.Name : null,
                    email = t.UserId is { } uid ? emails.GetValueOrDefault(uid)?.Email : null,
                    kind = taskPrompts.GetValueOrDefault(t.TaskId)?.Kind,
                    prompt = taskPrompts.GetValueOrDefault(t.TaskId) is { } tp ? Text.Truncate(Projects.ProjectService.WithoutImagesNote(tp.Prompt), 120) : null,
                    t.Cost, t.Parts, t.Calls
                })
            },
            projects = new
            {
                cost = UnitEconomics.Distribution(perProject.Values.Select(v => v.Cost)),
                top = topProjectIds.Select(pid => new
                {
                    projectId = pid,
                    name = projectNames.GetValueOrDefault(pid)?.Name,
                    email = projectNames.GetValueOrDefault(pid) is { } p ? emails.GetValueOrDefault(p.UserId)?.Email : null,
                    cost = perProject[pid].Cost,
                    tasks = perProject[pid].Tasks
                })
            },
            proMargins = new
            {
                users = margins.Count,
                aiCostBands = UnitEconomics.Bands(margins.Select(m => m.AiCost), 0m, 1m, 3m, 7m, 12m, proPrice)
                    .Select(b => new { from = b.From, to = b.To, count = b.Count }),
                margin = UnitEconomics.Distribution(margins.Select(m => m.Margin)),
                negative = margins.Count(m => m.Margin < 0),
                worst = margins.OrderBy(m => m.Margin).Take(10).Select(m => new
                {
                    userId = m.UserId, email = marginEmails.GetValueOrDefault(m.UserId), revenue = m.Revenue, aiCost = m.AiCost, margin = m.Margin
                })
            },
            byPurpose = rows.GroupBy(r => r.Kind).Select(g =>
            {
                var tasks = g.Where(r => r.TaskId is not null).Select(r => r.TaskId).Distinct().Count();
                return new
                {
                    purpose = g.Key,
                    tasks,
                    calls = g.Count(),
                    cost = g.Sum(r => r.Cost),
                    avgPerTask = tasks > 0 ? g.Sum(r => r.Cost) / tasks : 0,
                    callsPerTask = tasks > 0 ? Math.Round((double)g.Count() / tasks, 2) : 0
                };
            }).OrderByDescending(x => x.cost).ToList(),
            byModel = rows.GroupBy(r => (r.Provider, r.Model)).Select(g => new
            {
                provider = g.Key.Provider,
                model = g.Key.Model,
                calls = g.Count(),
                failures = g.Count(r => !r.Success),
                cacheHits = g.Count(r => r.CacheHit),
                cost = g.Sum(r => r.Cost),
                avgDurationMs = g.Any(r => r.Success && !r.CacheHit) ? (int)g.Where(r => r.Success && !r.CacheHit).Average(r => r.DurationMs) : 0
            }).OrderByDescending(x => x.cost).ToList(),
            byProvider = rows.GroupBy(r => r.Provider).Select(g => new
            {
                provider = g.Key,
                estimatedCost = g.Where(r => !r.CacheHit).Sum(r => r.Estimated),
                cost = g.Sum(r => r.Cost)
            }).OrderByDescending(x => x.cost).ToList(),
            topUsers = perUser.OrderByDescending(kv => kv.Value).Take(15).Select(kv => new
            {
                userId = kv.Key,
                email = emails.GetValueOrDefault(kv.Key)?.Email,
                name = emails.GetValueOrDefault(kv.Key)?.Name,
                plan = planOf.GetValueOrDefault(kv.Key, PlanKeys.Free),
                cost = kv.Value,
                calls = rows.Count(r => r.UserId == kv.Key),
                tasks = rows.Where(r => r.UserId == kv.Key && r.TaskId != null).Select(r => r.TaskId).Distinct().Count(),
                projects = rows.Where(r => r.UserId == kv.Key && r.ProjectId != null).Select(r => r.ProjectId).Distinct().Count()
            }).ToList(),
            daily = rows.GroupBy(r => r.CreatedAt.Date).OrderBy(g => g.Key).Select(g => new
            {
                date = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                cost = g.Sum(r => r.Cost),
                calls = g.Count(),
                users = g.Where(r => r.UserId != null).Select(r => r.UserId).Distinct().Count()
            }).ToList()
        };
    }
}
