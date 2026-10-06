using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Casco.Api.Features.Ai;

public record ChatMessageDto(string Role, string Content);

public record ChatRequest(IReadOnlyList<ChatMessageDto> Messages, int MaxOutputTokens, string? PromptCacheKey = null, bool Json = true);

/// <param name="ProviderCostUsd">Cost the provider reported for this call (e.g. OpenRouter's usage.cost), when it reports one.</param>
public record ChatResult(string Content, int InputTokens, int CachedInputTokens, int OutputTokens, decimal? ProviderCostUsd = null);

public class AiProviderException(string message, int? status = null, bool billable = false, int partialOutputChars = 0, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? Status { get; } = status;
    /// <summary>Wait the provider asked for (Retry-After header on 429/503).</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
    /// <summary>
    /// Rate limits, overloaded servers and dropped connections before any output: the provider did no billable work,
    /// so the same model can simply be tried again after a pause.
    /// </summary>
    public bool Transient => !Billable && (Status is 429 or 500 or 502 or 503 or 504 || (Status is null && Message.StartsWith("Network error", StringComparison.Ordinal)));
    /// <summary>The provider processed the request (timeout, cut stream, empty answer) and still bills the prompt.</summary>
    public bool Billable { get; } = billable || partialOutputChars > 0;
    /// <summary>Characters already streamed before the failure; billed as output tokens.</summary>
    public int PartialOutputChars { get; } = partialOutputChars;
}

public static class TokenEstimate
{
    /// <summary>Deliberately high (≈3 chars per token; Arabic is denser than English) so costs are never under-recorded.</summary>
    public static int FromChars(int chars) => (chars + 2) / 3;
}

/// <summary>Token usage as reported in a Chat Completions response.</summary>
public readonly record struct ProviderUsage(int Input, int Cached, int Output, decimal? CostUsd)
{
    /// <summary>
    /// Some OpenAI-compatible endpoints (e.g. Gemini with thinking) leave reasoning tokens out of completion_tokens
    /// but include them in total_tokens; they are billed as output, so the difference is added to Output.
    /// </summary>
    public static ProviderUsage Read(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return default;
        var input = GetInt(usage, "prompt_tokens");
        var output = GetInt(usage, "completion_tokens");
        var total = GetInt(usage, "total_tokens");
        if (total > input + output) output = total - input;
        var cached = usage.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object
            ? GetInt(details, "cached_tokens") : 0;
        decimal? cost = usage.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDecimal(out var d) && d >= 0 ? d : null;
        return new ProviderUsage(input, Math.Min(cached, input), output, cost);
    }

    private static int GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>Receives model output while it is being generated.</summary>
public interface IAiStream
{
    /// <summary>Discard what was streamed so far (e.g. a model failed mid-answer and a fallback model takes over).</summary>
    void Reset();
    void Append(string text);
    /// <summary>Model reasoning, shown in the chat and not part of the JSON answer.</summary>
    void Thought(string text) { }
}

public interface IChatProvider
{
    Task<ChatResult> CompleteAsync(AiModelOptions model, AiProviderOptions provider, ChatRequest request, IAiStream? stream, CancellationToken ct);
}

/// <summary>
/// Chat Completions client. Works for OpenAI and for Gemini through its OpenAI-compatible endpoint,
/// so one code path covers both providers.
/// </summary>
public class OpenAiCompatibleProvider(HttpClient http) : IChatProvider
{
    public async Task<ChatResult> CompleteAsync(AiModelOptions model, AiProviderOptions provider, ChatRequest request, IAiStream? stream, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = new JsonArray((provider.SingleSystemMessage ? MergeSystemMessages(request.Messages) : request.Messages)
                .Select(m => (JsonNode)new JsonObject
                {
                    ["role"] = m.Role,
                    ["content"] = m.Content
                }).ToArray()),
            [model.MaxTokensParam] = request.MaxOutputTokens
        };
        if (request.Json && model.JsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (!string.IsNullOrEmpty(model.ReasoningEffort)) body["reasoning_effort"] = model.ReasoningEffort;
        if (provider.SupportsPromptCacheKey && !string.IsNullOrEmpty(request.PromptCacheKey)) body["prompt_cache_key"] = request.PromptCacheKey;
        if (stream is not null)
        {
            body["stream"] = true;
            body["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(provider.TimeoutSeconds));

        using var req = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        var content = new StringBuilder();
        try
        {
            using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!res.IsSuccessStatusCode)
            {
                var error = await res.Content.ReadAsStringAsync(cts.Token);
                var retryAfter = res.Headers.RetryAfter is { } ra
                    ? ra.Delta ?? (ra.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                    : null;
                throw new AiProviderException($"HTTP {(int)res.StatusCode}: {Truncate(error, 400)}", (int)res.StatusCode, retryAfter: retryAfter);
            }
            var result = stream is null
                ? ParseCompletion(await res.Content.ReadAsStringAsync(cts.Token))
                : await ReadStreamAsync(res, request, stream, content, cts.Token);
            if (string.IsNullOrWhiteSpace(result.Content)) throw new AiProviderException("Empty response", billable: true);
            return result;
        }
        catch (AiProviderException ex) when (ex.PartialOutputChars == 0 && content.Length > 0)
        {
            throw new AiProviderException(ex.Message, ex.Status, ex.Billable, content.Length, ex.RetryAfter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiProviderException("Provider timeout", billable: true, partialOutputChars: content.Length);
        }
        catch (HttpRequestException ex) { throw new AiProviderException($"Network error: {ex.Message}", partialOutputChars: content.Length); }
        catch (IOException ex) { throw new AiProviderException($"Stream interrupted: {ex.Message}", billable: true, partialOutputChars: content.Length); }
    }

    private static ChatResult ParseCompletion(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var content = root.GetProperty("choices")[0].GetProperty("message").TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? "" : "";
        var usage = ProviderUsage.Read(root);
        return new ChatResult(content, usage.Input, usage.Cached, usage.Output, usage.CostUsd);
    }

    /// <summary>Server-sent events: "data: {chunk}" lines, usage arrives in the last chunk, ends with "data: [DONE]".</summary>
    private static async Task<ChatResult> ReadStreamAsync(HttpResponseMessage res, ChatRequest request, IAiStream stream, StringBuilder content, CancellationToken ct)
    {
        await using var body = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body, Encoding.UTF8);
        int input = 0, cached = 0, output = 0;
        decimal? cost = null;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new AiProviderException("Stream error: " + Truncate(err.ToString(), 400));
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "reasoning_content", "reasoning", "thinking" })
                {
                    if (delta.TryGetProperty(name, out var thought) && thought.ValueKind == JsonValueKind.String)
                    {
                        var idea = thought.GetString();
                        if (!string.IsNullOrEmpty(idea)) stream.Thought(idea);
                    }
                }
                if (delta.TryGetProperty("content", out var piece) && piece.ValueKind == JsonValueKind.String)
                {
                    var text = piece.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        content.Append(text);
                        stream.Append(text);
                    }
                }
            }
            var usage = ProviderUsage.Read(root);
            if (usage.Output > 0) (input, cached, output, cost) = (usage.Input, usage.Cached, usage.Output, usage.CostUsd ?? cost);
        }

        // Some providers omit usage when streaming: estimate on the high side so credits are never undercharged.
        if (output == 0 && content.Length > 0)
        {
            input = TokenEstimate.FromChars(request.Messages.Sum(m => m.Content.Length));
            output = TokenEstimate.FromChars(content.Length);
        }
        return new ChatResult(content.ToString(), input, cached, output, cost);
    }

    public static IReadOnlyList<ChatMessageDto> MergeSystemMessages(IReadOnlyList<ChatMessageDto> messages)
    {
        var system = messages.TakeWhile(m => m.Role == "system").ToList();
        if (system.Count < 2) return messages;
        return [new ChatMessageDto("system", string.Join("\n\n", system.Select(m => m.Content))), .. messages.Skip(system.Count)];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Offline provider for local development and tests: appends a visible note to index.html.</summary>
public partial class FakeChatProvider : IChatProvider
{
    [GeneratedRegex(@"## Request\s*\n(?<req>[\s\S]+?)(\n## |\z)")]
    private static partial Regex RequestSection();

    [GeneratedRegex(@"^- (?<url>https?://\S+(?:/u/|/uploads/).+)$", RegexOptions.Multiline)]
    private static partial Regex HostedImage();

    [GeneratedRegex(@"#demo-pages-(\d{1,3})")]
    private static partial Regex DemoPages();

    [GeneratedRegex(@"#demo-slow-(\d{1,2})")]
    private static partial Regex DemoSlow();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> FlakyCalls = new();

    public async Task<ChatResult> CompleteAsync(AiModelOptions model, AiProviderOptions provider, ChatRequest request, IAiStream? stream, CancellationToken ct)
    {
        var last = request.Messages.LastOrDefault(m => RequestSection().IsMatch(m.Content))?.Content ?? request.Messages[^1].Content;
        var match = RequestSection().Match(last);
        var requestText = (match.Success ? match.Groups["req"].Value : last).Trim();
        // "#demo-slow-N": real models take seconds per reply (load tests). "#demo-flaky": rate-limited twice, then answers.
        if (DemoSlow().Match(requestText) is { Success: true } slow)
            await Task.Delay(TimeSpan.FromSeconds(int.Parse(slow.Groups[1].Value)), ct);
        if (requestText.Contains("#demo-flaky", StringComparison.Ordinal) &&
            FlakyCalls.AddOrUpdate(Infrastructure.Text.Sha256Hex(last), 1, (_, n) => n + 1) <= 2)
            throw new AiProviderException("HTTP 429: rate limited (demo)", 429, retryAfter: TimeSpan.FromMilliseconds(300));
        // "#demo-read": first asks to see index.html, like a real model does when the file is only in the site map.
        var afterRead = request.Messages[^1].Content.StartsWith(Agent.PromptBuilder.ReadReplyHeader, StringComparison.Ordinal);
        if (requestText.Contains("#demo-read", StringComparison.Ordinal) && !afterRead)
        {
            var read = """{"summary": "", "read": ["index.html"], "files": []}""";
            stream?.Append(read);
            return new ChatResult(read, request.Messages.Sum(m => m.Content.Length) / 4, 0, read.Length / 4);
        }
        var ask = System.Net.WebUtility.HtmlEncode(requestText);
        if (ask.Length > 200) ask = ask[..200];
        var images = string.Concat(HostedImage().Matches(requestText)
            .Select(m => $"\n  <img src=\"{System.Net.WebUtility.HtmlEncode(m.Groups["url"].Value)}\" alt=\"\" style=\"max-width:100%\">"));
        var files = new List<object>
        {
            new
            {
                path = "index.html",
                op = "edit",
                edits = new[]
                {
                    new
                    {
                        find = "</body>",
                        replace = $"<section style=\"padding:24px;text-align:center;background:#fef3c7;font-family:sans-serif\">\n  <h2>تعديل تجريبي</h2>\n  <p>{ask}</p>{images}\n</section>\n</body>"
                    }
                }
            }
        };
        if (requestText.Contains("#demo-backend", StringComparison.Ordinal))
        {
            files.Add(new { path = Backend.BackendConfig.FileName, op = "write", content = DemoBackendJson });
            files.Add(new { path = Backend.BackendConfig.FunctionsFile, op = "write", content = DemoFunctions });
        }
        // "#demo-pages-N": a request too big for one reply; two pages per part, the rest goes to "remaining".
        string? remaining = null;
        if (DemoPages().Match(requestText) is { Success: true } pages)
        {
            var siteFiles = request.Messages.Count > 1 ? request.Messages[1].Content : "";
            var missing = Enumerable.Range(1, int.Parse(pages.Groups[1].Value)).Select(i => $"page-{i}.html")
                .Where(p => !Regex.IsMatch(siteFiles, $@"(<<<FILE {Regex.Escape(p)}>>>|^- {Regex.Escape(p)} \()", RegexOptions.Multiline))
                .ToList();
            foreach (var page in missing.Take(2))
                files.Add(new { path = page, op = "write", content = $"<!doctype html><html lang=\"ar\" dir=\"rtl\"><head><meta charset=\"utf-8\"><title>{page}</title></head><body><h1>{page}</h1></body></html>" });
            if (missing.Count > 2) remaining = $"{missing.Count - 2} pages left: {string.Join(", ", missing.Skip(2))}";
        }
        var summary = $"(وضع تجريبي بدون موديل حقيقي) تم تسجيل طلبك: {ask}" + (afterRead ? " (بعد قراءة الملفات)" : "");
        var result = new { summary, files, remaining };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        if (stream is not null)
        {
            for (var i = 0; i < json.Length; i += 24)
            {
                stream.Append(json.Substring(i, Math.Min(24, json.Length - i)));
                await Task.Delay(12, ct);
            }
        }
        var input = request.Messages.Sum(m => m.Content.Length) / 4;
        return new ChatResult(json, input, 0, json.Length / 4);
    }

    private const string DemoBackendJson = """
        {
          "collections": {
            "reviews": {
              "label": "التقييمات",
              "fields": {
                "name": {"type": "text", "label": "الاسم", "required": true, "max": 80},
                "rating": {"type": "integer", "label": "التقييم", "min": 1, "max": 5, "required": true},
                "phone": {"type": "phone", "label": "الهاتف", "private": true},
                "approved": {"type": "boolean", "label": "منشور", "readonly": true, "default": false}
              },
              "access": {"read": "public", "create": "public", "update": "admin", "delete": "admin"},
              "readWhere": {"approved": true}
            }
          },
          "functions": {"stats": {"access": "public"}, "spin": {"access": "public"}}
        }
        """;

    private const string DemoFunctions = """
        function stats(input, ctx) {
          var all = ctx.db.list('reviews', { pageSize: 100 });
          var sum = 0;
          all.items.forEach(function (r) { sum += r.rating; });
          return { count: all.total, average: all.total ? sum / all.total : 0, hello: input.name || null };
        }
        function spin(input, ctx) {
          while (true) { }
        }
        """;
}
