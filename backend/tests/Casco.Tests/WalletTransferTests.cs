using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Features.Publishing;
using Casco.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class WalletTransferTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
    private readonly SubscriptionService _subs;
    private readonly WalletTransferService _wallet;
    private readonly Guid _userId;
    private readonly string _proofDir = Path.Combine(Path.GetTempPath(), "casco-wallet-" + Guid.NewGuid().ToString("N"));

    public WalletTransferTests()
    {
        var billing = new BillingOptions { UsdPerCredit = 0.001m, Pro = new ProPlanOptions { MonthlyCredits = 5000 } };
        var credits = new CreditService(_t.Db, Options.Create(billing));
        _subs = new SubscriptionService(_t.Db, credits, Options.Create(billing), _clock);
        var proofs = new WalletProofStore(Options.Create(new AppOptions { DataPath = _proofDir }));
        _wallet = new WalletTransferService(_t.Db, _subs, proofs);
        var user = new User { Email = "wallet@casco.test", Name = "Wallet", GoogleId = "google-sub" };
        _t.Db.Users.Add(user);
        _t.Db.SaveChanges();
        _userId = user.Id;
    }

    public void Dispose()
    {
        _t.Dispose();
        if (Directory.Exists(_proofDir)) Directory.Delete(_proofDir, true);
    }

    [Fact]
    public async Task Approve_activates_pro_on_the_logged_in_account()
    {
        var row = new WalletTransfer
        {
            UserId = _userId,
            Method = WalletMethods.VodafoneCash,
            ProofKey = "proof.jpg",
            ContentType = "image/jpeg",
        };
        _t.Db.WalletTransfers.Add(row);
        await _t.Db.SaveChangesAsync();

        await _wallet.ApproveAsync(row.Id);
        Assert.True((await _subs.GetPlanAsync(_userId)).IsPro);

        await _wallet.ApproveAsync(row.Id);
        Assert.Equal(WalletTransferStatuses.Approved, (await _t.Db.WalletTransfers.FindAsync(row.Id))!.Status);
    }

    [Fact]
    public async Task Reject_does_not_activate_pro()
    {
        var row = new WalletTransfer
        {
            UserId = _userId,
            Method = WalletMethods.InstaPay,
            ProofKey = "proof.png",
            ContentType = "image/png",
        };
        _t.Db.WalletTransfers.Add(row);
        await _t.Db.SaveChangesAsync();

        await _wallet.RejectAsync(row.Id, "الصورة مش واضحة");
        Assert.False((await _subs.GetPlanAsync(_userId)).IsPro);
        Assert.Equal(WalletTransferStatuses.Rejected, (await _t.Db.WalletTransfers.FindAsync(row.Id))!.Status);
    }

    [Fact]
    public async Task Server_transfer_does_not_activate_pro()
    {
        var row = new WalletTransfer
        {
            UserId = _userId,
            Method = WalletMethods.VodafoneCash,
            Purpose = WalletPurposes.Server,
            ProjectId = Guid.NewGuid(),
            HostingTier = HostingTiers.Static,
            AmountMinor = ServerWalletPay.AmountMinor,
            Currency = ServerWalletPay.Currency,
            ProofKey = "proof.jpg",
            ContentType = "image/jpeg",
        };
        var project = new Project { UserId = _userId, Name = "Site", Slug = "wallet-site", SiteKey = "walletsite" };
        row.ProjectId = project.Id;
        _t.Db.Projects.Add(project);
        _t.Db.WalletTransfers.Add(row);
        await _t.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ApiException>(() => _wallet.ApproveAsync(row.Id));

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_t.Connection));
        using var provider = services.BuildServiceProvider();
        var app = Options.Create(new AppOptions { DataPath = _proofDir, SitesDomain = "casco.studio" });
        var billing = Options.Create(new BillingOptions());
        var resolver = new SiteResolver(provider.GetRequiredService<IServiceScopeFactory>(), cache, app, billing);
        var hosting = new HostingService(_t.Db, cache, resolver, billing, app, TimeProvider.System);
        var proofs = new WalletProofStore(Options.Create(new AppOptions { DataPath = _proofDir }));
        var wallet = new WalletTransferService(_t.Db, _subs, proofs, hosting);

        await wallet.ApproveAsync(row.Id, BillingIntervals.Weekly);
        Assert.False((await _subs.GetPlanAsync(_userId)).IsPro);
        var saved = await _t.Db.WalletTransfers.FindAsync(row.Id);
        Assert.Equal(BillingIntervals.Weekly, saved!.Interval);
        var site = await _t.Db.Projects.FindAsync(project.Id);
        Assert.Equal(HostingTiers.Static, site!.HostingTier);
        Assert.True(site.HostingPaidUntil > DateTime.UtcNow.AddDays(6));
        Assert.Equal(105m, saved.AmountMinor / 100m);
    }

    [Fact]
    public async Task Yearly_server_transfer_grants_twelve_months()
    {
        Assert.Equal(1260m, ServerWalletPay.ForInterval(BillingIntervals.Yearly) / 100m);
        Assert.Equal(105m, ServerWalletPay.ForInterval(BillingIntervals.Monthly) / 100m);
        Assert.Equal(8160m, WalletPay.ForInterval(BillingIntervals.Yearly) / 100m);
        Assert.Equal(680m, WalletPay.ForInterval(BillingIntervals.Monthly) / 100m);

        var project = new Project { UserId = _userId, Name = "Year", Slug = "wallet-year", SiteKey = "walletyear" };
        var row = new WalletTransfer
        {
            UserId = _userId,
            Method = WalletMethods.VodafoneCash,
            Purpose = WalletPurposes.Server,
            ProjectId = project.Id,
            HostingTier = HostingTiers.Static,
            Interval = BillingIntervals.Yearly,
            AmountMinor = ServerWalletPay.ForInterval(BillingIntervals.Yearly),
            Currency = ServerWalletPay.Currency,
            ProofKey = "proof.jpg",
            ContentType = "image/jpeg",
        };
        _t.Db.Projects.Add(project);
        _t.Db.WalletTransfers.Add(row);
        await _t.Db.SaveChangesAsync();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite(_t.Connection));
        using var provider = services.BuildServiceProvider();
        var app = Options.Create(new AppOptions { DataPath = _proofDir, SitesDomain = "casco.studio" });
        var billing = Options.Create(new BillingOptions());
        var resolver = new SiteResolver(provider.GetRequiredService<IServiceScopeFactory>(), cache, app, billing);
        var hosting = new HostingService(_t.Db, cache, resolver, billing, app, TimeProvider.System);
        var proofs = new WalletProofStore(Options.Create(new AppOptions { DataPath = _proofDir }));
        var wallet = new WalletTransferService(_t.Db, _subs, proofs, hosting);

        await wallet.ApproveAsync(row.Id, BillingIntervals.Monthly);
        var site = await _t.Db.Projects.FindAsync(project.Id);
        Assert.True(site!.HostingPaidUntil > DateTime.UtcNow.AddDays(360));
        Assert.Equal(BillingIntervals.Yearly, (await _t.Db.WalletTransfers.FindAsync(row.Id))!.Interval);
    }

    [Fact]
    public void Screenshot_must_be_an_image()
    {
        Assert.Equal(".jpg", WalletTransferService.ImageExtension([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(WalletTransferService.ImageExtension("%PDF"u8.ToArray()));
    }
}
