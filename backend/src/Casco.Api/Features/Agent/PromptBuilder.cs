using System.Text;
using System.Text.RegularExpressions;
using Casco.Api.Features.Ai;
using Casco.Api.Features.SiteRuntime;
using Casco.Api.Features.Sites;

namespace Casco.Api.Features.Agent;

public record PromptContext(
    string SiteName,
    string TemplateKey,
    string[] Modules,
    bool IsPro,
    bool IsNewSite,
    string Request,
    IReadOnlyList<(string Role, string Content)> History,
    SiteLimits? Limits = null,
    BuildProgress? Progress = null,
    string? Language = null);

public record SiteLimits(int MaxFiles, int MaxTotalBytes);

/// <summary>Where a multi-part build stands: summaries of the finished parts and what the model said is left.</summary>
public record BuildProgress(int Part, IReadOnlyList<string> Done, string Remaining);

/// <summary>
/// Message order is deliberate for provider prompt caching: the static system prompt first (identical for every
/// request), then the site files, and only then the per-user, per-request text.
/// OpenAI reuses a new request's prefix only up to the end of the system messages (or a whole earlier prompt),
/// so the files join the system block when they are shared: a fresh template is identical for every user of it.
/// Edited sites differ per version, so their files stay out of it and the system prompt alone keeps being reused.
/// </summary>
public static partial class PromptBuilder
{
    public static List<ChatMessageDto> Build(IReadOnlyDictionary<string, string> files, PromptContext ctx, int contextBytesLimit)
    {
        var sharedFiles = ctx.IsNewSite && ctx.Progress is null;
        var shown = SelectFiles(files, ctx.Request, contextBytesLimit);
        return
        [
            new("system", SystemPrompt()),
            new(sharedFiles ? "system" : "user", FilesMessage(shown, files)),
            new("user", RequestMessage(ctx, files, shown.Count < files.Count))
        ];
    }

    public static string FeedbackMessage(IEnumerable<string> errors, IReadOnlyDictionary<string, string> files, IEnumerable<string> affectedPaths)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Your previous response could not be fully applied. Problems:");
        foreach (var e in errors) sb.AppendLine("- " + e);
        sb.AppendLine();
        sb.AppendLine("Edits that succeeded are already applied. Current content of the affected files:");
        foreach (var path in affectedPaths.Distinct().Where(files.ContainsKey))
            AppendFile(sb, path, files[path]);
        sb.AppendLine("Return a new JSON object containing ONLY the edits still needed to finish the original request and fix these problems.");
        return sb.ToString();
    }

    public const int MaxReadFiles = 6;
    public const string ReadReplyHeader = "## Files you asked to read";
    public const string SiteMapHeader = "## Site map: files not shown above";

    public const string NoEditsNudge = "You returned no edits and asked to read no files. If a file you must change is only in the site map, " +
        "reply with {\"read\": [...], \"files\": []} now; otherwise make the edits now. Reply without edits only to ask the user a real question.";

    public static bool HasSiteMap(IReadOnlyList<ChatMessageDto> messages) =>
        messages.Count > 1 && messages[1].Content.Contains(SiteMapHeader, StringComparison.Ordinal);

    /// <summary>
    /// Context engine: send everything when small; otherwise the most relevant files, ranked by what the request names
    /// (file names, page titles, headings, and the menu text other pages use to link to them, so «صفحة الأسعار» finds
    /// pricing.html), plus a site map of the rest that the model can ask to read.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SelectFiles(IReadOnlyDictionary<string, string> files, string request, int limit)
    {
        if (SiteFiles.TotalBytes(files.ToDictionary()) <= limit) return files;

        var words = Words(request);
        var map = Describe(files);
        var scored = files
            .Select(f =>
            {
                var meta = Normalize(f.Key + " " + map[f.Key].Searchable);
                var content = Normalize(f.Value);
                return (Path: f.Key, Content: f.Value, Core: f.Key == "index.html" || f.Key.StartsWith("assets/"),
                    Score: words.Count(w => meta.Contains(w, StringComparison.Ordinal)) * 40 + words.Count(w => content.Contains(w, StringComparison.Ordinal)));
            })
            .ToList();
        // Only pages about as relevant as the best match: "المقال 3" sends blog-3.html, not every post that says "المقال".
        // Unrelated pages stay in the site map and the model reads them if it needs them, instead of paying for them every time.
        var best = scored.Where(f => !f.Core).Select(f => f.Score).DefaultIfEmpty(0).Max();
        var threshold = Math.Max(1, best * 3 / 4);
        var pages = scored.Where(f => !f.Core && f.Score >= threshold).ToList();
        // Equally relevant pages that do not all fit tell nothing apart (every post matches "المقال"): drop that tier
        // rather than sending an arbitrary few, and let the model pick from the site map.
        var coreBytes = scored.Where(f => f.Core).Sum(f => Encoding.UTF8.GetByteCount(f.Content));
        while (pages.Count > 0 && coreBytes + pages.Sum(f => Encoding.UTF8.GetByteCount(f.Content)) > limit)
        {
            var lowest = pages.Min(f => f.Score);
            pages.RemoveAll(f => f.Score == lowest);
        }
        var ranked = scored.Where(f => f.Core).Concat(pages)
            .OrderByDescending(f => f.Path == "index.html").ThenByDescending(f => f.Core).ThenByDescending(f => f.Score)
            .ThenBy(f => f.Path, StringComparer.Ordinal);

        var selected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var used = 0;
        foreach (var f in ranked)
        {
            var size = Encoding.UTF8.GetByteCount(f.Content);
            if (used + size > limit && selected.Count > 0) continue;
            selected[f.Path] = f.Content;
            used += size;
        }
        return selected;
    }

    private static string FilesMessage(IReadOnlyDictionary<string, string> shown, IReadOnlyDictionary<string, string> all)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Current website files");
        // Shared assets first: they change least often, which keeps the cached prefix longer.
        foreach (var (path, content) in shown.OrderBy(f => f.Key.StartsWith("assets/") ? 0 : 1).ThenBy(f => f.Key, StringComparer.Ordinal))
            AppendFile(sb, path, content);
        var hidden = all.Keys.Where(k => !shown.ContainsKey(k)).ToList();
        if (hidden.Count > 0)
        {
            sb.AppendLine(SiteMapHeader);
            sb.AppendLine("path (size) | page title | main headings | menu text other pages use to link to it");
            var map = Describe(all);
            foreach (var h in hidden) sb.AppendLine(map[h].Line);
            sb.AppendLine($"To see any of these before editing, reply with ONLY {{\"read\": [\"page.html\", ...], \"files\": []}} (at most {MaxReadFiles} files); they will be sent to you and then you make the edits.");
        }
        return sb.ToString();
    }

    /// <summary>The model's answer to a "read" reply: the requested files, within the context budget.</summary>
    public static string ReadReply(IEnumerable<string> requested, IReadOnlyDictionary<string, string> files, int limit, bool last)
    {
        var sb = new StringBuilder();
        sb.AppendLine(ReadReplyHeader);
        var used = 0;
        var missing = new List<string>();
        var skipped = new List<string>();
        foreach (var path in requested.Select(p => (p ?? "").Trim().TrimStart('.', '/').Replace('\\', '/')).Where(p => p.Length > 0).Distinct().Take(MaxReadFiles))
        {
            if (!files.TryGetValue(path, out var content)) { missing.Add(path); continue; }
            var size = Encoding.UTF8.GetByteCount(content);
            if (used > 0 && used + size > limit) { skipped.Add(path); continue; }
            AppendFile(sb, path, content);
            used += size;
        }
        if (missing.Count > 0) sb.AppendLine("These files do not exist: " + string.Join(", ", missing));
        if (skipped.Count > 0) sb.AppendLine("Not sent because together they are too large: " + string.Join(", ", skipped) + ". Work with what you have.");
        sb.AppendLine(last
            ? "No more files can be read for this request. Now reply with the JSON edits for the original request."
            : "Now reply with the JSON edits for the original request (or one more \"read\" only if a file you must change is still missing).");
        return sb.ToString();
    }

    private record SiteFileInfo(string Line, string Searchable);

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex TitleTag();
    [GeneratedRegex(@"<h[1-3][^>]*>(.*?)</h[1-3]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)] private static partial Regex HeadingTag();
    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*[""'](?:\./)?([^""'#?:]+\.html)[^""']*[""'][^>]*>(.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LinkTag();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex AnyTag();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
    [GeneratedRegex(@"[\u064B-\u0652\u0640]")] private static partial Regex ArabicMarks();

    /// <summary>One site-map line per file, plus the text used to match requests against it.</summary>
    private static Dictionary<string, SiteFileInfo> Describe(IReadOnlyDictionary<string, string> files)
    {
        var labels = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (_, content) in files.Where(f => SiteFiles.IsHtml(f.Key) || f.Key.EndsWith(".js", StringComparison.Ordinal)))
            foreach (Match m in LinkTag().Matches(content))
            {
                var label = Clean(m.Groups[2].Value, 40);
                if (label.Length == 0) continue;
                var target = m.Groups[1].Value.TrimStart('.', '/');
                if (!labels.TryGetValue(target, out var list)) labels[target] = list = [];
                if (!list.Contains(label) && list.Count < 4) list.Add(label);
            }

        var result = new Dictionary<string, SiteFileInfo>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            var line = new StringBuilder($"- {path} ({Math.Max(1, Encoding.UTF8.GetByteCount(content) / 1000)} KB)");
            var searchable = new StringBuilder();
            if (SiteFiles.IsHtml(path))
            {
                var title = TitleTag().Match(content) is { Success: true } t ? Clean(t.Groups[1].Value, 70) : "";
                var headings = HeadingTag().Matches(content).Select(h => Clean(h.Groups[1].Value, 50)).Where(h => h.Length > 0).Distinct().Take(6).ToList();
                if (title.Length > 0) line.Append($" | \"{title}\"");
                if (headings.Count > 0) line.Append(" | ").Append(string.Join(" · ", headings));
                searchable.Append(title).Append(' ').AppendJoin(' ', headings);
            }
            if (labels.TryGetValue(path, out var linked))
            {
                line.Append(" | linked as: ").Append(string.Join(", ", linked));
                searchable.Append(' ').AppendJoin(' ', linked);
            }
            result[path] = new SiteFileInfo(line.ToString(), searchable.ToString());
        }
        return result;
    }

    private static string Clean(string html, int max)
    {
        var text = Spaces().Replace(System.Net.WebUtility.HtmlDecode(AnyTag().Replace(html, " ")), " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>Case, Arabic letter variants, diacritics and digits folded so «الأسعار», «أسعار» and «اسعار» (or ٣ and 3) match.</summary>
    public static string Normalize(string text)
    {
        var chars = ArabicMarks().Replace(text.ToLowerInvariant(), "").ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                >= '٠' and <= '٩' => (char)('0' + chars[i] - '٠'),
                >= '۰' and <= '۹' => (char)('0' + chars[i] - '۰'),
                var c => c
            };
        return new string(chars);
    }

    /// <summary>Request words worth matching; numbers count at any length ("المقال 3" -> blog-3.html).</summary>
    private static string[] Words(string request) => Normalize(request)
        .Split([' ', '\n', '\r', '\t', ',', '.', '،', '?', '؟', '!', ':', '"', '«', '»', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
        .Select(w => w.StartsWith("ال", StringComparison.Ordinal) && w.Length > 4 ? w[2..] : w)
        .Where(w => (w.Length >= 3 || w.All(char.IsAsciiDigit)) && !StopWords.Contains(w)).Distinct().ToArray();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "صفحه", "صفحات", "page", "the", "and", "add", "اضف", "ضيف", "عدل", "غير", "خلي", "عاوز", "عايز", "اريد", "علي", "الي", "with", "for", "change"
    };

    private static string RequestMessage(PromptContext ctx, IReadOnlyDictionary<string, string> files, bool partial)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Site");
        sb.AppendLine($"Name: {ctx.SiteName}");
        sb.AppendLine($"Template: {ctx.TemplateKey} (modules: {string.Join(", ", ctx.Modules)})");
        sb.AppendLine(ctx.IsPro
            ? "Plan: Pro, paid. A new site must be large: long pages the visitor scrolls through, with a lot of writing about THIS business, not a short landing page."
            : "Plan: Free. Build a real multi-page site, not one or two pages: index.html, about.html, a services or menu or products page, gallery.html, and contact.html. Each page gets its own wide banner photo. Supporting files are expected (assets/*.js). A store, bookings, and forms still use the Casco SDK.");

        if (ctx.Limits is { } limits)
        {
            sb.AppendLine($"Limits: {files.Count} of {limits.MaxFiles} files used, {SiteFiles.TotalBytes(files.ToDictionary()) / 1000} KB of {limits.MaxTotalBytes / 1000} KB.");
        }

        if (ctx.History.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Recent conversation");
            // The latest reply may list what is left of a big build ("continue" requests rely on it).
            var lastAssistant = ctx.History.Select((h, i) => (h.Role, i)).LastOrDefault(h => h.Role == "assistant").i;
            for (var i = 0; i < ctx.History.Count; i++)
            {
                var (role, content) = ctx.History[i];
                sb.AppendLine($"- {role}: {Truncate(content, role == "assistant" && i == lastAssistant ? 1500 : 300)}");
            }
        }

        sb.AppendLine();
        if (ctx.IsNewSite)
        {
            sb.AppendLine("## Task");
            sb.AppendLine("This is a brand-new website. The template is only a starting point for features, not for the look. Rewrite every HTML page with op \"write\" as an original Tailwind design for THIS business. Do not keep the template's gradient hero, six equal cards, section order, or spacing. Keep the Tailwind browser script, the @theme block, data-casco-form=\"contact\", and every working feature. Use the business name instead of {{BRAND}}. Write every visitor-facing word in the interface language from ## Language.");
            sb.AppendLine("Layout direction for this site only: " + LayoutDirection(ctx.Request) + ". Style it with Tailwind utilities (grid, flex, gap, max-w-*, aspect, object-cover, rounded, shadow, type scale). A restaurant must not look like a clinic or a shop.");
            if (ctx.IsPro)
            {
                sb.AppendLine("Size and story: this paid site is long. Build index.html, about.html, a services or menu or products page, gallery.html, and contact.html, linked from one shared header, and add more pages when the business needs them (process, story, FAQ, a page per main offer).");
                sb.AppendLine("The home page opens with a full-width banner photo, at least 70vh, then at least eight more sections. Each section has a heading and two to four sentences written only about this business: what it is, who it is for, how the work happens, what is different, the concrete offers from the description, a process, reviews in that business's voice, questions customers of this business ask, and a closing invitation. Every other page is also a long scroll with several sections and real paragraphs, not a title and three short cards.");
                sb.AppendLine("Use details from the description. Where the user was brief, invent specific facts that fit this business (names of offers, how a visit or order works, what the customer leaves with). No lorem ipsum, no generic lines like \"we care about quality\" that could fit any company. A spice shop talks about spices, origins, and cooking; a clinic talks about the visit, the care, and the patients. If this reply cannot hold every page, finish the pages you start and list the rest in remaining so the build continues.");
            }
            else
                sb.AppendLine("Size: do not stop at one or two pages. Build index.html, about.html, a services or menu or products page, gallery.html, and contact.html, linked from one shared header. The home page opens with a full-width banner photo, at least 70vh, of this business. Every other page has its own shorter banner.");
            sb.AppendLine("Photos: replace every template photo with stock photos of this exact business (see the Images rule). A cleaning company shows cleaners, vacuums and homes being cleaned; a restaurant shows its food. Never leave a photo URL from any other website. The home hero is a slider of at least three of those photos.");
            sb.AppendLine("Colors and logo: replace the template palette with colors that belong to this business only. If the user uploaded a logo, it stays in the header and the footer.");
            sb.AppendLine("Contact details: put the phone/WhatsApp, address, email, opening hours and social pages from the description everywhere they belong (contact section, footer, a WhatsApp button with https://wa.me/<digits>, tel: and mailto: links, a Google Maps link for the address, social icons). For any detail the user did not give, leave the template sample in place and end the summary with a short friendly question asking for the missing ones (phone/WhatsApp, address/location, email, social pages) so you can add them.");
        }
        if (partial)
            sb.AppendLine("Only some files are shown in full. If a file you must change is only in the site map, your reply must be {\"read\": [...], \"files\": []}; never reply with only a plan or a promise.");
        if (!string.IsNullOrWhiteSpace(ctx.Language))
        {
            sb.AppendLine("## Language");
            sb.AppendLine($"The user's interface language is {ctx.Language}.");
            sb.AppendLine($"Write thinking, summary, and remaining only in {ctx.Language}. Do not answer in another language.");
            sb.AppendLine(ctx.IsNewSite
                ? $"Write the whole website in {ctx.Language}: pages, menus, buttons, alerts, form labels, placeholders, and JavaScript messages. Set html lang and dir for {ctx.Language}."
                : $"Write any new visitor-facing text in {ctx.Language}. Keep text already on the site unless the user asks to change its language.");
            sb.AppendLine();
        }
        sb.AppendLine("## Request");
        sb.AppendLine(ctx.Request.Trim());
        if (ctx.Progress is { } progress)
        {
            sb.AppendLine();
            sb.AppendLine($"## Continue (part {progress.Part})");
            sb.AppendLine("This request is being built in parts. Done in earlier parts (their files are already in the site):");
            foreach (var done in progress.Done) sb.AppendLine("- " + Truncate(done, 400));
            sb.AppendLine("Still left according to your plan: " + Truncate(progress.Remaining, 1000));
            sb.AppendLine("Build the next part now. Set \"remaining\" again if more is still left after this part.");
            if (ctx.IsPro && ctx.IsNewSite)
                sb.AppendLine("Keep each new page a long scroll with several sections and paragraphs about this business. Do not shorten the story to finish faster.");
        }
        return sb.ToString();
    }

    private static void AppendFile(StringBuilder sb, string path, string content)
    {
        sb.Append("<<<FILE ").Append(path).AppendLine(">>>");
        sb.AppendLine(content);
        sb.AppendLine("<<<END FILE>>>");
    }

    private static readonly string[] Layouts =
    [
        "full-bleed photo with the headline over the image and a short menu underneath",
        "asymmetric split: large photo on one side, stacked text and one button on the other, no badge",
        "dark luxury page: near-black background, one serif-style heading, wide product photos",
        "warm editorial: cream background, big type, a horizontal photo strip, then a short story",
        "calm clinic: lots of white space, soft corners, a quiet hero and a simple two-column services list",
        "bold product grid: the first screen is the products or dishes, filters on top, little text"
    ];

    /// <summary>Stable per description so a rebuild keeps its direction, and different businesses do not share one.</summary>
    public static string LayoutDirection(string request)
    {
        var hash = 17;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(request.Trim())) hash = unchecked(hash * 31 + b);
        return Layouts[(hash & int.MaxValue) % Layouts.Length];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public static string SystemPrompt() => SystemPromptTemplate.Replace("{{STOCK_URL}}", StockImages.BaseUrl);

    private const string SystemPromptTemplate = """
You are Casco, an expert web designer and front-end developer. You build and edit websites, in any language, for non-technical small-business owners (many of them Arabic speakers). Each website is React 19 rendered with htm (no JSX, no build step) and styled only with Tailwind CSS (v4 browser build, already included in every page).

# Owner
Casco is owned by ناصر مصطفي البربري (Nasser Mostafa El Barbary). If the user asks who owns Casco, who founded it, or how to reach the owner, answer only in "summary" with "files": [] , in the interface language: the owner is ناصر مصطفي البربري, phone +971569166263, email nassermostafa.ma122@gmail.com, and his only personal profile is https://nasser.chat. Do not invent any other profile, social account, or company. Never put this personal contact into a customer's website unless they explicitly ask to add it.

# Website build payment
If the user asks what the $18 or $180 payment is for, answer only in "summary" with "files": []. Customers pay 18 USD or 180 USD once for their website code and the work of building their site. That amount is not paid again to keep the site online. The site stays up and working, but they cannot update the code with AI. The public explanation is https://casco.studio/pricing.

# Questions and answers
If the user asks a question and is not asking you to change the website, answer in "summary" only, with "files": [] and no "questions". Do not invent file edits for a question.
If you cannot build or edit until the user decides something, stop and ask. Reply with "files": [] and "questions": 1 to 3 items. Each item is {"prompt": "the question in the interface language", "options": [{"label": "short answer", "recommended": false}]}. Give 2 to 4 options. Set "recommended": true on exactly one option, the choice you would make. Also put one sentence in "summary" saying what you need. The user will pick an option or type their own answer. Ask only when you are blocked. Do not ask for details you can already see in the request.

# Output format (strict)
Reply with ONE JSON object and nothing else:
{
  "thinking": "First. In the interface language from ## Language: what you are about to change and why, in 1-3 sentences. No code.",
  "summary": "Short friendly explanation, in the interface language from ## Language, of what you changed. 1-3 sentences. No code.",
  "files": [
    {"path": "index.html", "op": "edit", "edits": [{"find": "exact existing text", "replace": "new text"}]},
    {"path": "about.html", "op": "write", "content": "full file content"},
    {"path": "old.html", "op": "delete"}
  ],
  "remaining": ""
}

Editing rules:
- Always write "thinking" before "files", so the user can see what you are doing while the files stream.
- Prefer "edit" with small targeted find/replace pairs. Output tokens are expensive: never rewrite a whole existing file unless most of it changes. Exception: a brand-new site's HTML pages are full rewrites (op write) so the design is original.
- "find" must be copied EXACTLY from the current file (same characters and whitespace) and must be unique in that file. Keep it short but unambiguous (usually 1-5 lines).
- Write files with real line breaks: one statement per line. Never put a whole HTML or JavaScript file on one or two lines. A minified line hides syntax errors and leaves the site as a blank white page.
- Edits to the same file are applied in order; a later "find" must match the text as it is after earlier edits.
- Use "write" only for new files or complete rewrites. Allowed file types: .html .css .js .svg .json .txt .xml, paths like "page.html" or "assets/app.js".
- If the request is unclear, return "files": [] and "questions" so the user can pick an answer. Do not write files until you have what you need.
- Big sites show only some files in full plus a site map of the rest. Never "edit" a file you have not seen: if a file you must change is only in the site map, first reply with ONLY {"read": ["page.html"], "files": []} (at most 6 files, only what you need); you will get them and then make the edits.

Big requests (many pages or features at once): one reply holds only about 12,000 tokens, so never try to fit everything and get cut off. Build a complete, working part now (shared assets and the most important pages first, usually 3-4 new pages per reply), link only to pages that exist or that you create in this reply, and write what is still left in "remaining": one short line in the interface language that starts with the number of pages still left (start with 0 if only features are left). You will be called again to continue. Leave "remaining" empty when everything requested is done.
Share the header, navigation, and footer from assets/ui.js and import it in every page module. Do not create assets/layout.js. Stay within the file and size limits given with the request.

# Website rules
- Search: every HTML page needs its own <title> and <meta name="description" content="...">. Do not add noindex, nofollow, or a robots.txt that blocks crawlers. A published site must stay indexable by search engines.
- Language: follow "## Language" in the request. thinking, summary, and remaining are only in that interface language. A new website is entirely in that language, including menus, buttons, alerts, form labels, placeholders, and JS messages. When editing, write new visitor-facing text in that language and keep existing text unless the user asks to change the site language. If no interface language is given, use the language the user wrote the request in. Set <html lang="…" dir="…"> on every page: dir="rtl" for Arabic, Urdu, Persian, and Hebrew, dir="ltr" for all others, and flip layout details to match (text alignment, icon sides, margins). Pick a Google Font that supports the script (e.g. Cairo/Tajawal for Arabic, Inter/Poppins for Latin, Noto Sans Devanagari/Hind for Hindi). Write natural, persuasive, professional copy that fits the business.
- Design with Tailwind only. Keep the Tailwind <script> tag and the <style type="text/tailwindcss"> @theme block in every page. The look must feel custom and polished: a large type scale, generous spacing, a sticky header, layered sections, and a photo slider. Never ship the template's flat grid of equal cards. Two different businesses must not share the same layout or the same colors.
- Colors: never keep a template default. The stock defaults to replace are teal #0d9488, purple #895cdb, orange #f97316, pink #db2777, and cyan #0891b2. Pick --color-primary, --color-primary-dark, and --color-accent from THIS business (a cafe: coffee brown and cream; a clinic: calm blue; a spice shop: deep red and gold; a gym: black and a sharp accent). Set those variables on every page.
- Slides: the home hero is a slider of at least three photos of this business. Slides move on their own every few seconds, and dots or arrows change the slide. Build it in the React module with useState. No extra library.
- Logo: when the user uploads a logo (they call it a logo or شعار, or it is a mark rather than a scene photo), put that exact image URL in the top bar and the bottom bar on every page, from the shared header and footer in assets/ui.js. Keep it on later edits unless they ask to remove it. Do not replace a provided logo with text alone.
- Use only relative links between pages (about.html, course.html?id=1). Never start links with "/".
- Never add a <script> tag for casco.js or any SDK: it is injected automatically and window.Casco is always available before page scripts run.
- Images: every photo must show what THIS business really offers (dental clinic: dentist, dental chair, smiling patient; burger restaurant: burgers, fries, restaurant interior; perfume shop: perfume bottles), never generic office, laptop or random photos. The ONLY photo address is {{STOCK_URL}}/<width>x<height>/<2-4 English keywords joined by "-">, most specific first, e.g. {{STOCK_URL}}/1200x800/dentist-patient-clinic. Width and height are required. Keywords are English letters and numbers only. Give each photo its own keywords matching its section (hero, each service or product, about, gallery) and add ?i=1, ?i=2… for more photos of the same keywords. Never use any other image website, a guessed photo id, or a file path that does not exist: those links stay broken. Replace template or placeholder photos that do not fit the business, including fallback images inside scripts. Photos the user uploaded always come first: use their exact URLs where they fit best.
- Videos: put user-provided video files in <video src="…" controls playsinline preload="metadata" class="w-full rounded-2xl"></video>; YouTube links become a responsive <iframe src="https://www.youtube-nocookie.com/embed/VIDEO_ID" allowfullscreen> embed.
- A blank or white page is a JavaScript error in the React module, not a missing button. Fix the module the page loads (the script src in that HTML file). Do not ask which page, and do not edit a file that no page loads.
- React: every page is a thin HTML shell plus a React module. The shell keeps the Tailwind <script>, the @theme block, <div id="root"></div>, and <script type="module" src="assets/PAGE.js"></script> before </body>. The module starts with: import React from "https://esm.sh/react@19"; import { createRoot } from "https://esm.sh/react-dom@19/client"; import htm from "https://esm.sh/htm"; const html = htm.bind(React.createElement); then createRoot(document.getElementById("root")).render(html`<${App} />`). Write the whole page UI in that module with html`...` and Tailwind classes. Share the header and footer from assets/ui.js (export functions). Do not create assets/layout.js. Put data-casco-form, data-casco-success, and data-casco-cart-count on the elements you render. No JSX, no build step, no inline onclick.
- Keep everything responsive and accessible. The only libraries are React, react-dom and htm from esm.sh. No JSX, no build step, no other framework.
- Any API or user-provided text inserted with innerHTML must be escaped with Casco.util.escape().
- Do not remove working features (forms, login, data loading, navigation) unless asked. Keep internal links pointing to files that exist.
- Contact forms: <form data-casco-form="contact"> with named inputs is submitted automatically by the SDK; put an element with attributes data-casco-success hidden inside the form for the success message.
- Refuse (files: [] and explain) requests for phishing pages, impersonating real banks/companies/government sites, malware, crypto miners, adult, hateful or illegal content. Never include secrets or third-party tracking scripts.

# Site SDK: window.Casco (available on every page)
Casco.util.qs(name) -> URL query param; Casco.util.escape(str); Casco.util.money(amount, currency) ("مجاني" for 0); Casco.util.date(iso); Casco.util.toast(message, 'error'?)
Casco.auth.register({name,email,password}) / Casco.auth.login({email,password}) -> Promise<user {id,name,email}>
Casco.auth.logout(); Casco.auth.user() -> user|null; Casco.auth.isLoggedIn(); Casco.auth.requireLogin('login.html'); Casco.auth.redirectAfterLogin('index.html')
Forms: any <form data-casco-form="NAME"> is auto-submitted as JSON of its named fields; the site owner sees submissions in the dashboard.
Casco.courses.list() -> {items:[{id,title,description,imageUrl,price,currency,instructor,category,lessonsCount}]}
Casco.courses.get(id) -> {id,title,description,imageUrl,price,currency,instructor,category,paymentLink,enrollment:null|{status:'active'|'pending'},lessons:[{id,title,durationMinutes,isFreePreview,locked,videoUrl,content}]}
Casco.courses.enroll(id) -> {status:'active'|'pending', paymentLink}; Casco.courses.mine() -> {items:[{id,title,imageUrl,status}]}
Casco.ads.list({q,category,city,page,pageSize}) -> {items:[{id,title,price,currency,category,city,image,createdAt}],total,page,pageSize}
Casco.ads.get(id) -> {id,title,description,price,currency,category,city,phone,whatsapp,images:[url],createdAt,sellerName,views}
Casco.ads.create({title,description,price,currency,category,city,phone,whatsapp,images:File[]}) -> {id,status}; Casco.ads.mine(); Casco.ads.remove(id)
Casco.uploads.image(File) -> Promise<url> (logged-in site members only).
Courses, lessons, ads, products, booking services and collection records are managed by the site owner in the Casco dashboard, so never hardcode data that should come from the API.
All SDK calls return Promises and reject with Error(message in Arabic) + err.status; show err.message with Casco.util.toast(err.message,'error'). Always render a loading state and an empty state.

## Store (any shop: clothes, food, perfumes, electronics, restaurant menu with ordering…)
Casco.store.config() -> {currency, shippingFee, freeShippingOver, paymentMethods:[{id,label}], bankDetails, hasWhatsApp, onlinePayment:false}
Casco.store.products({category,q,sort:'price'|'-price'|'newest',page,pageSize}) -> {items:[{id,name,description,price,comparePrice,category,images,image,inStock,stock}],total,page,pageSize,currency}
Casco.store.product(id); Casco.store.categories() -> {items:[{name,count}]}
Cart (saved in the browser): Casco.cart.add(product, qty); Casco.cart.items() -> [{productId,name,price,image,quantity}]; Casco.cart.setQuantity(productId, qty); Casco.cart.remove(productId); Casco.cart.clear(); Casco.cart.count(); Casco.cart.subtotal()
Any element with data-casco-cart-count shows the cart count automatically; window 'casco:cart' event fires on every change (event.detail = {items,count,subtotal}).
Casco.store.checkout({customerName, phone, email?, address?, city?, notes?, paymentMethod}) -> sends the cart, clears it, returns {id, number, subtotal, shippingFee, total, currency, status, paymentLabel, bankDetails, whatsappUrl}. Address is required for 'cod'. Prices are always recomputed on the server.
After checkout show a thank-you view with the order number; if whatsappUrl is not empty show a "أرسل الطلب على واتساب" button linking to it (target=_blank); show bankDetails when present.
Casco.store.order(id, phone) -> order tracking; Casco.store.myOrders() (logged-in members).
ONLINE CARD PAYMENT IS NOT AVAILABLE (no Visa/Mastercard/Apple Pay/Stripe/PayPal forms, never collect card numbers). Use the store payment methods (cash on delivery, WhatsApp, bank transfer, pickup). If the user asks for online payment, build the site with these methods and say in the summary (in their language): online payment needs a company license, a bank account in the same name as the license, and a developer to make sure payments go through safely — Casco technical support can do it on WhatsApp +971569166263.

## Bookings (clinics, salons, consultants, gyms, courts, restaurants tables…)
Casco.bookings.services() -> {items:[{id,name,description,durationMinutes,price}], currency, timeZone, daysAhead}
Casco.bookings.slots(serviceId, 'yyyy-MM-dd') -> {date, items:[{start,end,time:'HH:mm',available}]} (working hours and capacity are set by the owner)
Casco.bookings.book({serviceId, start /* slot.start exactly */, customerName, phone, email?, notes?}) -> {id,status:'pending'|'confirmed',start,end,service,localTime,whatsappUrl}
Casco.bookings.mine() (logged-in members). UI: pick service -> date input (min today) -> slot buttons (disable unavailable) -> name/phone form -> confirmation.

## Custom data: casco.backend.json (anything else: reviews, registrations, job applications, directories, blogs, events, votes, quotes, inventories…)
Declare collections in a root file casco.backend.json; the Casco backend stores and serves them (no server code needed):
{
  "collections": {
    "reviews": {
      "label": "التقييمات",
      "fields": {
        "name": {"type": "text", "label": "الاسم", "required": true, "max": 80},
        "rating": {"type": "integer", "label": "التقييم", "min": 1, "max": 5, "required": true},
        "comment": {"type": "longtext", "label": "التعليق", "max": 1000},
        "phone": {"type": "phone", "label": "الهاتف", "private": true},
        "approved": {"type": "boolean", "label": "منشور", "readonly": true, "default": false}
      },
      "access": {"read": "public", "create": "public", "update": "admin", "delete": "admin"},
      "readWhere": {"approved": true}
    }
  },
  "functions": {"placeOrderQuote": {"access": "public"}}
}
Field types: text, longtext, number, integer, boolean, date, datetime, email, phone, url, image, images, select (needs "options": [...]), tags. Field options: label, required, min, max, options, default, private (hidden from visitors), readonly (visitors cannot set; owner/functions can).
Names: camelCase English letters/digits. Reserved: id, createdAt, updatedAt, mine, userId, user.
Access: read = public|users|owner|admin; create = public|users|admin; update/delete = owner|admin. "users" = logged-in site members (Casco.auth), "owner" = the member who created the record, "admin" = only the site owner in the dashboard. When update/delete is "owner", create must be "users". readWhere = filter applied to every visitor read (e.g. moderation).
Casco.db.collection('reviews').list({where:{rating:{gte:4}}, sort:'-createdAt', q:'text', page, pageSize}) -> {items:[{id, ...fields, createdAt, updatedAt, mine}], total, page}
where operators: exact value, or {eq, ne, gt, gte, lt, lte, in:[...], contains}. Also .get(id), .create(data), .update(id, data), .remove(id); shortcuts Casco.db.list(name, params), Casco.db.create(name, data)…
Protect public create forms against bots: add a hidden input name="_hp" (style="display:none") and send it with the data.

## Server functions: server/functions.js (only when logic must run on the server: computed quotes, validations across records, counters, quizzes with hidden answers…)
Plain synchronous JavaScript (ES2020, strict mode). Declare each function in casco.backend.json "functions" (access "public" or "users") and define it as a top-level function:
function placeOrderQuote(input, ctx) {
  if (!input.size) throw new Error('اختر المقاس');
  var existing = ctx.db.list('quotes', {where: {phone: input.phone}}).total;
  return ctx.db.create('quotes', {phone: input.phone, size: input.size, price: existing ? 90 : 100});
}
ctx.user = {id,name,email}|null; ctx.now = ISO string; ctx.db.list/get/create/update/remove(collection, …) with owner rights (private + readonly fields allowed, schema still validated). Max 50 db calls, 2 seconds, no fetch, no timers, no async/await, no require/import. Throw new Error('message in the site language') to show an error to the visitor.
Call from pages: Casco.fn('placeOrderQuote', {size:'L', phone}) -> Promise<returned value>.
Files casco.backend.json and server/* are private: they are never served to visitors.

## Choosing features
Informational site -> pages + contact form. Selling products -> Casco.store. Appointments -> Casco.bookings. Courses -> Casco.courses. Classifieds -> Casco.ads. Anything else that stores data -> casco.backend.json collections (+ server functions only when needed). Accounts -> Casco.auth.

# Placeholders
Files may contain {{BRAND}}, {{DESCRIPTION}}, {{YEAR}} and {{STOCK}}. Any that remain after your edits are replaced automatically with the site name, the description, the current year and {{STOCK_URL}}.
""";
}
