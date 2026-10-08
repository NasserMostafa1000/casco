using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Casco.Api.Domain;
using Casco.Api.Features.Ai;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Desktop;

public static class DesktopAgentEndpoints
{
    private const int MaxBodyChars = 2_000_000;

    public static void MapDesktopAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/desktop/update", UpdateAsync);
        var g = app.MapGroup("/api/desktop").RequireAuthorization();
        g.MapGet("/models", ModelsAsync);
        g.MapGet("/access", AccessAsync);
        g.MapPost("/share", ShareAsync);
        g.MapPost("/chat", ChatAsync).RequireRateLimiting("desktop");
        g.MapPost("/images", ImagesAsync).RequireRateLimiting("desktop");
    }

    private static async Task<IResult> UpdateAsync(string? version, AppDbContext db, IOptions<AppOptions> app, CancellationToken ct)
    {
        var policy = await DesktopUpdate.LoadAsync(db, ct);
        var message = string.IsNullOrWhiteSpace(policy.Message) ? DesktopUpdate.DefaultMessage : policy.Message;
        return Results.Ok(new
        {
            required = DesktopUpdate.IsOlder(version, policy.Version),
            version = policy.Version,
            message,
            url = $"{app.Value.FrontendBase}/download?update=1"
        });
    }

    private static async Task<IResult> AccessAsync(SubscriptionService subs, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var plan = await subs.GetPlanAsync(userId);
        var access = await DesktopTrial.ReadAsync(db, userId, plan.IsPro, http.Request.Headers[DesktopTrial.MachineHeader], ct);
        return Results.Ok(AccessBody(access));
    }

    private static async Task<IResult> ShareAsync(JsonElement body, SubscriptionService subs, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var slot = body.TryGetProperty("slot", out var slotEl) && slotEl.TryGetInt32(out var value) ? value : 0;
        var userId = http.User.UserId();
        var plan = await subs.GetPlanAsync(userId);
        var access = await DesktopTrial.ShareAsync(db, userId, plan.IsPro, http.Request.Headers[DesktopTrial.MachineHeader], slot, ct);
        return Results.Ok(AccessBody(access));
    }

    private static object AccessBody(DesktopTrial.Access access) => new
    {
        modelLocked = access.ModelLocked,
        shareRequired = access.ShareRequired,
        shares = access.Shares,
        slots = Enumerable.Range(1, DesktopTrial.Groups).Where(slot => (access.Mask & (1 << (slot - 1))) != 0).ToArray(),
        trialEndsAt = access.TrialEndsAt,
        downloadUrl = DesktopTrial.DownloadUrl,
    };

    private static async Task<IResult> ModelsAsync(ModelCatalog catalog, SubscriptionService subs, AppDbContext db, HttpContext http, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var plan = await subs.GetPlanAsync(userId);
        var access = await DesktopTrial.ReadAsync(db, userId, plan.IsPro, http.Request.Headers[DesktopTrial.MachineHeader], ct);
        var tiers = await catalog.GetEffectiveTiersAsync();
        var premiumOnly = new HashSet<string>(tiers.GetValueOrDefault(AiTiers.Premium) ?? [], StringComparer.Ordinal);
        foreach (var tier in new[] { AiTiers.Cheap, AiTiers.Standard })
        {
            foreach (var id in tiers.GetValueOrDefault(tier) ?? [])
                premiumOnly.Remove(id);
        }

        var models = catalog.Options.Models
            .Where(m => catalog.IsProviderConfigured(m.Provider))
            .Select(m => new
            {
                id = m.Id,
                label = EnglishLabel(m),
                premium = premiumOnly.Contains(m.Id),
                allowed = plan.CanUsePremium || !premiumOnly.Contains(m.Id),
                cost = m.InputPer1M + m.OutputPer1M
            })
            .ToList();
        var autoCost = models.Where(m => m.allowed).Select(m => m.cost).DefaultIfEmpty(0).Min();
        models.Insert(0, new { id = "auto", label = "Auto", premium = false, allowed = true, cost = autoCost });
        return Results.Ok(new { plan = plan.Key, models, modelLocked = access.ModelLocked, shareRequired = access.ShareRequired, shares = access.Shares, slots = Enumerable.Range(1, DesktopTrial.Groups).Where(slot => (access.Mask & (1 << (slot - 1))) != 0).ToArray(), downloadUrl = DesktopTrial.DownloadUrl });
    }

    private static string EnglishLabel(AiModelOptions model)
    {
        var label = string.IsNullOrWhiteSpace(model.Label) ? model.Id : model.Label;
        var cleaned = System.Text.RegularExpressions.Regex.Replace(label, @"\s*\([^)]*\)", "").Trim();
        return cleaned.Any(ch => ch > 127) ? model.Id : cleaned;
    }

    private static async Task<IResult> ChatAsync(JsonElement body, HttpContext http, ModelCatalog catalog, SubscriptionService subs,
        CreditService credits, AppDbContext db, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (!body.TryGetProperty("model", out var modelEl) || modelEl.ValueKind != JsonValueKind.String)
            throw ApiException.BadRequest("اختار موديل", "model_required");
        var modelId = modelEl.GetString() ?? "";
        await DesktopUpdate.EnsureCurrentAsync(http, db, ct);
        var userId = http.User.UserId();
        var plan = await subs.GetPlanAsync(userId);
        var machine = http.Request.Headers[DesktopTrial.MachineHeader].ToString();
        var access = await DesktopTrial.ReadAsync(db, userId, plan.IsPro, machine, ct);
        if (access.ShareRequired)
            throw new ApiException(402, "شارك رابط تنزيل Casco في 5 جروبات عشان تتفعل 5 ساعات مجانية.", "share_required");
        if (access.ModelLocked) modelId = "auto";
        var tiers = await catalog.GetEffectiveTiersAsync();
        var premiumOnly = new HashSet<string>(tiers.GetValueOrDefault(AiTiers.Premium) ?? [], StringComparer.Ordinal);
        foreach (var tier in new[] { AiTiers.Cheap, AiTiers.Standard })
        {
            foreach (var id in tiers.GetValueOrDefault(tier) ?? [])
                premiumOnly.Remove(id);
        }
        var model = modelId == "auto"
            ? PickAuto(catalog, plan, premiumOnly) ?? throw ApiException.BadRequest("No model is available right now.", "model_unavailable")
            : catalog.GetModel(modelId) ?? throw ApiException.BadRequest("Unknown model.", "unknown_model");
        if (!catalog.IsProviderConfigured(model.Provider) && !catalog.Options.UseFake)
            throw ApiException.BadRequest("That model is not available right now.", "model_unavailable");
        if (!plan.CanUsePremium && premiumOnly.Contains(model.Id))
            throw ApiException.Payment("This model is for Pro accounts. Choose another model or upgrade at casco.studio.", "premium_model");

        var covered = access.FreePrompt || access.Trial;
        var balance = await credits.GetBalanceAsync(userId);
        if (!covered && balance.Available < 1)
            throw new ApiException(402, "Your Casco credits are used up. Add credits at https://casco.studio/app/billing.", "insufficient_credits");

        if (!body.TryGetProperty("messages", out var messagesEl) || messagesEl.ValueKind != JsonValueKind.Array)
            throw ApiException.BadRequest("The message is missing.", "messages_required");

        var messages = ReadMessages(messagesEl);
        JsonNode? tools = body.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
            ? JsonNode.Parse(toolsEl.GetRawText())
            : null;
        if (tools is JsonArray toolArray && toolArray.ToJsonString().Length > 100_000)
            throw ApiException.BadRequest("The tool list is too large.", "tools_too_large");

        var started = Stopwatch.StartNew();
        JsonElement completion;
        try
        {
            completion = catalog.Options.UseFake
                ? FakeCompletion()
                : await CallProviderAsync(catalog, model, messages, tools, httpFactory, ct);
        }
        catch (AiProviderException ex)
        {
            await RecordAsync(db, userId, plan.Key, model, 0, 0, 0, 0, false, Truncate(ex.Message, 300), (int)started.ElapsedMilliseconds, "desktop");
            throw new ApiException(502, PublicModelError(ex.Message), "model_failed");
        }

        var usage = ProviderUsage.Read(completion);
        var cost = usage.CostUsd ?? CostCalculator.Compute(model, usage.Input, usage.Cached, usage.Output);
        if (cost <= 0 && (usage.Input + usage.Output) == 0)
            cost = CostCalculator.Compute(model, TokenEstimate.FromChars(messages.Sum(ContentChars)), 0, TokenEstimate.FromChars(CompletionChars(completion)));
        var charged = covered ? 0 : Math.Max(1, credits.CreditsForCost(cost));
        var after = covered ? balance : await credits.ChargeAsync(userId, charged, "desktop");
        if (access.FreePrompt) await DesktopTrial.MarkFreePromptUsedAsync(db, machine, ct);
        await DesktopTrial.LogAsync(db, userId, machine, model.Id, DesktopTrial.PromptText(body), ct);
        await RecordAsync(db, userId, plan.Key, model, usage.Input, usage.Cached, usage.Output, cost, true, null, (int)started.ElapsedMilliseconds, "desktop");

        return Results.Ok(new
        {
            completion,
            credits = new { charged, available = after.Available },
            shareRequired = access.FreePrompt,
            shares = access.Shares,
            modelLocked = access.ModelLocked || access.FreePrompt,
            downloadUrl = DesktopTrial.DownloadUrl
        });
    }

    private const decimal ImageUsd = 0.04m;
    private const string ImageModel = "dall-e-3";

    private static async Task<IResult> ImagesAsync(JsonElement body, HttpContext http, ModelCatalog catalog, SubscriptionService subs,
        CreditService credits, AppDbContext db, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        var prompt = body.TryGetProperty("prompt", out var promptEl) && promptEl.ValueKind == JsonValueKind.String
            ? (promptEl.GetString() ?? "").Trim()
            : "";
        if (prompt.Length == 0 || prompt.Length > 1000)
            throw ApiException.BadRequest("اكتب وصف الصورة في رسالة قصيرة.", "prompt_required");

        await DesktopUpdate.EnsureCurrentAsync(http, db, ct);
        var userId = http.User.UserId();
        var plan = await subs.GetPlanAsync(userId);
        var machine = http.Request.Headers[DesktopTrial.MachineHeader].ToString();
        var access = await DesktopTrial.ReadAsync(db, userId, plan.IsPro, machine, ct);
        if (access.ShareRequired)
            throw new ApiException(402, "شارك رابط تنزيل Casco في 5 جروبات عشان تتفعل 5 ساعات مجانية.", "share_required");
        var covered = access.Trial;
        var charge = covered ? 0 : Math.Max(1, credits.CreditsForCost(ImageUsd));
        var balance = await credits.GetBalanceAsync(userId);
        if (!covered && balance.Available < charge)
            throw new ApiException(402, "رصيد النقاط خلص. اشحن الحساب من https://casco.studio/app/billing.", "insufficient_credits");

        var started = Stopwatch.StartNew();
        var imageModel = new AiModelOptions { Id = ImageModel, Provider = "openai" };
        byte[] png;
        try
        {
            png = catalog.Options.UseFake
                ? Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")
                : await CallImageAsync(catalog, prompt, httpFactory, ct);
        }
        catch (AiProviderException ex)
        {
            await RecordAsync(db, userId, plan.Key, imageModel, 0, 0, 0, 0, false, Truncate(ex.Message, 300), (int)started.ElapsedMilliseconds, "desktop-image");
            throw new ApiException(502, "توليد الصورة فشل. حاول تاني.", "image_failed");
        }

        var after = covered ? balance : await credits.ChargeAsync(userId, charge, "desktop-image");
        await RecordAsync(db, userId, plan.Key, imageModel, 0, 0, 0, ImageUsd, true, null, (int)started.ElapsedMilliseconds, "desktop-image");
        return Results.Ok(new
        {
            image = Convert.ToBase64String(png),
            credits = new { charged = charge, available = after.Available }
        });
    }

    private static async Task<byte[]> CallImageAsync(ModelCatalog catalog, string prompt, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (!catalog.Options.Providers.TryGetValue("openai", out var provider) || string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new AiProviderException("Image provider is not configured");

        var body = new JsonObject
        {
            ["model"] = ImageModel,
            ["prompt"] = prompt,
            ["n"] = 1,
            ["size"] = "1024x1024",
            ["response_format"] = "b64_json"
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/images/generations")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
        var http = httpFactory.CreateClient(nameof(OpenAiCompatibleProvider));
        using var res = await http.SendAsync(req, cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode)
            throw new AiProviderException($"HTTP {(int)res.StatusCode}", (int)res.StatusCode);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.GetArrayLength() == 0
            || !data[0].TryGetProperty("b64_json", out var b64) || b64.ValueKind != JsonValueKind.String)
            throw new AiProviderException("Image response was empty");
        var encoded = b64.GetString();
        if (string.IsNullOrWhiteSpace(encoded))
            throw new AiProviderException("Image response was empty");
        return Convert.FromBase64String(encoded);
    }

    private sealed record DesktopMessage(string Role, JsonNode Content);

    private static AiModelOptions? PickAuto(ModelCatalog catalog, PlanInfo plan, HashSet<string> premiumOnly)
    {
        return catalog.Options.Models
            .Where(m => catalog.IsProviderConfigured(m.Provider))
            .Where(m => plan.CanUsePremium || !premiumOnly.Contains(m.Id))
            .OrderBy(m => m.InputPer1M + m.OutputPer1M)
            .FirstOrDefault();
    }

    private static List<DesktopMessage> ReadMessages(JsonElement messagesEl)
    {
        var messages = new List<DesktopMessage>();
        var chars = 0;
        foreach (var item in messagesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var role = item.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String ? roleEl.GetString() ?? "" : "";
            if (role is not ("system" or "user" or "assistant")) continue;
            if (!item.TryGetProperty("content", out var contentEl)) continue;
            JsonNode content;
            if (contentEl.ValueKind == JsonValueKind.String)
            {
                var text = contentEl.GetString() ?? "";
                chars += text.Length;
                content = text;
            }
            else if (contentEl.ValueKind == JsonValueKind.Array)
                content = ReadParts(contentEl, ref chars);
            else
                continue;
            if (chars > MaxBodyChars) throw ApiException.BadRequest("The request is too large.", "request_too_large");
            messages.Add(new DesktopMessage(role, content));
        }
        if (messages.Count == 0) throw ApiException.BadRequest("The message is missing.", "messages_required");
        return messages;
    }

    private static JsonArray ReadParts(JsonElement array, ref int chars)
    {
        var parts = new JsonArray();
        var images = 0;
        foreach (var part in array.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object) continue;
            var type = part.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String ? typeEl.GetString() : "";
            if (type == "text" && part.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
            {
                var text = textEl.GetString() ?? "";
                chars += text.Length;
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            }
            else if (type == "image_url" && images < 4 && part.TryGetProperty("image_url", out var imageEl)
                && imageEl.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
            {
                var url = urlEl.GetString() ?? "";
                if (!url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || url.Length > 1_500_000) continue;
                chars += url.Length;
                images++;
                parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = url } });
            }
        }
        return parts;
    }

    private static int ContentChars(DesktopMessage message) => message.Content is JsonValue value && value.TryGetValue<string>(out var text) ? text.Length : message.Content.ToJsonString().Length;

    private static async Task<JsonElement> CallProviderAsync(ModelCatalog catalog, AiModelOptions model, List<DesktopMessage> messages,
        JsonNode? tools, IHttpClientFactory httpFactory, CancellationToken ct)
    {
        if (!catalog.Options.Providers.TryGetValue(model.Provider, out var provider) || string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new AiProviderException("Provider is not configured");

        var payload = PayloadMessages(provider.SingleSystemMessage, messages);
        var body = new JsonObject
        {
            ["model"] = model.Id,
            ["messages"] = new JsonArray(payload.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content.DeepClone() }).ToArray()),
            [model.MaxTokensParam] = 8192
        };
        if (tools is not null)
            body["reasoning_effort"] = "none";
        else if (!string.IsNullOrEmpty(model.ReasoningEffort))
            body["reasoning_effort"] = model.ReasoningEffort;
        if (tools is not null) body["tools"] = tools;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(30, provider.TimeoutSeconds)));
        using var req = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        var http = httpFactory.CreateClient(nameof(OpenAiCompatibleProvider));
        using var res = await http.SendAsync(req, cts.Token);
        var text = await res.Content.ReadAsStringAsync(cts.Token);
        if (!res.IsSuccessStatusCode)
            throw new AiProviderException(ProviderError(text, (int)res.StatusCode), (int)res.StatusCode);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static JsonElement FakeCompletion()
    {
        using var doc = JsonDocument.Parse("""{"choices":[{"message":{"role":"assistant","content":"Offline mode: no model request was sent."}}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");
        return doc.RootElement.Clone();
    }

    private static int CompletionChars(JsonElement completion)
    {
        if (!completion.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) return 0;
        var message = choices[0].GetProperty("message");
        var content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "";
        return content.Length;
    }

    private static async Task RecordAsync(AppDbContext db, Guid userId, string plan, AiModelOptions model,
        int input, int cached, int output, decimal cost, bool success, string? error, int durationMs, string purpose)
    {
        db.AiUsages.Add(new AiUsage
        {
            UserId = userId,
            Provider = model.Provider,
            Model = model.Id,
            Purpose = purpose,
            Plan = plan,
            InputTokens = input,
            CachedInputTokens = cached,
            OutputTokens = output,
            EstimatedCostUsd = cost,
            ActualCostUsd = cost,
            Success = success,
            Error = error,
            DurationMs = durationMs
        });
        await db.SaveChangesAsync();
    }

    private static List<DesktopMessage> PayloadMessages(bool singleSystem, List<DesktopMessage> messages)
    {
        if (!singleSystem) return messages;
        var system = messages.TakeWhile(m => m.Role == "system" && m.Content is JsonValue).ToList();
        if (system.Count < 2) return messages;
        var joined = string.Join("\n\n", system.Select(m => m.Content?.ToString() ?? ""));
        return [new DesktopMessage("system", joined), .. messages.Skip(system.Count)];
    }

    private static string ProviderError(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
                return Truncate(message.GetString() ?? $"HTTP {status}", 240);
        }
        catch (JsonException)
        {
            return $"HTTP {status}";
        }
        return $"HTTP {status}";
    }

    private static string PublicModelError(string message)
    {
        if (string.IsNullOrWhiteSpace(message) || message.StartsWith("HTTP ", StringComparison.Ordinal))
            return "The model rejected the request. Try again.";
        return Truncate(message, 240);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
