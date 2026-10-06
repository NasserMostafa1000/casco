using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Ai;

public class AiOptions
{
    /// <summary>Use the offline fake provider (local development without API keys).</summary>
    public bool UseFake { get; set; }
    public Dictionary<string, AiProviderOptions> Providers { get; set; } = new();
    public List<AiModelOptions> Models { get; set; } = [];
    /// <summary>Ordered fallback chain per tier: cheap, standard, premium.</summary>
    public Dictionary<string, List<string>> Tiers { get; set; } = new();
}

public class AiProviderOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool SupportsPromptCacheKey { get; set; }
    /// <summary>Gemini's OpenAI-compatible endpoint silently drops every system message after the first.</summary>
    public bool SingleSystemMessage { get; set; }
    public int TimeoutSeconds { get; set; } = 150;
}

public class AiModelOptions
{
    public string Id { get; set; } = "";
    public string Provider { get; set; } = "";
    public string? Label { get; set; }
    public decimal InputPer1M { get; set; }
    public decimal CachedInputPer1M { get; set; }
    public decimal OutputPer1M { get; set; }
    public string? ReasoningEffort { get; set; }
    public string MaxTokensParam { get; set; } = "max_completion_tokens";
    public bool JsonMode { get; set; } = true;
}

public static class AiTiers
{
    public const string Cheap = "cheap";
    public const string Standard = "standard";
    public const string Premium = "premium";
    public static readonly string[] All = [Cheap, Standard, Premium];
}

public static class CostCalculator
{
    public static decimal Compute(AiModelOptions model, int inputTokens, int cachedInputTokens, int outputTokens)
    {
        var cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        var uncached = inputTokens - cached;
        return (uncached * model.InputPer1M + cached * model.CachedInputPer1M + outputTokens * model.OutputPer1M) / 1_000_000m;
    }
}

/// <summary>
/// Model catalog from configuration, with tier chains overridable at runtime by an admin (stored in AppSettings),
/// so switching models never needs a redeploy.
/// </summary>
public class ModelCatalog(IOptionsMonitor<AiOptions> options, IServiceScopeFactory scopes, IMemoryCache cache)
{
    public const string TiersSettingKey = "ai.tiers";
    private const string CacheKey = "ai.tiers.effective";

    public AiOptions Options => options.CurrentValue;

    public AiModelOptions? GetModel(string id) => Options.Models.FirstOrDefault(m => m.Id == id);

    public bool IsProviderConfigured(string provider) =>
        Options.UseFake || (Options.Providers.TryGetValue(provider, out var p) && !string.IsNullOrWhiteSpace(p.ApiKey));

    public async Task<Dictionary<string, List<string>>> GetEffectiveTiersAsync()
    {
        if (cache.TryGetValue(CacheKey, out Dictionary<string, List<string>>? cached) && cached is not null) return cached;

        var tiers = Options.Tiers.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var setting = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == TiersSettingKey);
            if (setting is not null)
            {
                var overrides = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(setting.Value) ?? new();
                foreach (var (tier, chain) in overrides) tiers[tier] = chain;
            }
        }
        cache.Set(CacheKey, tiers, TimeSpan.FromMinutes(5));
        return tiers;
    }

    public async Task<List<AiModelOptions>> GetChainAsync(string tier)
    {
        var tiers = await GetEffectiveTiersAsync();
        var ids = tiers.GetValueOrDefault(tier) ?? tiers.GetValueOrDefault(AiTiers.Cheap) ?? [];
        return ids.Select(GetModel).OfType<AiModelOptions>().Where(m => IsProviderConfigured(m.Provider)).ToList();
    }

    public async Task SaveTiersAsync(Dictionary<string, List<string>> tiers)
    {
        foreach (var (tier, chain) in tiers)
        {
            if (!AiTiers.All.Contains(tier)) throw ApiException.BadRequest($"Tier غير معروف: {tier}");
            var unknown = chain.Where(id => GetModel(id) is null).ToList();
            if (unknown.Count > 0) throw ApiException.BadRequest($"موديل غير معروف: {string.Join(", ", unknown)}");
            if (chain.Count == 0) throw ApiException.BadRequest($"يجب اختيار موديل واحد على الأقل لـ {tier}");
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var setting = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == TiersSettingKey);
        var json = JsonSerializer.Serialize(tiers);
        if (setting is null) db.AppSettings.Add(new AppSetting { Key = TiersSettingKey, Value = json });
        else { setting.Value = json; setting.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        cache.Remove(CacheKey);
    }

    public async Task ResetTiersAsync()
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AppSettings.Where(s => s.Key == TiersSettingKey).ExecuteDeleteAsync();
        cache.Remove(CacheKey);
    }
}
