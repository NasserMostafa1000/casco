using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class PricingTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ServiceProvider _services;

    public PricingTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_t.Connection));
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        _t.Dispose();
    }

    private static BillingOptions Billing() => new()
    {
        TopupPacks = [new TopupPack { Id = "small", Credits = 2000, PriceMinor = 500 }]
    };

    private PricingService Pricing(BillingOptions billing) =>
        new(Options.Create(billing), _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<PricingService>.Instance);

    [Fact]
    public async Task Saved_prices_apply_immediately_to_the_shared_options()
    {
        var billing = Billing();
        var pricing = Pricing(billing);
        var hosting = billing.Hosting; // services keep references to the options objects

        await pricing.SaveAsync(pricing.Current with
        {
            ProMonthlyPrice = 25, HostingStaticMonthly = 7, HostingBackendMonthly = 12.5m,
            TopupPacks = [new TopupPackPrice("Big", 9000, 15)]
        });

        Assert.Equal(2500, billing.Pro.MonthlyPriceMinor);
        Assert.Equal(700, hosting.StaticMonthlyMinor);
        Assert.Equal(1250, hosting.PriceMinor(HostingTiers.Backend, BillingIntervals.Monthly));
        var pack = Assert.Single(billing.TopupPacks);
        Assert.Equal(("big", 9000, 1500), (pack.Id, pack.Credits, pack.PriceMinor));
        Assert.NotNull(pricing.UpdatedAt);
    }

    [Fact]
    public async Task Saved_prices_survive_a_restart_and_reset_restores_config()
    {
        await Pricing(Billing()).SaveAsync(Pricing(Billing()).Current with { ProMonthlyPrice = 29 });

        var restarted = Billing();
        var pricing = Pricing(restarted);
        await pricing.LoadAsync();
        Assert.Equal(2900, restarted.Pro.MonthlyPriceMinor);
        Assert.Equal(18, pricing.Defaults.ProMonthlyPrice);

        await pricing.ResetAsync();
        Assert.Equal(1800, restarted.Pro.MonthlyPriceMinor);
        Assert.False(await _t.Db.AppSettings.AnyAsync(s => s.Key == PricingService.SettingKey));
    }

    [Fact]
    public async Task Corrupt_saved_prices_fall_back_to_config()
    {
        _t.Db.AppSettings.Add(new AppSetting { Key = PricingService.SettingKey, Value = "{\"ProMonthlyPrice\":0}" });
        await _t.Db.SaveChangesAsync();

        var billing = Billing();
        await Pricing(billing).LoadAsync();
        Assert.Equal(1800, billing.Pro.MonthlyPriceMinor);
    }

    public static TheoryData<string> InvalidEdits => ["zero price", "backend cheaper", "duplicate pack", "bad pack id", "no free site"];

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public async Task Rejects_invalid_prices(string edit)
    {
        var billing = Billing();
        var pricing = Pricing(billing);
        var s = pricing.Current;
        s = edit switch
        {
            "zero price" => s with { ProMonthlyPrice = 0 },
            "backend cheaper" => s with { HostingStaticMonthly = 10, HostingBackendMonthly = 5 },
            "duplicate pack" => s with { TopupPacks = [new("a", 10, 5), new("A", 20, 8)] },
            "bad pack id" => s with { TopupPacks = [new("باقة", 10, 5)] },
            _ => s with { FreeMaxProjects = 0 }
        };

        await Assert.ThrowsAsync<ApiException>(() => pricing.SaveAsync(s));
        Assert.Equal(1800, billing.Pro.MonthlyPriceMinor);
        Assert.Equal(500, billing.Hosting.StaticMonthlyMinor);
    }

    [Fact]
    public async Task Pricing_save_does_not_keep_old_operating_expenses()
    {
        var billing = Billing();
        billing.Economics.InfraMonthlyUsd = 30;
        var pricing = Pricing(billing);
        await pricing.SaveAsync(pricing.Current with
        {
            OperatingExpenses = [new("سيرفر 1", 20), new("Apple Developer", 8.5m)]
        });
        Assert.Empty(billing.Economics.OperatingExpenses);
        Assert.Equal(30m, billing.Economics.InfraMonthlyUsd);
        Assert.Empty(pricing.Current.OperatingExpenses ?? []);
    }
}
