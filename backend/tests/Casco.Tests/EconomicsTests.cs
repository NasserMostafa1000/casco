using System.Text.Json;
using System.Text.Json.Nodes;
using Casco.Api.Domain;
using Casco.Api.Features.Admin;
using Casco.Api.Features.Ai;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class EconomicsTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
    private readonly BillingOptions _billing = new() { Economics = new EconomicsOptions { InfraMonthlyUsd = 30, PaymentFeePercent = 3 } };
    private readonly EconomicsService _economics;

    public EconomicsTests() => _economics = new EconomicsService(_t.Db, Options.Create(_billing), _clock);

    public void Dispose() => _t.Dispose();

    // ---------- Provider usage parsing ----------

    private static ProviderUsage Usage(string json) => ProviderUsage.Read(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Reads_openai_usage_with_cached_tokens()
    {
        var u = Usage("""{"usage":{"prompt_tokens":1000,"completion_tokens":300,"total_tokens":1300,"prompt_tokens_details":{"cached_tokens":800}}}""");
        Assert.Equal(new ProviderUsage(1000, 800, 300, null), u);
    }

    [Fact]
    public void Counts_hidden_reasoning_tokens_as_output()
    {
        // Gemini thinking: total_tokens includes thoughts that completion_tokens leaves out.
        var u = Usage("""{"usage":{"prompt_tokens":1000,"completion_tokens":300,"total_tokens":2500}}""");
        Assert.Equal(1500, u.Output);
    }

    [Fact]
    public void Reads_provider_reported_cost_and_clamps_cached()
    {
        var u = Usage("""{"usage":{"prompt_tokens":10,"completion_tokens":5,"cost":0.00042,"prompt_tokens_details":{"cached_tokens":99}}}""");
        Assert.Equal(0.00042m, u.CostUsd);
        Assert.Equal(10, u.Cached);
        Assert.Equal(default, Usage("""{"choices":[]}"""));
    }

    [Fact]
    public void Cost_uses_cached_price_for_cached_input()
    {
        var model = new AiModelOptions { InputPer1M = 1m, CachedInputPer1M = 0.1m, OutputPer1M = 4m };
        Assert.Equal((200 * 1m + 800 * 0.1m + 300 * 4m) / 1_000_000m, CostCalculator.Compute(model, 1000, 800, 300));
        Assert.Equal(1000, TokenEstimate.FromChars(3000));
    }

    // ---------- Aggregation ----------

    [Fact]
    public void Distribution_reports_average_median_and_p90()
    {
        var d = UnitEconomics.Distribution([1m, 2m, 3m, 4m, 100m]);
        Assert.Equal(5, d.Users);
        Assert.Equal(22m, d.Average);
        Assert.Equal(3m, d.Median);
        Assert.Equal(61.6m, d.P90);
        Assert.Equal(100m, d.Max);
        Assert.Equal(0, UnitEconomics.Distribution([]).Users);
        Assert.Throws<ApiException>(() => UnitEconomics.MonthRange("2026-13"));
    }

    [Fact]
    public void Cost_bands_and_gross_margin_per_user()
    {
        var bands = UnitEconomics.Bands([0.5m, 0.8m, 2m, 7m, 25m], 0m, 1m, 3m, 12m);
        Assert.Equal([2, 1, 1, 1], bands.Select(b => b.Count));
        Assert.Null(bands[^1].To);
        Assert.Equal(18m - 7m - 2 * 0.5m, UnitEconomics.GrossMargin(18m, 7m, 2, 0.5m));
        Assert.True(UnitEconomics.GrossMargin(0m, 0.8m, 0, 0.5m) < 0);
    }

    private (Guid Free, Guid Pro) SeedUsage()
    {
        var free = new User { Email = "f@x.y", Name = "Free", CreatedAt = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc) };
        var pro = new User { Email = "p@x.y", Name = "Pro", CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) };
        var idle = new User { Email = "i@x.y", Name = "Idle Pro", CreatedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) };
        _t.Db.Users.AddRange(free, pro, idle);
        _t.Db.Subscriptions.AddRange(
            new Subscription { UserId = pro.Id, Plan = PlanKeys.Pro, CurrentPeriodStart = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), CurrentPeriodEnd = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Subscription { UserId = idle.Id, Plan = PlanKeys.Pro, CurrentPeriodStart = new DateTime(2026, 9, 5, 0, 0, 0, DateTimeKind.Utc), CurrentPeriodEnd = new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc) });
        var day = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var task = Guid.NewGuid();
        _t.Db.AiUsages.AddRange(
            new AiUsage { UserId = free.Id, TaskId = Guid.NewGuid(), Provider = "openai", Model = "m", Purpose = "generate", Plan = PlanKeys.Free, EstimatedCostUsd = 0.02m, Success = true, CreatedAt = day },
            new AiUsage { UserId = pro.Id, TaskId = task, Provider = "openai", Model = "m", Purpose = "edit", Plan = PlanKeys.Pro, EstimatedCostUsd = 0.30m, Success = true, CreatedAt = day },
            new AiUsage { UserId = pro.Id, TaskId = task, Provider = "gemini", Model = "g", Purpose = "edit", Plan = PlanKeys.Pro, EstimatedCostUsd = 0.10m, RetryCount = 1, Success = true, CreatedAt = day },
            new AiUsage { UserId = pro.Id, TaskId = task, Provider = "openai", Model = "m", Purpose = "edit", Plan = PlanKeys.Pro, ActualCostUsd = 0, ResponseCacheHit = true, Success = true, CreatedAt = day },
            new AiUsage { UserId = pro.Id, Provider = "openai", Model = "m", Purpose = "edit", Plan = PlanKeys.Pro, EstimatedCostUsd = 5m, Success = true, CreatedAt = day.AddMonths(-1) });
        _t.Db.Payments.Add(new Payment { UserId = pro.Id, Kind = PaymentKinds.Subscription, AmountMinor = 1800, Status = PaymentStatuses.Completed, CompletedAt = day });
        _t.Db.SaveChanges();
        return (free.Id, pro.Id);
    }

    [Fact]
    public async Task Report_answers_cost_per_user_and_pro_margin()
    {
        SeedUsage();
        var r = JsonSerializer.SerializeToNode(await _economics.ReportAsync("2026-09"))!;
        decimal D(JsonNode? n) => n!.GetValue<decimal>();

        Assert.Equal(0.42m, D(r["ai"]!["cost"]));
        Assert.Equal(4, r["ai"]!["calls"]!.GetValue<int>());
        Assert.Equal(1, r["ai"]!["retries"]!.GetValue<int>());
        Assert.Equal(0.10m, D(r["ai"]!["retryCost"]));
        Assert.Equal(2, r["perUser"]!["activeUsers"]!.GetValue<int>());
        Assert.Equal(0.21m, D(r["perUser"]!["average"]));
        Assert.Equal(0.02m, D(r["perUser"]!["costPerSignup"]));

        // Two Pro subscribers (one never used AI) share $0.40 of Pro usage and $0.02 of free-user cost.
        var pro = r["pro"]!;
        Assert.Equal(2, pro["subscribers"]!.GetValue<int>());
        Assert.Equal(0.20m, D(pro["aiCostPerSubscriber"]));
        Assert.Equal(0.01m, D(pro["freeCostPerSubscriber"]));
        Assert.Equal(0.54m, D(pro["paymentFee"]));
        Assert.Equal(18m - 0.54m - 0.20m - 0.01m, D(pro["marginPerSubscriber"]));
        Assert.Equal(18m, D(r["revenue"]!["subscriptions"]));

        var edit = r["byPurpose"]!.AsArray().First(x => x!["purpose"]!.GetValue<string>() == "edit")!;
        Assert.Equal(1, edit["tasks"]!.GetValue<int>());
        Assert.Equal(3, edit["calls"]!.GetValue<int>());
    }
}
