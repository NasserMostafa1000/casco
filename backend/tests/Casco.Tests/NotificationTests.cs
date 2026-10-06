using Casco.Api.Domain;
using Casco.Api.Features.Monitoring;
using Casco.Api.Features.Notifications;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Casco.Api.Infrastructure.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class NotificationTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly TestDb _t = new();
    private readonly EmailQueue _queue = new(Options.Create(new SmtpOptions { PickupDirectory = "mail" }));
    private readonly IOptions<AppOptions> _app = Options.Create(new AppOptions
    {
        PublicUrl = "https://casco.studio", SitesDomain = "casco.studio", SitesScheme = "https", SitesPort = ""
    });
    private readonly IOptions<BillingOptions> _billing = Options.Create(new BillingOptions());

    public void Dispose() => _t.Dispose();

    private UserEmails Emails() => new(_t.Db, _queue, _app, _billing, NullLogger<UserEmails>.Instance);

    private List<EmailMessage> Drain()
    {
        var list = new List<EmailMessage>();
        while (_queue.Reader.TryRead(out var m)) list.Add(m);
        return list;
    }

    [Theory]
    [InlineData(10, null)]
    [InlineData(6.5, "7d")]
    [InlineData(0.5, "1d")]
    [InlineData(-1, "expired")]
    [InlineData(-4, null)]
    public void Plan_reminder_stage(double daysLeft, string? stage) =>
        Assert.Equal(stage, ReminderStages.ForPlan(Now.AddDays(daysLeft), Now));

    [Theory]
    [InlineData(8, null)]
    [InlineData(3, "7d")]
    [InlineData(0.2, "1d")]
    [InlineData(-1, "grace")]
    [InlineData(-4, "stopped")]
    [InlineData(-7, null)]
    public void Hosting_reminder_stage_with_three_grace_days(double daysLeft, string? stage) =>
        Assert.Equal(stage, ReminderStages.ForHosting(Now.AddDays(daysLeft), Now, 3));

    private static MetricReading Cpu(double v) =>
        new(ServerIds.App, "cpu", v, $"{v}%", AlertEngine.LevelFor(v, 85, 95), 3);

    [Fact]
    public void Cpu_alert_needs_three_high_minutes_and_a_short_spike_sends_nothing()
    {
        var engine = new AlertEngine();
        var repeat = TimeSpan.FromHours(6);
        Assert.Empty(engine.Evaluate([Cpu(99)], Now, repeat));
        Assert.Empty(engine.Evaluate([Cpu(20)], Now.AddMinutes(1), repeat));
        Assert.Empty(engine.Evaluate([Cpu(97)], Now.AddMinutes(2), repeat));
        Assert.Empty(engine.Evaluate([Cpu(98)], Now.AddMinutes(3), repeat));

        var raised = engine.Evaluate([Cpu(96)], Now.AddMinutes(4), repeat);
        var change = Assert.Single(raised);
        Assert.Equal(AlertLevel.Critical, change.To);
        Assert.False(change.Repeat);

        Assert.Empty(engine.Evaluate([Cpu(90)], Now.AddMinutes(5), repeat));
        Assert.Empty(engine.Evaluate([Cpu(99)], Now.AddHours(1), repeat));
        Assert.True(Assert.Single(engine.Evaluate([Cpu(99)], Now.AddHours(7), repeat)).Repeat);
    }

    [Fact]
    public void Disk_alerts_immediately_escalates_and_recovers_once()
    {
        var engine = new AlertEngine();
        var repeat = TimeSpan.FromHours(6);
        MetricReading Disk(double v) => new(ServerIds.Database, "disk", v, $"{v}%", AlertEngine.LevelFor(v, 85, 95));

        Assert.Equal(AlertLevel.Warning, Assert.Single(engine.Evaluate([Disk(88)], Now, repeat)).To);
        Assert.Equal(AlertLevel.Critical, Assert.Single(engine.Evaluate([Disk(96)], Now.AddMinutes(1), repeat)).To);
        Assert.Empty(engine.Evaluate([Disk(90)], Now.AddMinutes(2), repeat));
        Assert.Single(engine.Active);
        var recovered = Assert.Single(engine.Evaluate([Disk(60)], Now.AddMinutes(3), repeat));
        Assert.True(recovered.Recovered);
        Assert.Empty(engine.Active);
        Assert.Empty(engine.Evaluate([Disk(60)], Now.AddMinutes(4), repeat));
    }

    [Fact]
    public void Alert_email_names_server_metric_and_advice()
    {
        var disk = new MetricReading(ServerIds.App, "disk", 96, "96% · 77 / 80 GB", AlertLevel.Critical);
        var content = AlertEmail.Build("ar", [new AlertChange(disk, AlertLevel.Ok, AlertLevel.Critical, false)], [disk], new MonitorOptions(), Now);
        Assert.StartsWith("🔴", content.Subject);
        Assert.Contains("السيرفر 1", content.Subject);
        Assert.Contains("docker system prune", content.HtmlBlock);
        Assert.Equal(EmailTone.Danger, content.Tone);
    }

    [Fact]
    public void Layout_is_rtl_for_arabic_embeds_the_logo_and_escapes_user_text()
    {
        var content = EmailCopy.ProExpired("ar", "<script>x</script>", Now, "https://casco.studio/app/billing");
        var (html, text) = EmailLayout.Render(content, "https://casco.studio");
        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains("cid:" + EmailAssets.LogoContentId, html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("https://casco.studio/app/billing", text);

        var (en, _) = EmailLayout.Render(EmailCopy.ProExpired("en", "Sara", Now, "https://x"), "https://casco.studio");
        Assert.Contains("dir=\"ltr\"", en);
        Assert.Contains("Your Casco Pro plan has ended", en);
    }

    [Fact]
    public void Bilingual_email_has_an_rtl_arabic_card_and_an_ltr_english_card()
    {
        var parts = EmailCopy.ArabicAndEnglish("ar", l => EmailCopy.ProExpired(l, "Sara", Now, "https://x"));
        var (html, text) = EmailLayout.Render(parts, "https://casco.studio");
        Assert.Equal("انتهى اشتراكك في Casco Pro | Your Casco Pro plan has ended", EmailLayout.Subject(parts));
        Assert.Contains("<html lang=\"ar\" dir=\"rtl\"", html);
        var arabic = html.IndexOf("class=\"pad\" dir=\"rtl\"", StringComparison.Ordinal);
        var english = html.IndexOf("class=\"pad\" dir=\"ltr\"", StringComparison.Ordinal);
        Assert.True(arabic > 0 && english > arabic);
        Assert.Contains("You&#39;re receiving this because you have a Casco account", html);
        Assert.Contains("وصلتك هذه الرسالة لأن لديك حساباً على Casco", html);
        Assert.True(text.IndexOf("انتهى اشتراكك في Pro", StringComparison.Ordinal) < text.IndexOf("Your Pro plan has ended", StringComparison.Ordinal));
    }

    [Fact]
    public void Mail_message_has_text_html_and_inline_logo()
    {
        var (html, text) = EmailLayout.Render(EmailCopy.ProExpired("en", "Sara", Now, "https://x"), "https://casco.studio");
        using var mail = EmailSenderService.Build(new EmailMessage("a@b.c", "Hi", html, text, "t"), "Casco <no-reply@casco.studio>");
        Assert.Equal("no-reply@casco.studio", mail.From!.Address);
        Assert.Equal(2, mail.AlternateViews.Count);
        Assert.Single(mail.AlternateViews[1].LinkedResources);
        Assert.True(EmailAssets.Logo.Length > 1000);
    }

    [Fact]
    public async Task Reminders_go_out_once_in_arabic_and_english_with_the_users_language_first()
    {
        var pro = new User { Email = "pro@b.c", Name = "Sara", Locale = "en" };
        var owner = new User { Email = "owner@b.c", Name = "أحمد", Locale = "ar" };
        _t.Db.Users.AddRange(pro, owner);
        _t.Db.Subscriptions.Add(new Subscription
        {
            UserId = pro.Id, Plan = PlanKeys.Pro, Interval = BillingIntervals.Monthly,
            CurrentPeriodStart = Now.AddDays(-28), CurrentPeriodEnd = Now.AddDays(2), NextCreditGrantAt = null
        });
        _t.Db.CreditEntries.Add(new CreditEntry { UserId = pro.Id, Amount = 5000, Bucket = CreditBuckets.Plan, Type = CreditEntryTypes.PlanGrant });
        _t.Db.Projects.Add(new Project
        {
            UserId = owner.Id, Name = "مطعم الريف", Slug = "rif", SiteKey = "k1", PublishedAt = Now.AddDays(-40),
            HostingTier = HostingTiers.Static, HostingPaidUntil = Now.AddDays(-1)
        });
        await _t.Db.SaveChangesAsync();

        Assert.Equal(2, await Emails().SendDueRemindersAsync(Now));
        var sent = Drain();
        var proMail = Assert.Single(sent, m => m.To == "pro@b.c");
        Assert.Equal("Your Casco Pro plan ends in 2 days | اشتراكك في Casco Pro ينتهي خلال 2 أيام", proMail.Subject);
        Assert.Contains("$18 / month", proMail.Html);
        Assert.Contains("$18 شهرياً", proMail.Html);
        Assert.True(proMail.Html.IndexOf("Your Pro plan is ending soon", StringComparison.Ordinal) < proMail.Html.IndexOf("اشتراكك ينتهي قريباً", StringComparison.Ordinal));
        var siteMail = Assert.Single(sent, m => m.To == "owner@b.c");
        Assert.StartsWith("انتهت استضافة مطعم الريف", siteMail.Subject);
        Assert.Contains("| Hosting for مطعم الريف expired", siteMail.Subject);
        Assert.Contains("renew=", siteMail.Html);
        Assert.Contains("rif.casco.studio", siteMail.Html);

        Assert.Equal(0, await Emails().SendDueRemindersAsync(Now.AddHours(1)));
        Assert.Empty(Drain());

        // A day later the Pro plan moves to the 1-day stage: one new e-mail.
        Assert.Equal(1, await Emails().SendDueRemindersAsync(Now.AddDays(1.2)));
        Assert.Equal("Your Casco Pro plan ends tomorrow | اشتراكك في Casco Pro ينتهي غداً", Assert.Single(Drain()).Subject);
    }

    [Fact]
    public async Task Low_credits_email_once_per_credit_period()
    {
        var user = new User { Email = "low@b.c", Name = "Sara", Locale = "hi" };
        _t.Db.Users.Add(user);
        _t.Db.Subscriptions.Add(new Subscription
        {
            UserId = user.Id, Plan = PlanKeys.Pro, Interval = BillingIntervals.Yearly,
            CurrentPeriodStart = Now.AddMonths(-2), CurrentPeriodEnd = Now.AddMonths(10), NextCreditGrantAt = Now.AddDays(12)
        });
        _t.Db.CreditEntries.AddRange(
            new CreditEntry { UserId = user.Id, Amount = 5000, Bucket = CreditBuckets.Plan, Type = CreditEntryTypes.PlanGrant },
            new CreditEntry { UserId = user.Id, Amount = -4700, Bucket = CreditBuckets.Plan, Type = "usage" });
        await _t.Db.SaveChangesAsync();

        Assert.Equal(1, await Emails().SendDueRemindersAsync(Now));
        var mail = Assert.Single(Drain());
        Assert.Equal("You're running low on credits | نقاطك قاربت على النفاد", mail.Subject);
        Assert.Contains("you have only 300 credits left", mail.Text);
        Assert.Contains("بقي لديك 300 نقطة فقط", mail.Text);
        Assert.Equal(0, await Emails().SendDueRemindersAsync(Now.AddHours(2)));
    }

    [Fact]
    public async Task Nothing_is_claimed_while_email_is_off()
    {
        var off = new EmailQueue(Options.Create(new SmtpOptions { Host = "", PickupDirectory = "" }));
        var user = new User { Email = "u@b.c", Name = "U" };
        _t.Db.Users.Add(user);
        _t.Db.Subscriptions.Add(new Subscription { UserId = user.Id, Plan = PlanKeys.Pro, CurrentPeriodEnd = Now.AddDays(2) });
        _t.Db.CreditEntries.Add(new CreditEntry { UserId = user.Id, Amount = 5000, Bucket = CreditBuckets.Plan, Type = CreditEntryTypes.PlanGrant });
        await _t.Db.SaveChangesAsync();

        var emails = new UserEmails(_t.Db, off, _app, _billing, NullLogger<UserEmails>.Instance);
        Assert.Equal(0, await emails.SendDueRemindersAsync(Now));
        Assert.Empty(_t.Db.SentNotifications);
        Assert.Equal(1, await Emails().SendDueRemindersAsync(Now));
    }

    [Fact]
    public void File_logger_writes_daily_file_and_counts_errors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "casco-logs-" + Guid.NewGuid().ToString("N"));
        var feed = new ErrorFeed();
        using (var provider = new FileLoggerProvider(dir, new FileLogOptions(), feed))
        {
            var logger = provider.CreateLogger("Test");
            logger.LogInformation("hello {Name}", "casco");
            logger.LogError(new InvalidOperationException("boom"), "failed {Id}", 7);
            logger.LogDebug("not written");
        }
        var file = Assert.Single(Directory.GetFiles(dir, "casco-*.log"));
        var content = File.ReadAllText(file);
        Assert.Contains("[INF] Test: hello casco", content);
        Assert.Contains("[ERR] Test: failed 7", content);
        Assert.Contains("InvalidOperationException: boom", content);
        Assert.DoesNotContain("not written", content);
        Assert.Equal(1, feed.CountSince(DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal("failed 7", Assert.Single(feed.Recent(10)).Message);
        Directory.Delete(dir, true);
    }
}
