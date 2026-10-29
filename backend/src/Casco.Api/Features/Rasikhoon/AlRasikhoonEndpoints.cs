using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casco.Api.Features.Ai;
using Casco.Api.Infrastructure;

namespace Casco.Api.Features.Rasikhoon;

/// <summary>
/// Public Ask AI for Al Rasikhoon Real Estate (الراسخون العقارية).
/// Streams dummy thinking word-by-word, then the answer. Apartment matching uses seeded inventory.
/// </summary>
public static class AlRasikhoonEndpoints
{
    public const string ProjectId = "al-rasikhoon";

    const string SystemPrompt = """
        أنت KPH، مساعد Ask AI الرسمي لشركة الراسخون العقارية (Al Rasikhoon Real Estate)، وتعمل عبر باك إند Casco مع نماذج OpenAI.

        أسلوب الرد (مهم جداً):
        - تكلم بطبيعية كموظف خدمة عملاء ذكي، مش روبوت يكرر نفس الفقرة.
        - جاوب على سؤال المستخدم مباشرة أولاً، ثم أضف معلومة مفيدة قصيرة فقط إذا لزمت.
        - ممنوع تكرار نفس مقدمة الشركة في كل رد.
        - رد بنفس لغة السؤال (عربي فصيح واضح أو إنجليزي بسيط).
        - لو السؤال خارج نطاق الشركة أو الموقع، اعتذر بلطف وارجع لموضوع الراسخون أو برج الوطن.

        من برمج هذا الموقع؟
        - المطوّر: ناصر مصطفى (Nasser Mostafa).
        - بنى الموقع ووكيل KPH كعرض قدرات لفرصة عمل في الراسخون، ودمجه عبر Casco.
        - لو سألو عن المبرمج / مطور الموقع / من عمل الموقع: اذكر ناصر مصطفى بوضوح.

        معلومات الشركة:
        - تأسست 10 يونيو 2002، المقر: شارع الشيخ خليفة بن زايد، النعيمية 1، عجمان
        - هاتف مجاني: 8005454 / +971 800 5454 · واتساب: https://wa.me/9718005454
        - إيميل: info@alrasikhoon.ae · وظائف: careers@alrasikhoon.ae
        - تقييم 5+ نجوم من دائرة الأراضي والتنظيم العقاري بعجمان
        - رخصة 51148 · قيد 19 · تصريح AJRE2210
        - السوشيال: Instagram @alrasikhoon.ae · Facebook alrasikhoon.ae · X @alrasikhoon · YouTube · LinkedIn · TikTok @alrasikhoon.ae · Telegram t.me/alrasikhhon · Threads · Linktree linktr.ee/alrasikhoon

        قيادة معروفة:
        - خليفة سلطان بن حارب المهيري: المالك والمدير العام
        - د. كريم أبو شليح: مدير المبيعات
        - حسام عوض: مدير المالية والإدارة
        - محمد الفكي: أخصائي سوشيال ميديا
        - مهندس محمد عادل: مسؤول التوظيف

        مشاريع: بوابة القاسمية، روضة السدر، بساتين السرة، الصجعة الصناعية، الحليو، قرية المها، الباهية، برج الوطن على كورنيش عجمان.

        برج الوطن: تملك حر، نحو 48 طابقاً (39 سكني + مواقف + مرافق)، استوديو من ~460 ألف درهم، 1BR من ~838 ألف، 2BR من ~1.35 مليون، 3BR من ~2.25 مليون، تسليم معلن Q4 2028 تقريباً. مطور KBH مع تسويق الراسخون.

        لا تختلق موظفين أو أسعار خارج ما سبق. وجّه التواصل لـ 8005454 أو واتساب عند الحاجة للحجز أو البيانات الرسمية.
        """;

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static readonly AptSeed[] Inventory =
    [
        new("F12-U03", 12, "1203", "studio", 0, 575, 460_000, true, Img0, Img2),
        new("F14-U01", 14, "1401", "studio", 0, 615, 518_000, true, Img1, Img0),
        new("F15-U07", 15, "1507", "studio", 0, 590, 485_000, true, Img0, Img1),
        new("F18-U02", 18, "1802", "1br", 1, 1369, 838_000, true, Img0, Img1),
        new("F20-U05", 20, "2005", "1br", 1, 1180, 796_000, true, Img2, Img0),
        new("F22-U04", 22, "2204", "1br", 1, 1350, 910_000, true, Img1, Img2),
        new("F25-U01", 25, "2501", "2br", 2, 1772, 1_350_000, true, Img1, Img0),
        new("F28-U03", 28, "2803", "2br", 2, 1819, 1_538_000, true, Img0, Img1),
        new("F30-U06", 30, "3006", "2br", 2, 1690, 1_420_000, true, Img2, Img1),
        new("F35-U02", 35, "3502", "3br", 3, 2253, 2_253_000, true, Img1, Img2),
        new("F38-U01", 38, "3801", "3br", 3, 2400, 2_480_000, true, Img0, Img1),
        new("F10-U08", 10, "1008", "studio", 0, 575, 445_000, true, Img2, Img0),
        new("F16-U04", 16, "1604", "1br", 1, 1250, 860_000, false, Img0, Img2),
        new("F32-U05", 32, "3205", "2br", 2, 1900, 1_680_000, true, Img1, Img0),
    ];

    const string Img0 = "https://uae-offplan.com/admin.uae-offplan/uploads/new_property/al-watan-tower/d001ac4e367c6b0e4b5bbdec6f2649cc.jpg";
    const string Img1 = "https://alzorahprop.com/wp-content/uploads/2025/12/IMG-20251208-WA0329.jpg";
    const string Img2 = "https://dashboard.alrasikhoon.ae/assets/images/banners/resized_w/001.webp";

    public static void MapAlRasikhoon(this WebApplication app)
    {
        var api = app.MapGroup("/api/rasikhoon").RequireCors("sites").DisableAntiforgery().RequireRateLimiting("site");
        api.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            projectId = ProjectId,
            company = "Al Rasikhoon Real Estate",
        }));
        api.MapPost("/ask", AskAsync);
        api.MapPost("/ask/stream", AskStreamAsync);
    }

    static async Task<IResult> AskAsync(AskBody? body, AiClient ai, CancellationToken ct)
    {
        var message = (body?.Message ?? "").Trim();
        if (!Validate(body, message, out var error, out var lang))
            return error!;

        var packed = await BuildReplyAsync(ai, message, lang, ct);
        return Results.Ok(new
        {
            reply = packed.Answer,
            thinking = packed.Thinking,
            apartments = packed.Apartments,
            source = "rasikhoon",
            projectId = ProjectId,
        });
    }

    static async Task AskStreamAsync(HttpContext http, AiClient ai, CancellationToken ct)
    {
        AskBody? body;
        try { body = await http.Request.ReadFromJsonAsync<AskBody>(JsonOpts, ct); }
        catch
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "Invalid JSON body" }, ct);
            return;
        }

        var message = (body?.Message ?? "").Trim();
        var projectId = (body?.ProjectId ?? "").Trim();
        var lang = NormalizeLang(body?.Lang, message);
        if (!string.IsNullOrEmpty(projectId) && !string.Equals(projectId, ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "Project mismatch", projectId = ProjectId }, ct);
            return;
        }
        if (string.IsNullOrEmpty(message))
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "message is required" }, ct);
            return;
        }
        if (message.Length > 2000)
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "message is too long" }, ct);
            return;
        }

        http.Response.Headers.ContentType = "text/event-stream; charset=utf-8";
        http.Response.Headers.CacheControl = "no-cache, no-transform";
        http.Response.Headers.Append("Connection", "keep-alive");
        await http.Response.StartAsync(ct);

        await WriteSse(http, new { meta = true, source = "rasikhoon", projectId = ProjectId }, ct);

        try
        {
            var packed = await BuildReplyAsync(ai, message, lang, ct);

            foreach (var token in StringInfoChars(packed.Thinking))
            {
                ct.ThrowIfCancellationRequested();
                await WriteSse(http, new { thinkingToken = token }, ct);
                await Task.Delay(18, ct);
            }

            await WriteSse(http, new { thinkingDone = true }, ct);
            await Task.Delay(120, ct);

            foreach (var token in StringInfoChars(packed.Answer))
            {
                ct.ThrowIfCancellationRequested();
                await WriteSse(http, new { token }, ct);
                await Task.Delay(12, ct);
            }

            if (packed.Apartments.Count > 0)
                await WriteSse(http, new { apartments = packed.Apartments }, ct);

            await WriteSse(http, new { done = true, source = "rasikhoon" }, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await WriteSse(http, new { error = Text.Truncate(ex.Message, 200) }, ct);
        }
    }

    static bool Validate(AskBody? body, string message, out IResult? error, out string lang)
    {
        error = null;
        lang = NormalizeLang(body?.Lang, message);
        var projectId = (body?.ProjectId ?? "").Trim();
        if (!string.IsNullOrEmpty(projectId) && !string.Equals(projectId, ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            error = Results.BadRequest(new { error = "Project mismatch", projectId = ProjectId });
            return false;
        }
        if (string.IsNullOrEmpty(message))
        {
            error = Results.BadRequest(new { error = "message is required" });
            return false;
        }
        if (message.Length > 2000)
        {
            error = Results.BadRequest(new { error = "message is too long" });
            return false;
        }
        return true;
    }

    static async Task<PackedReply> BuildReplyAsync(AiClient ai, string message, string lang, CancellationToken ct)
    {
        var intent = DetectIntent(message);
        var matches = MatchApartments(intent);

        if (intent.IsApartmentQuery)
        {
            var thinking = BuildApartmentThinking(message, intent, lang);
            var answer = BuildApartmentAnswer(intent, matches, lang);
            var cards = matches.Take(4).Select(ToCard).ToList();
            return new PackedReply(thinking, answer, cards);
        }

        try
        {
            var result = await ai.CompleteAsync(
                AiTiers.Cheap,
                new ChatRequest(
                    [
                        new("system", SystemPrompt + """

                            صيغة الإخراج الإلزامية — JSON فقط بدون markdown:
                            {"thinking":"...","answer":"..."}
                            - thinking: 2 إلى 4 جمل قصيرة توضّح كيف تفهم سؤال الزائر الحالي تحديداً (اذكر موضوع سؤاله)، ثم ماذا ستراجع قبل الرد. ممنوع قالب عام ثابت، وممنوع نسخ نفس التفكير في كل مرة.
                            - answer: الرد النهائي للزائر بنفس لغة سؤاله، طبيعي ومباشر.
                            """),
                        new("user", message),
                    ],
                    MaxOutputTokens: 900,
                    Json: true),
                new AiCallContext(null, null, null, "rasikhoon-ask", "public"),
                ct);

            if (TryParseAiPacket(result.Content, lang, out var thinking, out var answer))
                return new PackedReply(thinking, answer, []);

            var fallbackAnswer = (result.Content ?? "").Trim();
            if (string.IsNullOrEmpty(fallbackAnswer))
                fallbackAnswer = FallbackCompany(message, lang);
            return new PackedReply(BuildTopicThinking(message, lang), fallbackAnswer, []);
        }
        catch
        {
            return new PackedReply(BuildTopicThinking(message, lang), FallbackCompany(message, lang), []);
        }
    }

    static bool TryParseAiPacket(string? content, string lang, out string thinking, out string answer)
    {
        thinking = "";
        answer = "";
        if (string.IsNullOrWhiteSpace(content)) return false;
        var raw = content.Trim();
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return false;
        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;
            thinking = root.TryGetProperty("thinking", out var t) ? (t.GetString() ?? "").Trim() : "";
            answer = root.TryGetProperty("answer", out var a) ? (a.GetString() ?? "").Trim() : "";
            if (string.IsNullOrEmpty(answer)) return false;
            if (string.IsNullOrEmpty(thinking))
                thinking = lang == "en"
                    ? "Understanding this specific visitor question, then drafting a direct reply."
                    : "أفهم سؤال الزائر الحالي تحديداً، ثم أصيغ رداً مباشراً.";
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string Snip(string message, int max = 90)
    {
        var one = Regex.Replace(message.Trim(), @"\s+", " ");
        if (one.Length <= max) return one;
        return one[..(max - 1)] + "…";
    }

    static string BuildApartmentThinking(string message, Intent intent, string lang)
    {
        if (lang == "en")
        {
            var lines = new List<string>
            {
                $"Visitor asked: \"{Snip(message)}\".",
                "This looks like an Al Watan Tower unit search.",
            };
            if (intent.Budget is not null)
                lines.Add($"Budget cue around AED {intent.Budget:N0}.");
            if (intent.Type is not null)
                lines.Add($"Unit type cue: {intent.Type}.");
            if (intent.Floor is not null)
                lines.Add($"Floor cue: {intent.Floor}.");
            lines.Add("Matching demo inventory and preparing unit cards.");
            return string.Join(" ", lines);
        }

        var ar = new List<string>
        {
            $"الزائر سأل: \"{Snip(message)}\".",
            "يبدو بحث وحدة في برج الوطن.",
        };
        if (intent.Budget is not null)
            ar.Add($"إشارة ميزانية حوالي {intent.Budget:N0} درهم.");
        if (intent.Type is not null)
            ar.Add($"نوع الوحدة: {TypeAr(intent.Type)}.");
        if (intent.Floor is not null)
            ar.Add($"تفضيل الطابق: {intent.Floor}.");
        ar.Add("أطابق المخزون التجريبي وأجهّز بطاقات الوحدات.");
        return string.Join(" ", ar);
    }

    static string BuildTopicThinking(string message, string lang)
    {
        var lower = message.ToLowerInvariant();
        var topicEn = "general company enquiry";
        var topicAr = "استفسار عام عن الشركة";
        if (Regex.IsMatch(lower, @"مبرمج|مطور|مطوّر|programmer|developer|who (built|made|coded)|من (عمل|صنع|برمج)|مين (عمل|برمج)"))
        {
            topicEn = "who built / programmed this website";
            topicAr = "من برمج أو طوّر هذا الموقع";
        }
        else if (Regex.IsMatch(lower, @"تفهم عربي|بتفهم عربي|understand arabic|speak arabic|عربي ولا", RegexOptions.IgnoreCase))
        {
            topicEn = "whether the assistant understands Arabic";
            topicAr = "هل المساعد يفهم العربية";
        }
        else if (Regex.IsMatch(lower, @"تواصل|contact|هاتف|phone|واتس|whatsapp|ايميل|email", RegexOptions.IgnoreCase))
        {
            topicEn = "company contact channels";
            topicAr = "وسائل التواصل مع الشركة";
        }
        else if (Regex.IsMatch(lower, @"موظف|team|adel|عادل|كريم|حسام|خليفة|توظيف|recruit", RegexOptions.IgnoreCase))
        {
            topicEn = "team / recruitment people";
            topicAr = "الفريق أو التوظيف";
        }
        else if (Regex.IsMatch(lower, @"وطن|watan|tower|برج", RegexOptions.IgnoreCase))
        {
            topicEn = "Al Watan Tower details";
            topicAr = "تفاصيل برج الوطن";
        }
        else if (Regex.IsMatch(lower, @"مشروع|project|قاسمية|سدر|صجعة", RegexOptions.IgnoreCase))
        {
            topicEn = "Al Rasikhoon projects";
            topicAr = "مشاريع الراسخون";
        }

        return lang == "en"
            ? $"Visitor asked: \"{Snip(message)}\". Topic: {topicEn}. Pull only the facts needed for this question and answer directly."
            : $"الزائر سأل: \"{Snip(message)}\". الموضوع: {topicAr}. أراجع فقط ما يلزم لهذا السؤال وأرد مباشرة.";
    }

    static string BuildApartmentAnswer(Intent intent, List<AptSeed> matches, string lang)
    {
        if (matches.Count == 0)
        {
            return lang == "en"
                ? "I couldn’t find an available unit matching that budget/type in the demo inventory. Try raising the budget or changing the type — or WhatsApp 8005454 for live availability."
                : "ما لقيت وحدة متاحة مطابقة لهذه الميزانية/النوع في المخزون التجريبي. جرّب رفع الميزانية أو تغيير النوع، أو واتساب 8005454 للتوفر الفعلي.";
        }

        if (lang == "en")
        {
            var sb = new StringBuilder();
            sb.AppendLine("Here are available Al Watan Tower options from the demo inventory:");
            foreach (var m in matches.Take(4))
            {
                sb.AppendLine($"• Apt {m.Unit} · Floor {m.Floor} · {TypeEn(m.Type)} · AED {m.PriceAed:N0} · {m.SizeSqft} sqft");
            }
            sb.Append("Photos are attached below. For booking call/WhatsApp 8005454.");
            return sb.ToString();
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("هذه وحدات متاحة من برج الوطن (مخزون تجريبي):");
            foreach (var m in matches.Take(4))
            {
                sb.AppendLine($"• شقة {m.Unit} · طابق {m.Floor} · {TypeAr(m.Type)} · {m.PriceAed:N0} درهم · {m.SizeSqft} قدم²");
            }
            sb.Append("الصور مرفقة بالأسفل. للحجز اتصل/واتساب 8005454.");
            return sb.ToString();
        }
    }

    static List<AptSeed> MatchApartments(Intent intent)
    {
        IEnumerable<AptSeed> q = Inventory.Where(a => a.Available);
        if (intent.Type is not null)
            q = q.Where(a => a.Type == intent.Type);
        if (intent.Floor is not null)
            q = q.Where(a => a.Floor == intent.Floor || Math.Abs(a.Floor - intent.Floor.Value) <= 2);
        if (intent.Budget is not null)
            q = q.Where(a => a.PriceAed <= intent.Budget.Value * 1.05);

        var list = q.OrderBy(a => a.PriceAed).ToList();
        if (list.Count == 0 && intent.IsApartmentQuery)
        {
            list = Inventory.Where(a => a.Available)
                .Where(a => intent.Type is null || a.Type == intent.Type)
                .OrderBy(a => a.PriceAed)
                .Take(3)
                .ToList();
        }
        return list;
    }

    static Intent DetectIntent(string message)
    {
        var lower = message.ToLowerInvariant();
        var isApt =
            Regex.IsMatch(lower, @"studio|استوديو|استوديو|شقه|شقة|طابق|غرفة|غرفتين|bedroom|apartment|unit|floor|ميزاني|budget|درهم|aed")
            || Regex.IsMatch(lower, @"\d{3,}");

        string? type = null;
        if (Regex.IsMatch(lower, @"studio|استوديو|استوديو")) type = "studio";
        else if (Regex.IsMatch(lower, @"3\s*b|ثلاث|ثلاثة غرف|3br")) type = "3br";
        else if (Regex.IsMatch(lower, @"2\s*b|غرفتين|غرفتين|2br")) type = "2br";
        else if (Regex.IsMatch(lower, @"1\s*b|غرفة وصالة|غرفه وصاله|one bedroom|1br")) type = "1br";

        int? floor = null;
        var floorMatch = Regex.Match(message, @"(?:floor|طابق)\s*(\d{1,2})", RegexOptions.IgnoreCase);
        if (floorMatch.Success && int.TryParse(floorMatch.Groups[1].Value, out var f)) floor = f;
        else
        {
            var f2 = Regex.Match(message, @"(\d{1,2})\s*(?:طابق|floor)", RegexOptions.IgnoreCase);
            if (f2.Success && int.TryParse(f2.Groups[1].Value, out var fVal)) floor = fVal;
        }

        long? budget = ParseBudget(message);
        if (budget is null && type is null && floor is null && !Regex.IsMatch(lower, @"ميزاني|budget|سعر|price|under|اقل|أقل|within"))
            isApt = isApt && Regex.IsMatch(lower, @"studio|استوديو|شقه|شقة|apartment|غرفة|غرفتين|bedroom");
        return new Intent(isApt, type, floor, budget);
    }

    static long? ParseBudget(string message)
    {
        var cleaned = message.Replace(",", "").Replace("،", "");
        var hasBudgetWord = Regex.IsMatch(cleaned, @"ميزاني|budget|سعر|price|under|اقل|أقل|within|درهم|aed", RegexOptions.IgnoreCase);

        // Prefer explicit multipliers: 500k / 500 ألف / 1.5 million
        var m = Regex.Match(cleaned, @"(\d+(?:\.\d+)?)\s*(k|ألف|الف|مليون|million|m)\b", RegexOptions.IgnoreCase);
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scaled))
        {
            var unit = m.Groups[2].Value.ToLowerInvariant();
            if (unit is "k" or "ألف" or "الف") scaled *= 1_000;
            else scaled *= 1_000_000;
            return scaled is >= 100_000 and <= 20_000_000 ? (long)scaled : null;
        }

        if (!hasBudgetWord) return null;

        var plain = Regex.Match(cleaned, @"(\d{3,8})");
        if (!plain.Success) return null;
        if (!double.TryParse(plain.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
        if (n < 10_000) n *= 1_000; // "500" with budget word → 500k
        if (n is < 100_000 or > 20_000_000) return null;
        return (long)n;
    }

    static string NormalizeLang(string? requested, string message)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var r = requested.Trim().ToLowerInvariant();
            if (r.StartsWith("en")) return "en";
            if (r.StartsWith("ar")) return "ar";
        }
        return Regex.IsMatch(message, "[\u0600-\u06FF]") ? "ar" : "en";
    }

    static string FallbackCompany(string message, string lang)
    {
        var lower = message.ToLowerInvariant();
        if (Regex.IsMatch(lower, @"مبرمج|مطور|مطوّر|programmer|developer|who (built|made|coded|developed)|من (عمل|صنع|برمج|بنى|بني)|مين (عمل|برمج)"))
        {
            return lang == "en"
                ? "This site was built by Nasser Mostafa as a capability demo for Al Rasikhoon, with the KPH agent wired through Casco."
                : "مبرمج الموقع هو ناصر مصطفى. عمله كعرض قدرات للراسخون مع وكيل KPH عبر Casco.";
        }
        if (Regex.IsMatch(lower, @"تفهم عربي|بتفهم عربي|Understand arabic|speak arabic|عربي ولا", RegexOptions.IgnoreCase))
        {
            return lang == "en"
                ? "Yes — I understand Arabic and English. Ask anything about Al Rasikhoon or Al Watan Tower."
                : "أيوه، بفهم عربي وإنجليزي. اسأل عن الراسخون أو برج الوطن وأرد بنفس لغة سؤالك.";
        }
        if (Regex.IsMatch(lower, @"تواصل|contact|هاتف|phone|واتس|whatsapp|ايميل|email", RegexOptions.IgnoreCase))
        {
            return lang == "en"
                ? "Call 8005454 · WhatsApp https://wa.me/9718005454 · info@alrasikhoon.ae · Khalifa Bin Zayed St, Al Nuaimia 1, Ajman."
                : "تواصل: 8005454 · واتساب https://wa.me/9718005454 · info@alrasikhoon.ae · النعيمية 1، عجمان.";
        }
        return lang == "en"
            ? "Tell me what you need: Al Watan prices, a project, contacts, the team, or who built this site — and I’ll answer directly."
            : "قولي بالتحديد: أسعار برج الوطن، مشروع معيّن، تواصل، الفريق، أو مين برمج الموقع — وأجاوبك مباشرة.";
    }

    static string TypeAr(string type) => type switch
    {
        "studio" => "استوديو",
        "1br" => "غرفة وصالة",
        "2br" => "غرفتين وصالة",
        "3br" => "ثلاث غرف",
        _ => type,
    };

    static string TypeEn(string type) => type switch
    {
        "studio" => "Studio",
        "1br" => "1 Bedroom",
        "2br" => "2 Bedrooms",
        "3br" => "3 Bedrooms",
        _ => type,
    };

    static object ToCard(AptSeed a) => new
    {
        id = a.Id,
        floor = a.Floor,
        unit = a.Unit,
        type = a.Type,
        priceAed = a.PriceAed,
        sizeSqft = a.SizeSqft,
        available = a.Available,
        images = new[] { a.Image1, a.Image2 },
    };

    static async Task WriteSse(HttpContext http, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await http.Response.Body.WriteAsync(bytes, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    static IEnumerable<string> StringInfoChars(string text)
    {
        var enumr = StringInfo.GetTextElementEnumerator(text);
        while (enumr.MoveNext())
            yield return enumr.GetTextElement();
    }

    public record AskBody(string? Message, string? ProjectId, string? Lang);
    record Intent(bool IsApartmentQuery, string? Type, int? Floor, long? Budget);
    record PackedReply(string Thinking, string Answer, List<object> Apartments);
    record AptSeed(string Id, int Floor, string Unit, string Type, int Beds, int SizeSqft, long PriceAed, bool Available, string Image1, string Image2);
}
