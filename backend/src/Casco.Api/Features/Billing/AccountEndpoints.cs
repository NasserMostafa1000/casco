using Casco.Api.Domain;
using Casco.Api.Features.Sites;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Billing;

public static class AccountEndpoints
{
    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", async (HttpContext http, AppDbContext db, SubscriptionService subs, CreditService credits, TemplateCatalog templates) =>
        {
            var userId = http.User.UserId();
            var user = await db.Users.FindAsync(userId) ?? throw new ApiException(401, "الحساب غير موجود", "unauthorized");
            var lang = http.Request.Headers["X-Casco-Lang"].ToString();
            if (lang is "ar" or "en" or "hi" && user.Locale != lang)
            {
                user.Locale = lang;
                await db.SaveChangesAsync();
            }
            var plan = await subs.GetPlanAsync(userId);
            var balance = await credits.GetBalanceAsync(userId);
            var projectsCount = await db.Projects.CountAsync(p => p.UserId == userId);

            return Results.Ok(new
            {
                user = new { user.Id, user.Name, user.Email, user.Role, user.FreeSiteUsed, user.Locale },
                plan = new
                {
                    plan.Key,
                    plan.Interval,
                    plan.PeriodEnd,
                    plan.IsPro,
                    daysLeft = plan.PeriodEnd is { } end ? Math.Max(0, (int)Math.Ceiling((end - DateTime.UtcNow).TotalDays)) : (int?)null
                },
                credits = new { plan = balance.Plan, topup = balance.Topup, reserved = balance.Reserved, available = balance.Available },
                limits = new
                {
                    plan.MaxProjects,
                    projectsCount,
                    plan.CanUsePremium,
                    plan.CanCustomDomain,
                    plan.CanMultiPage,
                    templates = templates.All.Select(t => new
                    {
                        t.Key, t.Name, t.Description, t.Plan,
                        allowed = SubscriptionService.CanUseTemplate(plan, t)
                    })
                }
            });
        }).RequireAuthorization();

        app.MapGet("/api/me/usage", async (HttpContext http, AppDbContext db) =>
        {
            var userId = http.User.UserId();
            var start = DateTime.UtcNow.Date.AddDays(-13);
            var calls = await db.AiUsages.AsNoTracking()
                .Where(u => u.UserId == userId && u.Success && u.CreatedAt >= start)
                .Select(u => new { u.CreatedAt, u.InputTokens, u.OutputTokens })
                .ToListAsync();
            var spent = await db.CreditEntries.AsNoTracking()
                .Where(e => e.UserId == userId && e.Type == CreditEntryTypes.Usage && e.Amount < 0 && e.CreatedAt >= start)
                .Select(e => new { e.CreatedAt, e.Amount })
                .ToListAsync();
            var days = Enumerable.Range(0, 14).Select(offset =>
            {
                var day = start.AddDays(offset);
                var next = day.AddDays(1);
                var dayCalls = calls.Where(u => u.CreatedAt >= day && u.CreatedAt < next).ToList();
                return new
                {
                    date = day.ToString("yyyy-MM-dd"),
                    requests = dayCalls.Count,
                    tokens = dayCalls.Sum(u => u.InputTokens + u.OutputTokens),
                    credits = spent.Where(e => e.CreatedAt >= day && e.CreatedAt < next).Sum(e => -e.Amount)
                };
            }).ToList();
            return Results.Ok(new { days });
        }).RequireAuthorization();

        app.MapGet("/api/me/share", async (HttpContext http, AppDbContext db, IOptions<AppOptions> appOpt) =>
        {
            var user = await db.Users.FindAsync(http.User.UserId()) ?? throw new ApiException(401, "الحساب غير موجود", "unauthorized");
            var code = await ShareReward.EnsureCodeAsync(db, user);
            var joined = await ShareReward.JoinedAsync(db, user.Id);
            return Results.Ok(new
            {
                code,
                link = $"{appOpt.Value.FrontendBase}/register?ref={code}",
                joined,
                required = ShareReward.Friends,
                bonusPrompts = ShareReward.BonusPrompts,
                claimed = user.ShareRewardClaimed
            });
        }).RequireAuthorization();

        app.MapPost("/api/me/share/claim", async (HttpContext http, AppDbContext db) =>
        {
            var user = await db.Users.FindAsync(http.User.UserId()) ?? throw new ApiException(401, "الحساب غير موجود", "unauthorized");
            if (user.ShareRewardClaimed)
                throw ApiException.Conflict("مكافأة المشاركة اتضافت قبل كده ومرة واحدة بس", "share_already_claimed");

            var joined = await ShareReward.JoinedAsync(db, user.Id);
            if (joined < ShareReward.Friends)
                throw ApiException.BadRequest($"لسه {joined} من {ShareReward.Friends} أصحاب عملوا حساب من الرابط", "share_incomplete");

            user.ShareRewardClaimed = true;
            await db.SaveChangesAsync();
            return Results.Ok(new { claimed = true, bonusPrompts = ShareReward.BonusPrompts });
        }).RequireAuthorization();
    }
}

public class SubscriptionMaintenanceService(IServiceScopeFactory scopes, LeaderElection leader, ILogger<SubscriptionMaintenanceService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await leader.WaitUntilLeaderAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var subs = scope.ServiceProvider.GetRequiredService<SubscriptionService>();
                var now = DateTime.UtcNow;

                var due = await db.Subscriptions
                    .Where(s => s.Plan == Domain.PlanKeys.Pro && (s.CurrentPeriodEnd <= now || s.NextCreditGrantAt <= now))
                    .OrderBy(s => s.CurrentPeriodEnd).Select(s => s.UserId).Take(500).ToListAsync(stoppingToken);
                foreach (var userId in due) await subs.RefreshAsync(userId);

                await db.AiResponseCache.Where(c => c.ExpiresAt < now).ExecuteDeleteAsync(stoppingToken);
                await db.RevokedTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(stoppingToken);
                var stale = now.AddDays(-2);
                await db.Payments.Where(p => p.Status == Domain.PaymentStatuses.Pending && p.CreatedAt < stale)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, Domain.PaymentStatuses.Canceled), stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Subscription maintenance failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
