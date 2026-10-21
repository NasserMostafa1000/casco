using System.Text;
using System.Text.Json;
using Casco.Api.Features.Ai;
using Casco.Api.Infrastructure;

namespace Casco.Api.Features.Ardh;

/// <summary>
/// Public Ask AI for Ard Al Musk & Al Bukhour (ehabalobaidi.com).
/// Answers only about that company, via Casco's real AI providers.
/// </summary>
public static class ArdhAlMuskEndpoints
{
    public const string ProjectId = "ardh-al-musk-bukhour";

    const string SystemPrompt = """
        أنت مساعد Ask AI الرسمي من Casco لمشروع ardh-al-musk-bukhour فقط — شركة أرض المسك والبخور (Ard Al Musk & Al Bukhour Company).
        تعمل عبر باك إند Casco على api.casco.studio، والدومين المرتبط هو ehabalobaidi.com.

        أسلوب الرد:
        - تكلم بطبيعية كأنك موظف من المحل، مش روبوت.
        - جمل قصيرة وواضحة.
        - لو السؤال بالعربي رد بالعربي الواضح. لو بالإنجليزي رد بالإنجليزي البسيط.
        - لا تستخدم عبارات آلية مثل "بصفتي مساعد ذكاء اصطناعي".

        حدود صارمة:
        - جاوب فقط عن هذه الشركة ومنتجاتها وعناوينها وأرقامها.
        - ممنوع ذكر شركات ثانية أو خلط معلومات معها.
        - لو السؤال برة نشاط الشركة، اعتذر بلطف وارجع لموضوع الشركة.

        معلومات الشركة:
        - الاسم: شركة أرض المسك والبخور (Ard Al Musk & Al Bukhour Company)
        - النشاط: تجارة عامة — عطارية · بخور · غذائية · عطور
        - المكان: البصرة، العراق
        - العنوان: الأشار - الدكير قرب كراج القرنة
        - المسؤول: Ehab Al-Obaidi
        - واتساب/عراق: 009647801322505 و 009647714939950
        - هاتف دبي: 00971543722250
        - الإيميل: ehabbookr@gmail.com
        - الدومين: ehabalobaidi.com
        - إنستغرام: https://instagram.com/ardh_almsk
        - تيليجرام: https://t.me/+ZjvoNBVJ8HFiYWEy
        - تيك توك: https://www.tiktok.com/@ardh_almsk

        منتجات متوفرة ضمن الدليل:
        - بخور وأعواد: بنجواتي دوب، بنجواتي ٧ في ١، ليبرتي فيجي، بخور حامدي، ٧ أسود أفريقية، جيم، مجموعة ليبرتي، شاشي لافندر، شاشي روزه، مجموعة ميمبو، باف طارد البعوض
        - عطور الشركة: غسق، سحر الأنوثة
        - زيوت: فيتامين E، فيتامين A، زيت الحية، باجاج روزيه ٥٥٥
        - حناء: أم خماس، بنت الملوك، لميس
        - بهارات: زنجبيل، هيل أخضر، بهارات الطباخ العراقي
        - غذائية: شاي ماتشا، كاكاو

        لو العميل حب يطلب، وجّهه لواتساب 009647801322505 وقل له يكتب اسم الصنف والكمية.
        """;

    static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void MapArdhAlMusk(this WebApplication app)
    {
        var api = app.MapGroup("/api/ardh").RequireCors("sites").DisableAntiforgery().RequireRateLimiting("site");
        api.MapGet("/health", () => Results.Ok(new
        {
            ok = true,
            projectId = ProjectId,
            engine = "casco",
            domain = "ehabalobaidi.com",
            api = "https://api.casco.studio",
        }));
        api.MapPost("/ask", AskAsync);
        api.MapPost("/ask/stream", AskStreamAsync);
    }

    static async Task<IResult> AskAsync(AskBody? body, AiClient ai, CancellationToken ct)
    {
        var message = (body?.Message ?? "").Trim();
        var projectId = (body?.ProjectId ?? "").Trim();
        if (!string.IsNullOrEmpty(projectId) && !string.Equals(projectId, ProjectId, StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Project mismatch", projectId = ProjectId });
        if (string.IsNullOrEmpty(message))
            return Results.BadRequest(new { error = "message is required" });
        if (message.Length > 2000)
            return Results.BadRequest(new { error = "message is too long" });

        try
        {
            var reply = await GenerateAsync(ai, message, ct);
            return Results.Ok(new { reply, source = "casco-upstream", engine = "casco", projectId = ProjectId });
        }
        catch (Exception ex)
        {
            return Results.Json(new { error = "Ask AI failed", detail = Text.Truncate(ex.Message, 200) }, statusCode: 500);
        }
    }

    static async Task AskStreamAsync(HttpContext http, AiClient ai, CancellationToken ct)
    {
        AskBody? body = null;
        try
        {
            body = await http.Request.ReadFromJsonAsync<AskBody>(JsonOpts, ct);
        }
        catch
        {
            http.Response.StatusCode = 400;
            await http.Response.WriteAsJsonAsync(new { error = "Invalid JSON body" }, ct);
            return;
        }

        var message = (body?.Message ?? "").Trim();
        var projectId = (body?.ProjectId ?? "").Trim();
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

        await WriteSse(http, new { meta = true, source = "casco-upstream", engine = "casco", projectId = ProjectId }, ct);

        try
        {
            var reply = await GenerateAsync(ai, message, ct);
            var enumerator = StringInfoChars(reply);
            foreach (var token in enumerator)
            {
                ct.ThrowIfCancellationRequested();
                await WriteSse(http, new { token }, ct);
                await Task.Delay(12, ct);
            }
            await WriteSse(http, new { done = true, source = "casco-upstream", engine = "casco" }, ct);
        }
        catch (OperationCanceledException)
        {
            // client disconnected
        }
        catch (Exception ex)
        {
            await WriteSse(http, new { error = Text.Truncate(ex.Message, 200) }, ct);
        }
    }

    static async Task<string> GenerateAsync(AiClient ai, string message, CancellationToken ct)
    {
        var result = await ai.CompleteAsync(
            AiTiers.Cheap,
            new ChatRequest(
                [
                    new("system", SystemPrompt),
                    new("user", message),
                ],
                MaxOutputTokens: 700,
                Json: false),
            new AiCallContext(null, null, null, "ardh-ask", "public"),
            ct);

        var reply = (result.Content ?? "").Trim();
        if (string.IsNullOrEmpty(reply))
            reply = "تعذّر الرد اللحظة. تواصل على واتساب 009647801322505.";
        return reply;
    }

    static async Task WriteSse(HttpContext http, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await http.Response.Body.WriteAsync(bytes, ct);
        await http.Response.Body.FlushAsync(ct);
    }

    /// <summary>Enumerate Unicode text elements so Arabic combining marks stay with their letters.</summary>
    static IEnumerable<string> StringInfoChars(string text)
    {
        var enumr = System.Globalization.StringInfo.GetTextElementEnumerator(text);
        while (enumr.MoveNext())
            yield return enumr.GetTextElement();
    }

    public record AskBody(string? Message, string? ProjectId);
}
