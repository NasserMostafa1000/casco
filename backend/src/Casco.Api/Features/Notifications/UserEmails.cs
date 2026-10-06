using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Casco.Api.Infrastructure.Email.EmailText;

namespace Casco.Api.Features.Notifications;

public static class ReminderStages
{
    public const string Week = "7d";
    public const string Day = "1d";
    public const string Expired = "expired";
    public const string Grace = "grace";
    public const string Stopped = "stopped";

    /// <summary>Only the current stage is sent, so a missed 7-day reminder is not sent late next to the 1-day one.</summary>
    public static string? ForPlan(DateTime end, DateTime now)
    {
        var left = end - now;
        if (left > TimeSpan.FromDays(7)) return null;
        if (left > TimeSpan.FromDays(1)) return Week;
        if (left > TimeSpan.Zero) return Day;
        return left > TimeSpan.FromDays(-3) ? Expired : null;
    }

    public static string? ForHosting(DateTime paidUntil, DateTime now, int graceDays)
    {
        var left = paidUntil - now;
        if (left > TimeSpan.FromDays(7)) return null;
        if (left > TimeSpan.FromDays(1)) return Week;
        if (left > TimeSpan.Zero) return Day;
        var offline = paidUntil.AddDays(Math.Max(0, graceDays));
        if (now < offline) return Grace;
        return now < offline.AddDays(3) ? Stopped : null;
    }

    /// <summary>React dist hosting: one reminder two days before it ends, then one when the site is taken offline.</summary>
    public static string? ForReact(DateTime paidUntil, DateTime now)
    {
        var left = paidUntil - now;
        if (left > TimeSpan.FromDays(2)) return null;
        if (left > TimeSpan.Zero) return "2d";
        return now < paidUntil.AddDays(3) ? Stopped : null;
    }
}

/// <summary>Customer e-mails: billing reminders, receipts and low credits. Each one is sent at most once (SentNotification key).</summary>
public class UserEmails(AppDbContext db, EmailQueue queue, IOptions<AppOptions> app, IOptions<BillingOptions> billing, ILogger<UserEmails> logger)
{
    /// <summary>Plan credits at or below this share of the monthly grant trigger the low-credits e-mail.</summary>
    public const double LowCreditsShare = 0.1;

    private string BillingUrl => $"{app.Value.FrontendBase}/app/billing";
    private string RenewUrl(Guid projectId) => $"{BillingUrl}?renew={projectId}";

    public void Send(string to, string kind, IReadOnlyList<EmailContent> parts)
    {
        var (html, text) = EmailLayout.Render(parts, app.Value.FrontendBase);
        queue.Enqueue(new EmailMessage(to, EmailLayout.Subject(parts), html, text, kind));
    }

    /// <summary>Claims <paramref name="key"/> and queues the e-mail; false when it was already sent or e-mail is off.</summary>
    public async Task<bool> SendOnceAsync(string key, string kind, Guid? userId, string to, IReadOnlyList<EmailContent> parts, CancellationToken ct = default)
    {
        if (!queue.Enabled || string.IsNullOrWhiteSpace(to)) return false;
        if (await db.SentNotifications.AnyAsync(n => n.Key == key, ct)) return false;
        var row = new SentNotification { Key = key, UserId = userId, Kind = kind };
        db.SentNotifications.Add(row);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
        Send(to, kind, parts);
        return true;
    }

    public async Task PaymentReceiptAsync(Payment payment)
    {
        try
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == payment.UserId);
            if (user is null) return;
            var lang = Normalize(user.Locale);
            string item;
            DateTime? validUntil = null;
            switch (payment.Kind)
            {
                case PaymentKinds.Subscription:
                    item = EmailCopy.ProItem(lang, payment.Interval);
                    validUntil = await db.Subscriptions.Where(s => s.UserId == user.Id).Select(s => s.CurrentPeriodEnd).FirstOrDefaultAsync();
                    break;
                case PaymentKinds.Hosting:
                    var site = await db.Projects.AsNoTracking().Where(p => p.Id == payment.ProjectId)
                        .Select(p => new { p.Name, p.HostingPaidUntil }).FirstOrDefaultAsync();
                    item = EmailCopy.HostingItem(lang, site?.Name ?? "");
                    validUntil = site?.HostingPaidUntil;
                    break;
                default:
                    item = EmailCopy.TopupItem(lang, payment.TopupCredits ?? 0);
                    break;
            }
            var amount = payment.Currency == "USD" ? BillingOptions.Dollars(payment.AmountMinor) : $"{payment.AmountMinor / 100m:0.##} {payment.Currency}";
            var reference = payment.Id.ToString("N")[..10].ToUpperInvariant();
            var content = EmailCopy.Receipt(lang, user.Name, item, amount, payment.CompletedAt ?? DateTime.UtcNow, validUntil, reference, payment.IsTest, BillingUrl);
            await SendOnceAsync($"receipt:{payment.Id}", "receipt", user.Id, user.Email, [content]);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not queue the receipt for payment {PaymentId}", payment.Id);
        }
    }

    /// <summary>Sends every reminder that is due now. Returns how many e-mails were queued.</summary>
    public async Task<int> SendDueRemindersAsync(DateTime now, CancellationToken ct = default)
    {
        if (!queue.Enabled) return 0;
        var b = billing.Value;
        var sent = 0;

        var plans = await db.Subscriptions.AsNoTracking()
            .Where(s => s.CurrentPeriodEnd != null && s.CurrentPeriodEnd > now.AddDays(-3) && s.CurrentPeriodEnd <= now.AddDays(7))
            .Join(db.Users, s => s.UserId, u => u.Id, (s, u) => new { s.UserId, s.Interval, End = s.CurrentPeriodEnd!.Value, u.Email, u.Name, u.Locale })
            .Take(2000).ToListAsync(ct);
        foreach (var p in plans)
        {
            var stage = ReminderStages.ForPlan(p.End, now);
            if (stage is null) continue;
            var content = EmailCopy.ArabicAndEnglish(p.Locale, lang => stage == ReminderStages.Expired
                ? EmailCopy.ProExpired(lang, p.Name, p.End, BillingUrl)
                : EmailCopy.ProExpiring(lang, p.Name, p.End, (int)Math.Ceiling((p.End - now).TotalHours), PlanPrice(lang, p.Interval, b), b.Pro.MonthlyCredits, BillingUrl));
            if (await SendOnceAsync($"pro:{p.UserId}:{p.End:yyyyMMddHHmm}:{stage}", "pro_" + stage, p.UserId, p.Email, content, ct)) sent++;
        }

        var grace = b.Hosting.GraceDays;
        var sites = await db.Projects.AsNoTracking()
            .Where(p => p.HostingTier != null && p.PublishedAt != null && !p.IsAdminSuspended && p.HostingPaidUntil != null
                        && p.HostingPaidUntil > now.AddDays(-(grace + 3)) && p.HostingPaidUntil <= now.AddDays(7))
            .Join(db.Users, p => p.UserId, u => u.Id, (p, u) => new
            {
                p.Id, p.UserId, p.Name, p.Slug, p.CustomDomain, p.CustomDomainVerified, p.HostingTier, Until = p.HostingPaidUntil!.Value,
                u.Email, OwnerName = u.Name, u.Locale
            })
            .Take(5000).ToListAsync(ct);
        foreach (var s in sites)
        {
            if (s.HostingTier == HostingTiers.React)
            {
                var reactStage = ReminderStages.ForReact(s.Until, now);
                if (reactStage is null) continue;
                var reactUrl = s.CustomDomainVerified && !string.IsNullOrEmpty(s.CustomDomain) ? $"https://{s.CustomDomain}" : app.Value.SiteUrl(s.Slug);
                var reactRenew = $"{app.Value.FrontendBase}/app/react/{s.Id}";
                var reactMail = EmailCopy.ArabicAndEnglish(s.Locale, lang => reactStage == ReminderStages.Stopped
                    ? EmailCopy.ReactHostingStopped(lang, s.OwnerName, s.Name, reactUrl, reactRenew)
                    : EmailCopy.ReactHostingSoon(lang, s.OwnerName, s.Name, reactUrl, s.Until, reactRenew));
                if (await SendOnceAsync($"hosting:{s.Id}:{s.Until:yyyyMMddHHmm}:{reactStage}", "hosting_" + reactStage, s.UserId, s.Email, reactMail, ct)) sent++;
                continue;
            }
            var stage = ReminderStages.ForHosting(s.Until, now, grace);
            if (stage is null) continue;
            var url = s.CustomDomainVerified && !string.IsNullOrEmpty(s.CustomDomain) ? $"https://{s.CustomDomain}" : app.Value.SiteUrl(s.Slug);
            var renew = RenewUrl(s.Id);
            var content = EmailCopy.ArabicAndEnglish(s.Locale, lang => stage switch
            {
                ReminderStages.Grace => EmailCopy.HostingGrace(lang, s.OwnerName, s.Name, url, s.Until.AddDays(grace), renew),
                ReminderStages.Stopped => EmailCopy.HostingStopped(lang, s.OwnerName, s.Name, url, renew),
                _ => EmailCopy.HostingExpiring(lang, s.OwnerName, s.Name, url, s.Until, (int)Math.Ceiling((s.Until - now).TotalHours),
                    PerMonth(lang, BillingOptions.Dollars(b.Hosting.MonthlyMinor(s.HostingTier!))), renew)
            });
            if (await SendOnceAsync($"hosting:{s.Id}:{s.Until:yyyyMMddHHmm}:{stage}", "hosting_" + stage, s.UserId, s.Email, content, ct)) sent++;
        }

        sent += await SendLowCreditsAsync(now, b, ct);
        return sent;
    }

    private async Task<int> SendLowCreditsAsync(DateTime now, BillingOptions b, CancellationToken ct)
    {
        var threshold = (int)(b.Pro.MonthlyCredits * LowCreditsShare);
        var pro = await db.Subscriptions.AsNoTracking()
            .Where(s => s.Plan == PlanKeys.Pro && s.CurrentPeriodEnd > now)
            .Select(s => new { s.UserId, s.NextCreditGrantAt, s.CurrentPeriodEnd })
            .ToListAsync(ct);
        var sent = 0;
        foreach (var batch in pro.Chunk(500))
        {
            var ids = batch.Select(p => p.UserId).ToList();
            var balances = await db.CreditEntries.Where(e => ids.Contains(e.UserId))
                .GroupBy(e => e.UserId).Select(g => new { UserId = g.Key, Sum = g.Sum(e => e.Amount) })
                .ToDictionaryAsync(x => x.UserId, x => x.Sum, ct);
            var low = batch.Where(p => balances.GetValueOrDefault(p.UserId) <= threshold).ToList();
            if (low.Count == 0) continue;
            var lowIds = low.Select(p => p.UserId).ToList();
            var users = await db.Users.AsNoTracking().Where(u => lowIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Email, u.Name, u.Locale }).ToDictionaryAsync(u => u.Id, ct);
            foreach (var p in low)
            {
                if (!users.TryGetValue(p.UserId, out var u)) continue;
                var refill = p.NextCreditGrantAt ?? p.CurrentPeriodEnd;
                var available = Math.Max(0, balances.GetValueOrDefault(p.UserId));
                var content = EmailCopy.ArabicAndEnglish(u.Locale, lang => EmailCopy.CreditsLow(lang, u.Name, available, p.NextCreditGrantAt, BillingUrl));
                if (await SendOnceAsync($"credits-low:{p.UserId}:{refill:yyyyMMddHHmm}", "credits_low", p.UserId, u.Email, content, ct)) sent++;
            }
        }
        return sent;
    }

    private static string PerMonth(string lang, string price) => L(lang, $"{price} شهرياً", $"{price} / month", $"{price} / माह");

    private static string PlanPrice(string lang, string? interval, BillingOptions b) => interval == BillingIntervals.Yearly
        ? L(lang, $"{BillingOptions.Dollars(b.Pro.YearlyPriceMinor)} سنوياً", $"{BillingOptions.Dollars(b.Pro.YearlyPriceMinor)} / year", $"{BillingOptions.Dollars(b.Pro.YearlyPriceMinor)} / वर्ष")
        : PerMonth(lang, BillingOptions.Dollars(b.Pro.MonthlyPriceMinor));
}

public class BillingReminderService(IServiceScopeFactory scopes, EmailQueue queue, TimeProvider clock, LeaderElection leader, ILogger<BillingReminderService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await leader.WaitUntilLeaderAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }

        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(30));
        do
        {
            if (!queue.Enabled) continue;
            try
            {
                using var scope = scopes.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<UserEmails>().SendDueRemindersAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                if (sent > 0) logger.LogInformation("Queued {Count} billing reminder e-mail(s)", sent);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Billing reminders failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
