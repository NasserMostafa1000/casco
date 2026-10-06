using System.Security.Cryptography;
using System.Text;
using Casco.Api.Features.Notifications;
using Casco.Api.Infrastructure;
using Casco.Api.Infrastructure.Email;
using Casco.Api.Infrastructure.Logging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Monitoring;

public record EmailTestRequest(string? Kind, string? Lang);

public static class MonitoringEndpoints
{
    public static readonly string[] EmailKinds =
        ["pro_expiring", "pro_expired", "hosting_expiring", "hosting_grace", "hosting_stopped", "receipt", "credits_low", "site_order", "server_alert", "server_recovered"];

    /// <summary>Server 2's agent posts its readings here with the shared token.</summary>
    public static void MapMonitorIngest(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/internal/metrics", (HttpContext http, AgentReport report, ServerMonitor monitor) =>
        {
            if (!monitor.AcceptsAgent) return Results.NotFound();
            var header = http.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(monitor.Options.AgentToken)))
                return Results.Unauthorized();
            static double? Pct(double? v) => v is { } x ? Math.Clamp(x, 0, 100) : null;
            monitor.Receive(report with { Cpu = Pct(report.Cpu), Memory = Pct(report.Memory), Disk = Pct(report.Disk) });
            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("monitor");

    public static void MapMonitoring(this RouteGroupBuilder admin)
    {
        admin.MapGet("/monitor", (ServerMonitor monitor, ErrorFeed errors) => Results.Ok(monitor.View(errors.Recent(50))));

        admin.MapPost("/monitor/check", async (ServerMonitor monitor, ErrorFeed errors, AppDbContext db, CancellationToken ct) =>
        {
            var changes = await monitor.CheckAsync(db, ct);
            return Results.Ok(new { changes = changes.Count, view = monitor.View(errors.Recent(50)) });
        });

        admin.MapGet("/email/preview", (string? kind, string? lang, IOptions<AppOptions> app, IOptions<BillingOptions> billing, IOptions<MonitorOptions> monitor) =>
        {
            var parts = SampleParts(kind ?? "pro_expiring", lang ?? "ar", app.Value, billing.Value, monitor.Value);
            var (html, _) = EmailLayout.Render(parts, app.Value.FrontendBase, EmailAssets.LogoDataUri);
            return Results.Text(html, "text/html; charset=utf-8");
        });

        admin.MapPost("/email/test", async (EmailTestRequest req, HttpContext http, AppDbContext db, EmailQueue queue,
            IOptions<AppOptions> app, IOptions<BillingOptions> billing, IOptions<MonitorOptions> monitor) =>
        {
            if (!queue.Enabled)
                throw ApiException.BadRequest("الإيميل غير مفعّل: اضبط Smtp__Host و Smtp__User و Smtp__Password في .env", "email_disabled");
            var recipients = monitor.Value.AlertEmails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
            if (recipients.Count == 0)
            {
                var me = await db.Users.Where(u => u.Id == http.User.UserId()).Select(u => u.Email).FirstAsync();
                recipients.Add(me);
            }
            var kinds = string.IsNullOrWhiteSpace(req.Kind) || req.Kind == "all" ? EmailKinds : EmailKinds.Where(k => k == req.Kind).ToArray();
            if (kinds.Length == 0) throw ApiException.BadRequest("نوع غير معروف");
            var lang = EmailText.Normalize(req.Lang ?? "ar");
            foreach (var kind in kinds)
            {
                var parts = SampleParts(kind, lang, app.Value, billing.Value, monitor.Value);
                var (html, text) = EmailLayout.Render(parts, app.Value.FrontendBase);
                foreach (var to in recipients)
                    queue.Enqueue(new EmailMessage(to, "[TEST] " + EmailLayout.Subject(parts), html, text, "test_" + kind));
            }
            return Results.Ok(new { sent = kinds.Length * recipients.Count, to = recipients, mode = queue.Mode });
        });

        admin.MapPost("/email/reminders", async (UserEmails emails, TimeProvider clock, CancellationToken ct) =>
            Results.Ok(new { sent = await emails.SendDueRemindersAsync(clock.GetUtcNow().UtcDateTime, ct) }));
    }

    public static readonly string[] ReminderKinds = ["pro_expiring", "pro_expired", "hosting_expiring", "hosting_grace", "hosting_stopped", "credits_low"];

    /// <summary>The sample as it is really sent: reminders in Arabic and English, the rest in one language.</summary>
    public static IReadOnlyList<EmailContent> SampleParts(string kind, string lang, AppOptions app, BillingOptions billing, MonitorOptions monitor) =>
        ReminderKinds.Contains(kind)
            ? EmailCopy.ArabicAndEnglish(lang, l => Sample(kind, l, app, billing, monitor))
            : [Sample(kind, lang, app, billing, monitor)];

    /// <summary>Realistic example of each e-mail, for previews and test sends.</summary>
    public static EmailContent Sample(string kind, string lang, AppOptions app, BillingOptions billing, MonitorOptions monitor)
    {
        lang = kind.StartsWith("server_", StringComparison.Ordinal) ? (lang == "en" ? "en" : "ar") : EmailText.Normalize(lang);
        var now = DateTime.UtcNow;
        var billingUrl = $"{app.FrontendBase}/app/billing";
        var site = EmailText.L(lang, "مطعم الريف", "Bloom Café", "ब्लूम कैफ़े");
        var url = app.SiteUrl("bloom-cafe");
        var name = EmailText.L(lang, "سارة", "Sara", "सारा");
        var renew = $"{billingUrl}?renew=00000000-0000-0000-0000-000000000000";
        var monthly = BillingOptions.Dollars(billing.Pro.MonthlyPriceMinor);
        return kind switch
        {
            "pro_expired" => EmailCopy.ProExpired(lang, name, now.AddDays(-1), billingUrl),
            "hosting_expiring" => EmailCopy.HostingExpiring(lang, name, site, url, now.AddDays(6.5), 156,
                EmailText.L(lang, $"{BillingOptions.Dollars(billing.Hosting.StaticMonthlyMinor)} شهرياً", $"{BillingOptions.Dollars(billing.Hosting.StaticMonthlyMinor)} / month", $"{BillingOptions.Dollars(billing.Hosting.StaticMonthlyMinor)} / माह"), renew),
            "hosting_grace" => EmailCopy.HostingGrace(lang, name, site, url, now.AddDays(billing.Hosting.GraceDays), renew),
            "hosting_stopped" => EmailCopy.HostingStopped(lang, name, site, url, renew),
            "receipt" => EmailCopy.Receipt(lang, name, EmailCopy.ProItem(lang, "monthly"), monthly, now, now.AddMonths(1), "7F3A9C21B0", false, billingUrl),
            "credits_low" => EmailCopy.CreditsLow(lang, name, 420, now.AddDays(9), billingUrl),
            "site_order" => EmailCopy.SiteEvent(lang, site, SiteEventKind.Order, 1024,
                "طلب جديد رقم #1024\n- 2 × برجر كلاسيك (90 AED)\nالإجمالي: 105 AED\nالاسم: أحمد علي\nالهاتف: +971501234567\nطريقة الدفع: الدفع عند الاستلام",
                $"{app.FrontendBase}/app"),
            "server_alert" or "server_recovered" => SampleAlert(lang, kind == "server_recovered", monitor, now),
            _ => EmailCopy.ProExpiring(lang, name, now.AddDays(6.5), 156,
                EmailText.L(lang, $"{monthly} شهرياً", $"{monthly} / month", $"{monthly} / माह"), billing.Pro.MonthlyCredits, billingUrl)
        };
    }

    private static EmailContent SampleAlert(string lang, bool recovered, MonitorOptions o, DateTime now)
    {
        var disk = new MetricReading(ServerIds.App, "disk", recovered ? 61 : 96, recovered ? "61% · 48.8 / 80 GB" : "96% · 76.8 / 80 GB",
            recovered ? AlertLevel.Ok : AlertLevel.Critical);
        var memory = new MetricReading(ServerIds.Database, "memory", recovered ? 55 : 91, recovered ? "55% · 4.4 / 8 GB" : "91% · 7.3 / 8 GB",
            recovered ? AlertLevel.Ok : AlertLevel.Warning, 3);
        var readings = new List<MetricReading>
        {
            new(ServerIds.App, "cpu", 42, "42% · load 1.7 / 4", AlertLevel.Ok, 3),
            new(ServerIds.App, "memory", 63, "63% · 5 / 8 GB", AlertLevel.Ok, 3),
            disk,
            new(ServerIds.App, "errors", 2, "2 / 10 min", AlertLevel.Ok),
            new(ServerIds.Database, "heartbeat", 1, "1 min", AlertLevel.Ok),
            new(ServerIds.Database, "cpu", 18, "18% · load 0.6 / 4", AlertLevel.Ok, 3),
            memory,
            new(ServerIds.Database, "disk", 34, "34% · 27 / 80 GB", AlertLevel.Ok),
            new(ServerIds.Database, "database", 1, "✓", AlertLevel.Ok, 2),
            new(ServerIds.Database, "backup", 9, "9 h", AlertLevel.Ok)
        };
        var changes = recovered
            ? new List<AlertChange> { new(disk, AlertLevel.Critical, AlertLevel.Ok, false), new(memory, AlertLevel.Warning, AlertLevel.Ok, false) }
            : [new(disk, AlertLevel.Ok, AlertLevel.Critical, false), new(memory, AlertLevel.Ok, AlertLevel.Warning, false)];
        return AlertEmail.Build(lang, changes, readings, o, now);
    }
}
