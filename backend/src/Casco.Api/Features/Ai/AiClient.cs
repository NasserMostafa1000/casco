using System.Diagnostics;
using System.Text.Json;
using Casco.Api.Domain;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Api.Features.Ai;

/// <param name="Attempt">1-based round within the task (fix rounds after invalid output increase it).</param>
/// <param name="Part">Part of a multi-part build.</param>
public record AiCallContext(Guid? UserId, Guid? ProjectId, Guid? TaskId, string Purpose, string? Plan, int Attempt = 1, int Part = 1);

public record AiCallResult(string Content, string Model, string Provider, decimal CostUsd, bool FromCache, string CacheKey);

/// <summary>
/// Runs a request through the tier's model chain with automatic fallback.
/// Identical requests are answered from a local response cache (zero token cost);
/// provider-side prompt caching is maximized by PromptBuilder keeping a stable prefix.
/// </summary>
public class AiClient(
    ModelCatalog catalog,
    IServiceProvider services,
    AppDbContext db,
    IOptions<AgentOptions> agentOptions,
    ILogger<AiClient> logger)
{
    public async Task<AiCallResult> CompleteAsync(string tier, ChatRequest request, AiCallContext ctx, CancellationToken ct, IAiStream? stream = null)
    {
        var chain = await catalog.GetChainAsync(tier);
        if (chain.Count == 0)
            throw new ApiException(503, "لم يتم إعداد أي موديل ذكاء اصطناعي بعد (أضف مفاتيح OpenAI أو Gemini)", "ai_not_configured");

        var errors = new List<string>();
        for (var index = 0; index < chain.Count; index++)
        {
            var model = chain[index];
            ct.ThrowIfCancellationRequested();
            stream?.Reset();
            var cacheKey = ComputeCacheKey(model.Id, request);
            var retry = 0;
            AiUsage Usage() => new()
            {
                UserId = ctx.UserId, ProjectId = ctx.ProjectId, TaskId = ctx.TaskId, Plan = ctx.Plan,
                Provider = model.Provider, Model = model.Id, Purpose = ctx.Purpose, Part = ctx.Part,
                RetryCount = Math.Max(0, ctx.Attempt - 1) + index + retry
            };

            var sw = Stopwatch.StartNew();
            var cached = await db.AiResponseCache.FirstOrDefaultAsync(c => c.Key == cacheKey && c.ExpiresAt > DateTime.UtcNow, ct);
            if (cached is not null)
            {
                cached.Hits++;
                var hit = Usage();
                hit.InputTokens = cached.InputTokens;
                hit.OutputTokens = cached.OutputTokens;
                hit.ActualCostUsd = 0;
                hit.ResponseCacheHit = true;
                hit.Success = true;
                hit.DurationMs = (int)sw.ElapsedMilliseconds;
                db.AiUsages.Add(hit);
                await db.SaveChangesAsync(ct);
                stream?.Append(cached.Content);
                return new AiCallResult(cached.Content, model.Id, model.Provider, 0, true, cacheKey);
            }

            for (; ; retry++)
            {
                try
                {
                    var provider = ResolveProvider(model, out var providerOptions);
                    var result = await provider.CompleteAsync(model, providerOptions, request, stream, ct);
                    var cost = CostCalculator.Compute(model, result.InputTokens, result.CachedInputTokens, result.OutputTokens);

                    var usage = Usage();
                    usage.InputTokens = result.InputTokens;
                    usage.CachedInputTokens = result.CachedInputTokens;
                    usage.OutputTokens = result.OutputTokens;
                    usage.EstimatedCostUsd = cost;
                    usage.ActualCostUsd = result.ProviderCostUsd;
                    usage.Success = true;
                    usage.DurationMs = (int)sw.ElapsedMilliseconds;
                    db.AiUsages.Add(usage);
                    db.AiResponseCache.Add(new AiResponseCacheEntry
                    {
                        Key = cacheKey, Model = model.Id, Content = result.Content, InputTokens = result.InputTokens,
                        OutputTokens = result.OutputTokens, OriginalCostUsd = cost,
                        ExpiresAt = DateTime.UtcNow.AddDays(agentOptions.Value.ResponseCacheDays)
                    });
                    await SaveIgnoringCacheConflictAsync(ct);
                    return new AiCallResult(result.Content, model.Id, model.Provider, cost, false, cacheKey);
                }
                catch (AiProviderException ex)
                {
                    logger.LogWarning("Model {Model} failed: {Error}", model.Id, ex.Message);
                    errors.Add($"{model.Id}: {ex.Message}");
                    var failed = Usage();
                    failed.Success = false;
                    failed.Error = Text.Truncate(ex.Message, 500);
                    failed.DurationMs = (int)sw.ElapsedMilliseconds;
                    if (ex.Billable)
                    {
                        // The provider worked on the request before it failed and still bills it.
                        failed.InputTokens = TokenEstimate.FromChars(request.Messages.Sum(m => m.Content.Length));
                        failed.OutputTokens = TokenEstimate.FromChars(ex.PartialOutputChars);
                        failed.EstimatedCostUsd = CostCalculator.Compute(model, failed.InputTokens, 0, failed.OutputTokens);
                    }
                    db.AiUsages.Add(failed);
                    await db.SaveChangesAsync(ct);

                    // Not billable, so waiting and asking the same model again costs the user nothing.
                    if (!ex.Transient || RetryDelay(retry, ex.RetryAfter, agentOptions.Value.ProviderRetries) is not { } delay) break;
                    logger.LogInformation("Retrying {Model} in {Delay} ms after: {Error}", model.Id, (int)delay.TotalMilliseconds, ex.Message);
                    await Task.Delay(delay, ct);
                    stream?.Reset();
                    sw.Restart();
                }
            }
        }

        logger.LogError("All models failed for tier {Tier}: {Errors}", tier, string.Join(" | ", errors));
        throw new ApiException(503, "خدمة الذكاء الاصطناعي غير متاحة حالياً، حاول بعد قليل", "ai_unavailable");
    }

    /// <summary>
    /// Exponential backoff with jitter (about 1s, 2s, 4s, 8s, each randomized to 50-100%) so many workers hitting the same
    /// rate limit do not retry in lockstep. A provider's Retry-After wins when it is short; null = give up on this model.
    /// </summary>
    public static TimeSpan? RetryDelay(int retry, TimeSpan? retryAfter, int maxRetries, Random? random = null)
    {
        if (retry >= maxRetries) return null;
        if (retryAfter is { } wait && wait > TimeSpan.Zero)
            return wait <= TimeSpan.FromSeconds(20) ? wait : null;
        var ceiling = Math.Min(8_000, 1_000 * Math.Pow(2, retry));
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + (random ?? Random.Shared).NextDouble() * 0.5));
    }

    /// <summary>Drop a cached response that turned out to be unusable so a retry hits the model again.</summary>
    public async Task InvalidateAsync(string cacheKey) =>
        await db.AiResponseCache.Where(c => c.Key == cacheKey).ExecuteDeleteAsync();

    public static string ComputeCacheKey(string modelId, ChatRequest request) =>
        Text.Sha256Hex(modelId + "\n" + request.MaxOutputTokens + "\n" + request.Json + "\n" + JsonSerializer.Serialize(request.Messages));

    private IChatProvider ResolveProvider(AiModelOptions model, out AiProviderOptions providerOptions)
    {
        if (catalog.Options.UseFake)
        {
            providerOptions = new AiProviderOptions();
            return services.GetRequiredService<FakeChatProvider>();
        }
        providerOptions = catalog.Options.Providers[model.Provider];
        return services.GetRequiredService<OpenAiCompatibleProvider>();
    }

    private async Task SaveIgnoringCacheConflictAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<AiResponseCacheEntry>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            await db.SaveChangesAsync(ct);
        }
    }
}
