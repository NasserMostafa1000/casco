using Casco.Api.Domain;
using Casco.Api.Features.Auth;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Projects;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Admin;

public static class UserFilters
{
    public const string All = "all";
    /// <summary>Has at least one site.</summary>
    public const string Built = "built";
    /// <summary>Has at least one real (non-test) completed payment, including payments recorded manually by an admin.</summary>
    public const string Paid = "paid";
    public const string Unpaid = "unpaid";
    public const string BuiltUnpaid = "built_unpaid";
    public const string NoSite = "no_site";
    public const string Pro = "pro";
    /// <summary>Has a published site whose hosting ran out (past the grace days), so visitors see it stopped.</summary>
    public const string Stopped = "stopped";
    /// <summary>Has a site suspended by an admin.</summary>
    public const string Suspended = "suspended";

    public static readonly string[] Values = [All, Built, Paid, Unpaid, BuiltUnpaid, NoSite, Pro, Stopped, Suspended];
}

public static class AdminUsers
{
    private const int PageSize = 50;

    public static void MapAdminUserEndpoints(this RouteGroupBuilder g)
    {
        g.MapGet("/users", async (string? search, string? filter, string? sort, int? page, AppDbContext db, IOptions<BillingOptions> billing) =>
        {
            var now = DateTime.UtcNow;
            var stoppedBefore = now.AddDays(-billing.Value.Hosting.GraceDays);
            var users = db.Users.AsQueryable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var s = search.Trim();
                var lower = s.ToLowerInvariant();
                users = users.Where(u => u.Email.Contains(lower) || u.Name.Contains(s)
                    || db.Projects.Any(p => p.UserId == u.Id && (p.Name.Contains(s) || p.Slug.Contains(lower) || (p.CustomDomain != null && p.CustomDomain.Contains(lower)))));
            }

            IQueryable<User> Built(IQueryable<User> q) => q.Where(u => db.Projects.Any(p => p.UserId == u.Id));
            IQueryable<User> Paid(IQueryable<User> q) => q.Where(u => db.Payments.Any(p => p.UserId == u.Id && p.Status == PaymentStatuses.Completed && !p.IsTest));
            IQueryable<User> Unpaid(IQueryable<User> q) => q.Where(u => !db.Payments.Any(p => p.UserId == u.Id && p.Status == PaymentStatuses.Completed && !p.IsTest));
            IQueryable<User> Pro(IQueryable<User> q) => q.Where(u => u.Subscription != null && u.Subscription.Plan == PlanKeys.Pro && u.Subscription.CurrentPeriodEnd > now);
            IQueryable<User> Stopped(IQueryable<User> q) => q.Where(u => db.Projects.Any(p => p.UserId == u.Id && p.PublishedAt != null
                && (p.HostingPaidUntil == null || p.HostingPaidUntil <= stoppedBefore)));
            IQueryable<User> Suspended(IQueryable<User> q) => q.Where(u => db.Projects.Any(p => p.UserId == u.Id && p.IsAdminSuspended));

            var counts = new Dictionary<string, int>
            {
                [UserFilters.All] = await users.CountAsync(),
                [UserFilters.Built] = await Built(users).CountAsync(),
                [UserFilters.Paid] = await Paid(users).CountAsync(),
                [UserFilters.Unpaid] = await Unpaid(users).CountAsync(),
                [UserFilters.BuiltUnpaid] = await Unpaid(Built(users)).CountAsync(),
                [UserFilters.NoSite] = await users.CountAsync(u => !db.Projects.Any(p => p.UserId == u.Id)),
                [UserFilters.Pro] = await Pro(users).CountAsync(),
                [UserFilters.Stopped] = await Stopped(users).CountAsync(),
                [UserFilters.Suspended] = await Suspended(users).CountAsync()
            };

            var filtered = filter switch
            {
                UserFilters.Built => Built(users),
                UserFilters.Paid => Paid(users),
                UserFilters.Unpaid => Unpaid(users),
                UserFilters.BuiltUnpaid => Unpaid(Built(users)),
                UserFilters.NoSite => users.Where(u => !db.Projects.Any(p => p.UserId == u.Id)),
                UserFilters.Pro => Pro(users),
                UserFilters.Stopped => Stopped(users),
                UserFilters.Suspended => Suspended(users),
                _ => users
            };

            var rows = filtered.Select(u => new
            {
                u.Id, u.Email, u.Name, u.Role, u.CreatedAt,
                google = u.GoogleId != null,
                apple = u.AppleId != null,
                plan = u.Subscription != null && u.Subscription.Plan == PlanKeys.Pro && u.Subscription.CurrentPeriodEnd > now ? PlanKeys.Pro : PlanKeys.Free,
                periodEnd = u.Subscription != null ? u.Subscription.CurrentPeriodEnd : null,
                credits = db.CreditEntries.Where(e => e.UserId == u.Id).Sum(e => (int?)e.Amount) ?? 0,
                projects = db.Projects.Count(p => p.UserId == u.Id),
                publishedSites = db.Projects.Count(p => p.UserId == u.Id && p.PublishedAt != null),
                stoppedSites = db.Projects.Count(p => p.UserId == u.Id && p.PublishedAt != null && (p.HostingPaidUntil == null || p.HostingPaidUntil <= stoppedBefore)),
                suspendedSites = db.Projects.Count(p => p.UserId == u.Id && p.IsAdminSuspended),
                paidMinor = db.Payments.Where(p => p.UserId == u.Id && p.Status == PaymentStatuses.Completed && !p.IsTest).Sum(p => (int?)p.AmountMinor) ?? 0,
                payments = db.Payments.Count(p => p.UserId == u.Id && p.Status == PaymentStatuses.Completed && !p.IsTest),
                lastPaymentAt = db.Payments.Where(p => p.UserId == u.Id && p.Status == PaymentStatuses.Completed && !p.IsTest).Max(p => (DateTime?)p.CompletedAt),
                lastActivityAt = db.Projects.Where(p => p.UserId == u.Id).Max(p => (DateTime?)p.UpdatedAt),
                aiCost = db.AiUsages.Where(a => a.UserId == u.Id).Sum(a => (double?)(a.ActualCostUsd ?? a.EstimatedCostUsd)) ?? 0
            });

            rows = sort switch
            {
                "paid" => rows.OrderByDescending(r => r.paidMinor).ThenByDescending(r => r.CreatedAt),
                "cost" => rows.OrderByDescending(r => r.aiCost).ThenByDescending(r => r.CreatedAt),
                "sites" => rows.OrderByDescending(r => r.projects).ThenByDescending(r => r.CreatedAt),
                "activity" => rows.OrderBy(r => r.lastActivityAt == null).ThenByDescending(r => r.lastActivityAt).ThenByDescending(r => r.CreatedAt),
                _ => rows.OrderByDescending(r => r.CreatedAt)
            };

            var current = Math.Max(1, page ?? 1);
            var items = await rows.Skip((current - 1) * PageSize).Take(PageSize).ToListAsync();
            return Results.Ok(new
            {
                total = counts.GetValueOrDefault(UserFilters.Values.Contains(filter) ? filter! : UserFilters.All),
                page = current,
                pageSize = PageSize,
                counts,
                items = items.Select(r => new
                {
                    r.Id, r.Email, r.Name, r.Role, r.CreatedAt, r.google, r.apple, r.plan, r.periodEnd, r.credits,
                    r.projects, r.publishedSites, r.stoppedSites, r.suspendedSites, paid = r.paidMinor / 100m, r.payments, r.lastPaymentAt,
                    r.lastActivityAt, r.aiCost
                })
            });
        });

        g.MapGet("/users/{id:guid}", async (Guid id, AppDbContext db, HostingService hosting, TokenService tokens,
            IOptions<AppOptions> appOpt, TemplateCatalog templates) =>
        {
            var user = await db.Users.Include(u => u.Subscription).AsNoTracking().FirstOrDefaultAsync(u => u.Id == id)
                       ?? throw ApiException.NotFound("المستخدم غير موجود");
            var now = DateTime.UtcNow;
            var opt = appOpt.Value;

            var projects = await db.Projects.Where(p => p.UserId == id).OrderByDescending(p => p.UpdatedAt).ToListAsync();
            var projectIds = projects.Select(p => p.Id).ToList();
            var versionCounts = await db.ProjectVersions.Where(v => projectIds.Contains(v.ProjectId))
                .GroupBy(v => v.ProjectId).Select(x => new { x.Key, count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.count);
            var taskCounts = await db.AgentTasks.Where(t => projectIds.Contains(t.ProjectId))
                .GroupBy(t => t.ProjectId).Select(x => new { x.Key, count = x.Count() }).ToDictionaryAsync(x => x.Key, x => x.count);
            var costs = await db.AiUsages.Where(a => a.ProjectId != null && projectIds.Contains(a.ProjectId.Value))
                .GroupBy(a => a.ProjectId!.Value)
                .Select(x => new { x.Key, cost = x.Sum(a => (double)(a.ActualCostUsd ?? a.EstimatedCostUsd)) })
                .ToDictionaryAsync(x => x.Key, x => x.cost);
            var suspenderIds = projects.Where(p => p.AdminSuspendedBy != null).Select(p => p.AdminSuspendedBy!.Value).Distinct().ToList();
            var suspenders = await db.Users.Where(u => suspenderIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email);

            var sites = new List<object>();
            foreach (var p in projects)
            {
                var files = p.CurrentVersionId is { } vid
                    ? await db.ProjectVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.FilesJson).FirstOrDefaultAsync()
                    : null;
                var fileMap = files is null ? new SortedDictionary<string, string>() : SiteFiles.Parse(files);
                sites.Add(new
                {
                    p.Id, p.Name, p.Description, p.TemplateKey, p.Slug, p.CreatedAt, p.UpdatedAt, p.PublishedAt,
                    template = templates.Get(p.TemplateKey)?.Name,
                    p.CustomDomain, p.CustomDomainVerified,
                    siteUrl = p.PublishedAt is null ? null : ProjectEndpoints.SiteUrl(opt, p.Slug, p.CustomDomain, p.CustomDomainVerified),
                    previewUrl = p.CurrentVersionId is null ? null
                        : $"{opt.PublicUrl.TrimEnd('/')}/preview/{p.Id:N}/{tokens.CreatePreviewToken(p.Id, TimeSpan.FromHours(2))}/",
                    pages = fileMap.Keys.Count(SiteFiles.IsHtml),
                    features = HostingService.Features(fileMap),
                    hosting = await hosting.StatusAsync(p),
                    suspension = p.IsAdminSuspended
                        ? new
                        {
                            reason = p.AdminSuspensionReason, at = p.AdminSuspendedAt,
                            by = p.AdminSuspendedBy is { } by ? suspenders.GetValueOrDefault(by) : null
                        }
                        : null,
                    versions = versionCounts.GetValueOrDefault(p.Id),
                    tasks = taskCounts.GetValueOrDefault(p.Id),
                    aiCost = costs.GetValueOrDefault(p.Id)
                });
            }

            var names = projects.ToDictionary(p => p.Id, p => p.Name);
            var payments = (await db.Payments.Where(p => p.UserId == id).OrderByDescending(p => p.CreatedAt).Take(100).ToListAsync())
                .Select(p => new
                {
                    p.Id, p.Kind, p.Plan, p.Interval, p.TopupPackId, p.TopupCredits, p.ProjectId, p.HostingTier,
                    projectName = p.ProjectId is { } pid ? names.GetValueOrDefault(pid) : null,
                    amount = p.AmountMinor / 100m, p.Currency, p.Provider, p.Status, p.IsTest, p.CreatedAt, p.CompletedAt
                }).ToList();
            var real = payments.Where(p => p.Status == PaymentStatuses.Completed && !p.IsTest).ToList();

            return Results.Ok(new
            {
                user = new
                {
                    user.Id, user.Email, user.Name, user.Role, user.CreatedAt, user.FreeSiteUsed,
                    password = user.PasswordHash != "", google = user.GoogleId != null, apple = user.AppleId != null
                },
                plan = new
                {
                    plan = user.Subscription?.IsProActive(now) == true ? PlanKeys.Pro : PlanKeys.Free,
                    interval = user.Subscription?.Interval,
                    periodEnd = user.Subscription?.CurrentPeriodEnd
                },
                credits = await db.CreditEntries.Where(e => e.UserId == id).SumAsync(e => (int?)e.Amount) ?? 0,
                totals = new
                {
                    paid = real.Sum(p => p.amount),
                    payments = real.Count,
                    aiCost = await db.AiUsages.Where(a => a.UserId == id).SumAsync(a => (double?)(a.ActualCostUsd ?? a.EstimatedCostUsd)) ?? 0,
                    creditsUsed = -(await db.CreditEntries.Where(e => e.UserId == id && e.Type == CreditEntryTypes.Usage).SumAsync(e => (int?)e.Amount) ?? 0),
                    tasks = await db.AgentTasks.CountAsync(t => t.UserId == id)
                },
                sites,
                payments
            });
        });
    }

    /// <summary>A payment received outside Ziina (bank transfer, cash) so revenue and "who paid" stay accurate.</summary>
    public static async Task<Payment?> RecordManualPaymentAsync(AppDbContext db, Guid userId, decimal? amount, string currency, Action<Payment> describe)
    {
        if (amount is not > 0) return null;
        if (amount > 100_000) throw ApiException.BadRequest("المبلغ غير منطقي");
        var now = DateTime.UtcNow;
        var payment = new Payment
        {
            UserId = userId, Currency = currency, Provider = "manual", Status = PaymentStatuses.Completed,
            AmountMinor = (int)Math.Round(amount.Value * 100m, MidpointRounding.AwayFromZero), CreatedAt = now, CompletedAt = now
        };
        describe(payment);
        db.Payments.Add(payment);
        await db.SaveChangesAsync();
        return payment;
    }
}
