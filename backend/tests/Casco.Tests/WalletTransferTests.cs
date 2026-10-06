using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
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
    public void Screenshot_must_be_an_image()
    {
        Assert.Equal(".jpg", WalletTransferService.ImageExtension([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Null(WalletTransferService.ImageExtension("%PDF"u8.ToArray()));
    }
}
