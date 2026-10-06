using System.Globalization;
using System.Net;
using System.Text;
using Casco.Api.Infrastructure.Email;

namespace Casco.Api.Features.Monitoring;

/// <summary>Owner e-mail for server alerts (Arabic or English).</summary>
public static class AlertEmail
{
    private static string L(string lang, string ar, string en) => lang == "ar" ? ar : en;
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
    private static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    public static string ServerName(string server, string lang) => server switch
    {
        ServerIds.App => L(lang, "السيرفر 1 — التطبيق", "Server 1 — App"),
        ServerIds.Database => L(lang, "السيرفر 2 — قاعدة البيانات", "Server 2 — Database"),
        _ => server
    };

    public static string Label(string key, string lang) => key switch
    {
        "cpu" => L(lang, "المعالج (CPU)", "CPU"),
        "memory" => L(lang, "الذاكرة (RAM)", "Memory (RAM)"),
        "disk" => L(lang, "مساحة القرص", "Disk space"),
        "errors" => L(lang, "الأخطاء (آخر 10 دقائق)", "Errors (last 10 min)"),
        "heartbeat" => L(lang, "آخر تواصل من السيرفر", "Last report from server"),
        "database" => L(lang, "الوصول لقاعدة البيانات", "Database reachable"),
        "dbConnections" => L(lang, "اتصالات قاعدة البيانات", "Database connections"),
        "pgReady" => L(lang, "Postgres يستجيب", "Postgres responding"),
        "backup" => L(lang, "عمر آخر نسخة احتياطية", "Age of last backup"),
        _ => key
    };

    public static string Threshold(string key, MonitorOptions o, string lang) => key switch
    {
        "cpu" => $"{N(o.Cpu.Warning)}% / {N(o.Cpu.Critical)}%",
        "memory" => $"{N(o.Memory.Warning)}% / {N(o.Memory.Critical)}%",
        "disk" => $"{N(o.Disk.Warning)}% / {N(o.Disk.Critical)}%",
        "dbConnections" => $"{N(o.DbConnections.Warning)}% / {N(o.DbConnections.Critical)}%",
        "errors" => $"{N(o.Errors.Warning)} / {N(o.Errors.Critical)}",
        "heartbeat" => L(lang, $"{o.HeartbeatMinutes} دقائق", $"{o.HeartbeatMinutes} min"),
        "backup" => L(lang, $"{o.BackupMaxHours} / {o.BackupMaxHours * 2} ساعة", $"{o.BackupMaxHours} / {o.BackupMaxHours * 2} h"),
        _ => "—"
    };

    private static string Tip(string server, string key, string lang) => (server, key) switch
    {
        (_, "cpu") => L(lang, "افتح صحة النظام في لوحة الأدمن وشوف عدد الطلبات الجارية. لو الضغط مستمر قلّل `Agent__Workers` أو كبّر السيرفر.",
            "Open System health in the admin panel to see running generations. If load stays high, lower `Agent__Workers` or upgrade the server."),
        (ServerIds.Database, "memory") => L(lang, "راجع shared_buffers في `deploy/server2/docker-compose.yml`، ولو تكرر كبّر رام السيرفر 2.",
            "Review shared_buffers in `deploy/server2/docker-compose.yml` and add RAM to server 2 if it keeps happening."),
        (_, "memory") => L(lang, "أعد تشغيل التطبيق: `docker compose restart api`. ولو تكرر كبّر رام السيرفر 1.",
            "Restart the app: `docker compose restart api`. If it keeps happening, add RAM to server 1."),
        (ServerIds.Database, "disk") => L(lang, "امسح النسخ الاحتياطية القديمة من `deploy/server2/backups` ونظّف Docker: `docker system prune -af`.",
            "Delete old dumps in `deploy/server2/backups` and clean Docker: `docker system prune -af`."),
        (_, "disk") => L(lang, "نظّف Docker: `docker system prune -af`، وراجع حجم `deploy/server1/data` (الصور والسجلات).",
            "Clean Docker: `docker system prune -af`, and check the size of `deploy/server1/data` (uploads and logs)."),
        (_, "errors") => L(lang, "افتح «آخر الأخطاء» في لوحة الأدمن أو الملف `data/logs/casco-YYYY-MM-DD.log` على السيرفر 1.",
            "Open “Recent errors” in the admin panel or `data/logs/casco-YYYY-MM-DD.log` on server 1."),
        (_, "heartbeat") => L(lang, "السيرفر 2 لا يرسل بياناته: تأكد أنه يعمل (`docker compose ps`) وأن `MONITOR_URL` و `MONITOR_TOKEN` صحيحين.",
            "Server 2 stopped reporting: check it is up (`docker compose ps`) and that `MONITOR_URL` and `MONITOR_TOKEN` are correct."),
        (_, "database") => L(lang, "التطبيق لا يصل لقاعدة البيانات: تأكد أن Postgres يعمل على السيرفر 2 وأن الجدار الناري يسمح بالمنفذ 5432 من السيرفر 1.",
            "The app can't reach the database: check Postgres is running on server 2 and the firewall allows port 5432 from server 1."),
        (_, "dbConnections") => L(lang, "الاتصالات قريبة من max_connections: قلّل `Maximum Pool Size` في رابط الاتصال أو ارفع `max_connections`.",
            "Connections are close to max_connections: lower `Maximum Pool Size` in the connection string or raise `max_connections`."),
        (_, "pgReady") => L(lang, "Postgres لا يستجيب: `docker compose logs db` على السيرفر 2.", "Postgres isn't responding: `docker compose logs db` on server 2."),
        (_, "backup") => L(lang, "النسخ الاحتياطي متأخر: `docker compose logs backup` على السيرفر 2.", "Backups are late: `docker compose logs backup` on server 2."),
        _ => ""
    };

    /// <summary>`command` parts become left-to-right code so they stay readable inside Arabic sentences.</summary>
    private static string TipHtml(string tip)
    {
        var parts = tip.Split('`');
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
            sb.Append(i % 2 == 0
                ? E(parts[i])
                : $"<code dir=\"ltr\" style=\"display:inline-block;font-family:Consolas,Menlo,monospace;font-size:12px;background:#eef0f6;color:#16205b;padding:1px 6px;border-radius:6px;unicode-bidi:isolate;\">{E(parts[i])}</code>");
        return sb.ToString();
    }

    private static (string Bg, string Fg, string Text) Chip(AlertLevel level, string lang) => level switch
    {
        AlertLevel.Critical => ("#fee2e2", "#b91c1c", L(lang, "خطر", "Critical")),
        AlertLevel.Warning => ("#ffedd5", "#c2410c", L(lang, "تحذير", "Warning")),
        _ => ("#dcfce7", "#15803d", L(lang, "طبيعي", "OK"))
    };

    public static EmailContent Build(string lang, IReadOnlyList<AlertChange> changes, IReadOnlyList<MetricReading> readings, MonitorOptions o, DateTime now)
    {
        var rtl = lang == "ar";
        var start = rtl ? "right" : "left";
        var worst = changes.Where(c => !c.Recovered).Select(c => c.To).DefaultIfEmpty(AlertLevel.Ok).Max();
        var allRepeat = changes.All(c => c.Repeat);
        var first = changes.OrderByDescending(c => c.To).First();
        var headline = $"{ServerName(first.Reading.Server, lang)} — {Label(first.Reading.Key, lang)} {first.Reading.Display}"
                       + (changes.Count > 1 ? $" (+{changes.Count - 1})" : "");

        var (tone, icon, badge, title) = worst switch
        {
            AlertLevel.Critical => (EmailTone.Danger, "🔴", L(lang, "تنبيه خطير", "Critical alert"),
                allRepeat ? L(lang, "المشكلة ما زالت قائمة", "Still happening") : L(lang, "موارد السيرفر وصلت لمستوى خطر", "Server resources reached a dangerous level")),
            AlertLevel.Warning => (EmailTone.Warning, "🟠", L(lang, "تحذير", "Warning"),
                allRepeat ? L(lang, "المشكلة ما زالت قائمة", "Still happening") : L(lang, "موارد السيرفر تقترب من الحد", "Server resources are getting close to the limit")),
            _ => (EmailTone.Success, "✅", L(lang, "رجعت طبيعية", "Resolved"), L(lang, "رجعت الموارد لطبيعتها", "Server resources are back to normal"))
        };

        var h = new StringBuilder();
        h.Append($"""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:6px 0 18px;">""");
        foreach (var c in changes)
        {
            var (bg, fg, text) = Chip(c.To, lang);
            var from = c.Repeat ? L(lang, "ما زالت مستمرة", "still ongoing") : L(lang, $"من {Chip(c.From, lang).Text} إلى {text}", $"{Chip(c.From, lang).Text} → {text}");
            h.Append($"""
<tr><td style="padding:10px 14px;border:1px solid #eceef5;border-radius:12px;background:#ffffff;text-align:{start};">
  <span style="display:inline-block;background:{bg};color:{fg};font-size:12px;font-weight:700;padding:3px 10px;border-radius:999px;">{E(text)}</span>
  <span style="font-size:14px;font-weight:700;color:#0b0d1a;padding:0 6px;">{E(ServerName(c.Reading.Server, lang))} — {E(Label(c.Reading.Key, lang))}</span>
  <div style="font-size:13px;color:#475467;padding-top:4px;"><span dir="ltr" style="unicode-bidi:isolate;font-weight:700;color:#0b0d1a;">{E(c.Reading.Display)}</span> · {E(L(lang, "الحد", "limit"))} <span dir="ltr" style="unicode-bidi:isolate;">{E(Threshold(c.Reading.Key, o, lang))}</span> · {E(from)}</div>
</td></tr>
<tr><td height="8" style="height:8px;font-size:0;line-height:8px;">&nbsp;</td></tr>
""");
        }
        h.Append("</table>");

        foreach (var server in new[] { ServerIds.App, ServerIds.Database })
        {
            var rows = readings.Where(r => r.Server == server).ToList();
            if (rows.Count == 0) continue;
            h.Append($"""<p style="margin:14px 0 8px;font-size:13px;font-weight:800;color:#16205b;letter-spacing:.2px;">{E(ServerName(server, lang))}</p>""");
            h.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#f7f8fc;border:1px solid #eceef5;border-radius:14px;">""");
            for (var i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var (bg, fg, text) = Chip(r.Level, lang);
                var border = i < rows.Count - 1 ? "border-bottom:1px solid #eceef5;" : "";
                h.Append($"""
<tr>
  <td style="padding:10px 14px;{border}font-size:13px;color:#475467;text-align:{start};">{E(Label(r.Key, lang))}</td>
  <td style="padding:10px 14px;{border}font-size:13px;font-weight:700;color:#0b0d1a;text-align:{start};" dir="ltr">{E(r.Display)}</td>
  <td style="padding:10px 14px;{border}text-align:{(rtl ? "left" : "right")};"><span style="display:inline-block;background:{bg};color:{fg};font-size:11px;font-weight:700;padding:2px 9px;border-radius:999px;">{E(text)}</span></td>
</tr>
""");
            }
            h.Append("</table>");
        }

        var tips = changes.Where(c => !c.Recovered).Select(c => Tip(c.Reading.Server, c.Reading.Key, lang)).Where(t => t != "").Distinct().ToList();
        if (tips.Count > 0)
        {
            h.Append($"""<p style="margin:20px 0 8px;font-size:14px;font-weight:800;color:#0b0d1a;">{E(L(lang, "تعمل إيه دلوقتي", "What to do"))}</p>""");
            h.Append($"""<ul style="margin:0;padding-{start}:20px;padding-{(rtl ? "left" : "right")}:0;font-size:14px;line-height:24px;color:#475467;">""");
            foreach (var t in tips) h.Append($"<li style=\"margin:0 0 6px;\">{TipHtml(t)}</li>");
            h.Append("</ul>");
        }

        var text2 = new StringBuilder();
        foreach (var c in changes) text2.AppendLine($"- {ServerName(c.Reading.Server, lang)} — {Label(c.Reading.Key, lang)}: {c.Reading.Display} [{Chip(c.To, lang).Text}]");
        foreach (var t in tips) text2.AppendLine("* " + t.Replace("`", ""));

        return new EmailContent
        {
            Lang = lang,
            Tone = tone,
            Badge = badge,
            Subject = $"{icon} Casco — {badge}: {headline}",
            Preheader = headline,
            Title = title,
            Paragraphs = [L(lang, $"وقت الفحص: {now:yyyy-MM-dd HH:mm} UTC", $"Checked at {now:yyyy-MM-dd HH:mm} UTC")],
            HtmlBlock = h.ToString(),
            TextBlock = text2.ToString(),
            Note = worst == AlertLevel.Ok ? null : L(lang,
                $"هنبعتلك تاني لو المشكلة زادت، أو كل {o.RepeatHours} ساعات لو فضلت موجودة، وهنبعت رسالة لما ترجع طبيعية.",
                $"We'll email again if it gets worse, every {o.RepeatHours} hours while it lasts, and once it's back to normal.")
        };
    }
}
