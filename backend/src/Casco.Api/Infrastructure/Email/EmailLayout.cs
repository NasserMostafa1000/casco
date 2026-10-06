using System.Globalization;
using System.Net;
using System.Text;

namespace Casco.Api.Infrastructure.Email;

public enum EmailTone { Brand, Success, Warning, Danger }

public record EmailFact(string Label, string Value, bool Ltr = false);

public record EmailButton(string Text, string Url);

public class EmailContent
{
    public required string Lang { get; init; }
    public required string Subject { get; init; }
    public required string Title { get; init; }
    /// <summary>Short text shown next to the subject in the inbox list.</summary>
    public string Preheader { get; init; } = "";
    public string? Badge { get; init; }
    public EmailTone Tone { get; init; } = EmailTone.Brand;
    public IReadOnlyList<string> Paragraphs { get; init; } = [];
    public IReadOnlyList<EmailFact> Facts { get; init; } = [];
    /// <summary>Trusted HTML inserted after the facts (already encoded by the caller).</summary>
    public string? HtmlBlock { get; init; }
    public string? TextBlock { get; init; }
    public EmailButton? Button { get; init; }
    public string? Note { get; init; }
}

/// <summary>Language helpers shared by every e-mail (Arabic, English, Hindi; anything else falls back to English).</summary>
public static class EmailText
{
    public static readonly string[] Languages = ["ar", "en", "hi"];

    public static string Normalize(string? lang) => lang is "ar" or "en" or "hi" ? lang : "en";

    public static bool IsRtl(string lang) => lang == "ar";

    public static string L(string lang, string ar, string en, string hi) => Normalize(lang) switch
    {
        "ar" => ar,
        "hi" => hi,
        _ => en
    };

    private static CultureInfo Culture(string lang) => Normalize(lang) switch
    {
        "ar" => CultureInfo.GetCultureInfo("ar-EG"),
        "hi" => CultureInfo.GetCultureInfo("hi-IN"),
        _ => CultureInfo.GetCultureInfo("en-US")
    };

    public static string Date(DateTime utc, string lang)
    {
        var culture = Culture(lang);
        var format = Normalize(lang) == "en" ? "MMMM d, yyyy" : "d MMMM yyyy";
        return utc.ToString(format, culture);
    }

    public static string DateAndTime(DateTime utc, string lang) => $"{Date(utc, lang)} · {utc:HH:mm} UTC";

    public static string Number(long n) => n.ToString("#,0", CultureInfo.InvariantCulture);
}

/// <summary>Branded, table-based HTML layout that renders in Gmail, Outlook and Apple Mail, plus a plain-text part.</summary>
public static class EmailLayout
{
    private const string Font = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,'Helvetica Neue',Arial,'Noto Sans Arabic','Noto Sans Devanagari',Tahoma,sans-serif";

    private static (string Solid, string Gradient, string SoftBg, string SoftText) Colors(EmailTone tone) => tone switch
    {
        EmailTone.Success => ("#059669", "linear-gradient(90deg,#10b981,#059669)", "#ecfdf5", "#047857"),
        EmailTone.Warning => ("#ea580c", "linear-gradient(90deg,#f59e0b,#f97316)", "#fff7ed", "#c2410c"),
        EmailTone.Danger => ("#dc2626", "linear-gradient(90deg,#ef4444,#e11d48)", "#fef2f2", "#b91c1c"),
        _ => ("#4f46e5", "linear-gradient(90deg,#4f46e5,#7c3aed,#c026d3)", "#eef2ff", "#4338ca")
    };

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Style(string link) => $$"""
<style>
  body { margin:0; padding:0; background:#f1f3f9; }
  a { color:{{link}}; }
  @media (max-width:620px) {
    .container { width:100% !important; }
    .pad { padding:28px 22px !important; }
    .title { font-size:22px !important; line-height:30px !important; }
    .btn a { display:block !important; text-align:center !important; }
    .fact-label, .fact-value { display:block !important; width:100% !important; box-sizing:border-box !important; }
    .fact-rtl { text-align:right !important; }
    .fact-ltr { text-align:left !important; }
    .fact-label { border-bottom:0 !important; padding-bottom:0 !important; }
    .fact-value { padding-top:2px !important; }
  }
</style>
""";

    public static (string Html, string Text) Render(EmailContent c, string homeUrl, string? logoSrc = null) => Render([c], homeUrl, logoSrc);

    /// <summary>Subject of a message made of several language versions, e.g. "Arabic | English".</summary>
    public static string Subject(IReadOnlyList<EmailContent> parts) => string.Join(" | ", parts.Select(p => p.Subject).Distinct());

    /// <summary>One message with a card per language version (same content in e.g. Arabic then English).</summary>
    public static (string Html, string Text) Render(IReadOnlyList<EmailContent> parts, string homeUrl, string? logoSrc = null)
    {
        var first = parts[0];
        var lang = EmailText.Normalize(first.Lang);
        var dir = EmailText.IsRtl(lang) ? "rtl" : "ltr";
        var logo = logoSrc ?? "cid:" + EmailAssets.LogoContentId;
        var home = homeUrl.TrimEnd('/');
        var homeLabel = home.Replace("https://", "").Replace("http://", "");
        var langs = parts.Select(p => EmailText.Normalize(p.Lang)).Distinct().ToList();

        var h = new StringBuilder();
        h.Append($"""
<!DOCTYPE html>
<html lang="{lang}" dir="{dir}" xmlns="http://www.w3.org/1999/xhtml">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="x-apple-disable-message-reformatting">
<meta name="color-scheme" content="light">
<meta name="supported-color-schemes" content="light">
<title>{E(Subject(parts))}</title>
{Style(Colors(first.Tone).Solid)}
</head>
<body style="margin:0;padding:0;background:#f1f3f9;">
<div style="display:none;max-height:0;overflow:hidden;opacity:0;color:transparent;">{E(first.Preheader)}&#8203;&#8199;&#65279;&#847; &#8203;&#8199;&#65279;&#847; &#8203;&#8199;&#65279;&#847;</div>
<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="background:#f1f3f9;">
<tr><td align="center" style="padding:32px 12px;">
<table role="presentation" class="container" width="600" cellpadding="0" cellspacing="0" border="0" style="width:600px;max-width:600px;">
<tr><td align="center" style="padding:0 0 22px;">
  <a href="{E(home)}" style="text-decoration:none;display:inline-block;" dir="ltr">
    <img src="{E(logo)}" width="44" height="44" alt="Casco" style="display:inline-block;vertical-align:middle;border:0;width:44px;height:44px;">
    <span style="display:inline-block;vertical-align:middle;padding-left:10px;font-family:{Font};font-size:22px;font-weight:800;letter-spacing:4px;color:#16205b;">CASCO</span>
  </a>
</td></tr>
""");
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) h.Append("""<tr><td height="18" style="height:18px;line-height:18px;font-size:0;">&nbsp;</td></tr>""");
            AppendCard(h, parts[i]);
        }

        var year = DateTime.UtcNow.Year;
        h.Append($"""<tr><td align="center" style="padding:24px 16px 0;font-family:{Font};font-size:12px;line-height:20px;color:#98a2b3;text-align:center;">""" + "\n");
        foreach (var l in langs)
            h.Append($"""<div dir="{(EmailText.IsRtl(l) ? "rtl" : "ltr")}">{E(EmailText.L(l, "Casco — ابنِ موقعك بالذكاء الاصطناعي", "Casco — build your website with AI", "Casco — AI से अपनी वेबसाइट बनाएं"))}</div>""");
        h.Append($"""<a href="{E(home)}" style="color:#667085;text-decoration:none;font-weight:600;" dir="ltr">{E(homeLabel)}</a>""");
        foreach (var l in langs)
            h.Append($"""<div dir="{(EmailText.IsRtl(l) ? "rtl" : "ltr")}">{E(EmailText.L(l, "وصلتك هذه الرسالة لأن لديك حساباً على Casco. هذا بريد آلي، لا ترد عليه.", "You're receiving this because you have a Casco account. This is an automated message — please don't reply.", "यह ईमेल आपको इसलिए मिला क्योंकि आपका Casco पर खाता है। यह स्वचालित संदेश है — कृपया जवाब न दें।"))}</div>""");
        h.Append($"""
  <span dir="ltr">© {year} Casco</span>
</td></tr>
</table>
</td></tr>
</table>
</body>
</html>
""");

        var t = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) t.AppendLine("――――――――――").AppendLine();
            AppendText(t, parts[i]);
        }
        t.AppendLine("— Casco · " + homeLabel);
        return (h.ToString(), t.ToString());
    }

    private static void AppendCard(StringBuilder h, EmailContent c)
    {
        var lang = EmailText.Normalize(c.Lang);
        var rtl = EmailText.IsRtl(lang);
        var dir = rtl ? "rtl" : "ltr";
        var start = rtl ? "right" : "left";
        var end = rtl ? "left" : "right";
        var (solid, gradient, softBg, softText) = Colors(c.Tone);
        var L = (string ar, string en, string hi) => EmailText.L(lang, ar, en, hi);

        h.Append($"""
<tr><td lang="{lang}" style="background:#ffffff;border:1px solid #e6e8f0;border-radius:20px;overflow:hidden;box-shadow:0 12px 32px -18px rgba(22,32,91,.35);">
  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
  <tr><td height="6" bgcolor="{solid}" style="height:6px;line-height:6px;font-size:0;background:{solid};background-image:{gradient};border-radius:20px 20px 0 0;">&nbsp;</td></tr>
  <tr><td class="pad" dir="{dir}" style="padding:36px 40px 34px;font-family:{Font};text-align:{start};">
""");

        if (!string.IsNullOrEmpty(c.Badge))
            h.Append($"""<div style="margin:0 0 14px;"><span style="display:inline-block;background:{softBg};color:{softText};font-size:12px;font-weight:700;letter-spacing:.3px;padding:5px 12px;border-radius:999px;">{E(c.Badge)}</span></div>""");

        h.Append($"""<h1 class="title" style="margin:0 0 14px;font-family:{Font};font-size:25px;line-height:34px;font-weight:800;color:#0b0d1a;">{E(c.Title)}</h1>""");

        foreach (var p in c.Paragraphs)
            h.Append($"""<p style="margin:0 0 14px;font-size:15px;line-height:26px;color:#475467;">{E(p)}</p>""");

        if (c.Facts.Count > 0)
        {
            h.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="margin:10px 0 6px;background:#f7f8fc;border:1px solid #eceef5;border-radius:14px;">""");
            for (var i = 0; i < c.Facts.Count; i++)
            {
                var f = c.Facts[i];
                var border = i < c.Facts.Count - 1 ? "border-bottom:1px solid #eceef5;" : "";
                var valueDir = f.Ltr ? " dir=\"ltr\" style=\"unicode-bidi:isolate;\"" : "";
                h.Append($"""
<tr>
  <td class="fact-label fact-{dir}" style="padding:12px 18px;{border}font-size:13px;line-height:20px;color:#667085;text-align:{start};white-space:nowrap;">{E(f.Label)}</td>
  <td class="fact-value fact-{dir}" style="padding:12px 18px;{border}font-size:14px;line-height:20px;font-weight:700;color:#0b0d1a;text-align:{end};word-break:break-word;"><span{valueDir}>{E(f.Value)}</span></td>
</tr>
""");
            }
            h.Append("</table>");
        }

        if (!string.IsNullOrEmpty(c.HtmlBlock)) h.Append(c.HtmlBlock);

        if (c.Button is { } b)
        {
            h.Append($"""
<table role="presentation" cellpadding="0" cellspacing="0" border="0" class="btn" style="margin:24px 0 8px;">
<tr><td bgcolor="{solid}" style="border-radius:12px;background:{solid};background-image:{gradient};">
  <a href="{E(b.Url)}" target="_blank" style="display:inline-block;padding:14px 30px;font-family:{Font};font-size:15px;font-weight:700;color:#ffffff;text-decoration:none;border-radius:12px;">{E(b.Text)}</a>
</td></tr>
</table>
<p style="margin:10px 0 0;font-size:12px;line-height:19px;color:#98a2b3;">{E(L("إذا لم يعمل الزر، انسخ هذا الرابط في المتصفح:", "If the button doesn't work, paste this link into your browser:", "अगर बटन काम न करे, तो यह लिंक ब्राउज़र में खोलें:"))}<br><a href="{E(b.Url)}" dir="ltr" style="color:#667085;word-break:break-all;">{E(b.Url)}</a></p>
""");
        }

        if (!string.IsNullOrEmpty(c.Note))
            h.Append($"""<p style="margin:18px 0 0;padding-top:16px;border-top:1px solid #eef0f5;font-size:13px;line-height:21px;color:#667085;">{E(c.Note)}</p>""");

        h.Append("""
  </td></tr>
  </table>
</td></tr>
""");
    }

    private static void AppendText(StringBuilder t, EmailContent c)
    {
        t.AppendLine(c.Title).AppendLine();
        foreach (var p in c.Paragraphs) t.AppendLine(p).AppendLine();
        foreach (var f in c.Facts) t.AppendLine($"{f.Label}: {f.Value}");
        if (c.Facts.Count > 0) t.AppendLine();
        if (!string.IsNullOrEmpty(c.TextBlock)) t.AppendLine(c.TextBlock).AppendLine();
        if (c.Button is { } tb) t.AppendLine($"{tb.Text}: {tb.Url}").AppendLine();
        if (!string.IsNullOrEmpty(c.Note)) t.AppendLine(c.Note).AppendLine();
    }
}
