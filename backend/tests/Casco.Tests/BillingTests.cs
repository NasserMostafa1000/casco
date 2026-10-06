using Casco.Api.Domain;
using Casco.Api.Features.Billing;
using Casco.Api.Infrastructure;
using Microsoft.Extensions.Options;

namespace Casco.Tests;

public class BillingTests : IDisposable
{
    private readonly TestDb _t = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
    private readonly BillingOptions _billing = new() { UsdPerCredit = 0.001m, Pro = new ProPlanOptions { MonthlyCredits = 5000 } };
    private readonly CreditService _credits;
    private readonly SubscriptionService _subs;
    private readonly Guid _userId;

    public BillingTests()
    {
        _credits = new CreditService(_t.Db, Options.Create(_billing));
        _subs = new SubscriptionService(_t.Db, _credits, Options.Create(_billing), _clock);
        var user = new User { Email = "a@b.c", Name = "A" };
        _t.Db.Users.Add(user);
        _t.Db.SaveChanges();
        _userId = user.Id;
    }

    public void Dispose() => _t.Dispose();

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.0000001, 1)]
    [InlineData(0.001, 1)]
    [InlineData(0.0031, 4)]
    [InlineData(1.5, 1500)]
    public void Credits_are_ceiling_of_cost(decimal cost, int expected) =>
        Assert.Equal(expected, CreditMath.FromCost(cost, 0.001m));

    [Fact]
    public async Task Reservation_blocks_spending_beyond_available()
    {
        await _credits.GrantAsync(_userId, 50, CreditBuckets.Topup, CreditEntryTypes.SignupBonus, null);
        await _credits.ReserveAsync(_userId, Guid.NewGuid(), 40);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _credits.ReserveAsync(_userId, Guid.NewGuid(), 20));
        Assert.Equal(402, ex.Status);
        Assert.Equal(10, (await _credits.GetBalanceAsync(_userId)).Available);
    }

    [Fact]
    public async Task Last_credits_of_the_month_stay_usable()
    {
        await _credits.GrantAsync(_userId, 30, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);

        var held = await _credits.ReserveAsync(_userId, Guid.NewGuid(), 80, 10);

        Assert.Equal(30, held);
        Assert.Equal(0, (await _credits.GetBalanceAsync(_userId)).Available);
    }

    [Fact]
    public async Task Below_the_minimum_reports_the_shortfall()
    {
        await _credits.GrantAsync(_userId, 5, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _credits.ReserveAsync(_userId, Guid.NewGuid(), 80, 10));

        Assert.Equal(402, ex.Status);
        Assert.Equal("insufficient_credits", ex.Code);
        Assert.Equal(new CreditShortfall(5, 10), ex.Details);
    }

    [Fact]
    public async Task Overrun_on_the_last_credits_is_taken_from_the_next_grant()
    {
        await _credits.GrantAsync(_userId, 30, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);
        var task = Guid.NewGuid();
        await _credits.ReserveAsync(_userId, task, 80, 10);
        await _credits.SettleAsync(_userId, task, 50);
        Assert.Equal(-20, (await _credits.GetBalanceAsync(_userId)).Plan);

        await _credits.ExpirePlanCreditsAsync(_userId, "grant");
        await _credits.GrantAsync(_userId, 5000, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, "grant");

        Assert.Equal(4980, (await _credits.GetBalanceAsync(_userId)).Plan);
    }

    [Fact]
    public async Task Big_build_tops_up_its_reservation_until_credits_run_out()
    {
        await _credits.GrantAsync(_userId, 150, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);
        var task = Guid.NewGuid();
        Assert.Equal(80, await _credits.ReserveAsync(_userId, task, 80, 10));

        Assert.Equal(70, await _credits.ExtendAsync(_userId, task, 80, 10));
        var balance = await _credits.GetBalanceAsync(_userId);
        Assert.Equal(0, balance.Available);
        Assert.Equal(150, balance.Reserved);
        Assert.Equal(0, await _credits.ExtendAsync(_userId, task, 80, 10));

        await _credits.SettleAsync(_userId, task, 140);
        Assert.Equal(10, (await _credits.GetBalanceAsync(_userId)).Available);
        Assert.Equal(0, await _credits.ExtendAsync(_userId, task, 80, 10));
        Assert.Equal(0, await _credits.ExtendAsync(_userId, Guid.NewGuid(), 80, 10));
    }

    [Fact]
    public async Task Reservations_keep_a_trail_of_what_was_consumed_or_released()
    {
        await _credits.GrantAsync(_userId, 200, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);
        var settled = Guid.NewGuid();
        var failed = Guid.NewGuid();
        await _credits.ReserveAsync(_userId, settled, 80);
        await _credits.ReserveAsync(_userId, failed, 80);
        Assert.Equal(40, (await _credits.GetBalanceAsync(_userId)).Available);

        await _credits.SettleAsync(_userId, settled, 25);
        await _credits.ReleaseAsync(failed, ReleaseReasons.Restart);
        await _credits.ReleaseAsync(failed);

        var rows = _t.Db.CreditReservations.ToDictionary(r => r.TaskId);
        Assert.Equal((ReservationStatuses.Settled, 25, ReleaseReasons.Completed), (rows[settled].Status, rows[settled].Consumed, rows[settled].CloseReason));
        Assert.Equal((ReservationStatuses.Released, 0, ReleaseReasons.Restart), (rows[failed].Status, rows[failed].Consumed, rows[failed].CloseReason));
        Assert.NotNull(rows[failed].ClosedAt);
        var balance = await _credits.GetBalanceAsync(_userId);
        Assert.Equal((0, 175), (balance.Reserved, balance.Available));
    }

    [Fact]
    public async Task Reconciliation_releases_holds_whose_task_is_gone_but_not_running_ones()
    {
        await _credits.GrantAsync(_userId, 300, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);
        var project = Guid.NewGuid();
        AgentTask Task(string status) => new() { ProjectId = project, UserId = _userId, Prompt = "x", Status = status };
        var running = Task(TaskStatuses.Running);
        var queued = Task(TaskStatuses.Queued);
        var finished = Task(TaskStatuses.Failed);
        _t.Db.AgentTasks.AddRange(running, queued, finished);
        await _t.Db.SaveChangesAsync();
        foreach (var id in new[] { running.Id, queued.Id, finished.Id, Guid.NewGuid() })
            await _credits.ReserveAsync(_userId, id, 50);

        Assert.Equal(2, await _credits.ReconcileAsync());
        Assert.Equal(100, (await _credits.GetBalanceAsync(_userId)).Reserved);
        Assert.Equal(ReleaseReasons.Stale, _t.Db.CreditReservations.Single(r => r.TaskId == finished.Id).CloseReason);
        Assert.Equal(0, await _credits.ReconcileAsync());
    }

    [Fact]
    public async Task Settle_consumes_plan_credits_before_topup_and_releases_reservation()
    {
        await _credits.GrantAsync(_userId, 30, CreditBuckets.Plan, CreditEntryTypes.PlanGrant, null);
        await _credits.GrantAsync(_userId, 100, CreditBuckets.Topup, CreditEntryTypes.Topup, null);
        var task = Guid.NewGuid();
        await _credits.ReserveAsync(_userId, task, 80);

        await _credits.SettleAsync(_userId, task, 50);

        var balance = await _credits.GetBalanceAsync(_userId);
        Assert.Equal(0, balance.Plan);
        Assert.Equal(80, balance.Topup);
        Assert.Equal(0, balance.Reserved);
    }

    [Fact]
    public async Task Monthly_activation_grants_credits_and_expires_after_a_month()
    {
        await _subs.ActivateProAsync(_userId, BillingIntervals.Monthly, "p1");

        var plan = await _subs.GetPlanAsync(_userId);
        Assert.True(plan.IsPro);
        Assert.Equal(5000, (await _credits.GetBalanceAsync(_userId)).Plan);

        _clock.Advance(TimeSpan.FromDays(32));
        plan = await _subs.GetPlanAsync(_userId);
        Assert.False(plan.IsPro);
        Assert.Equal(0, (await _credits.GetBalanceAsync(_userId)).Plan);
    }

    [Fact]
    public async Task Yearly_grants_monthly_without_rollover()
    {
        await _subs.ActivateProAsync(_userId, BillingIntervals.Yearly, "p1");
        var task = Guid.NewGuid();
        await _credits.ReserveAsync(_userId, task, 1000);
        await _credits.SettleAsync(_userId, task, 1000);
        Assert.Equal(4000, (await _credits.GetBalanceAsync(_userId)).Plan);

        _clock.Advance(TimeSpan.FromDays(31));
        await _subs.RefreshAsync(_userId);
        Assert.Equal(5000, (await _credits.GetBalanceAsync(_userId)).Plan);

        _clock.Advance(TimeSpan.FromDays(31));
        await _subs.RefreshAsync(_userId);
        await _subs.RefreshAsync(_userId);
        Assert.Equal(5000, (await _credits.GetBalanceAsync(_userId)).Plan);
        Assert.True((await _subs.GetPlanAsync(_userId)).IsPro);
    }

    [Fact]
    public async Task Early_renewal_extends_period_and_schedules_next_grant()
    {
        await _subs.ActivateProAsync(_userId, BillingIntervals.Monthly, "p1");
        var firstEnd = (await _subs.GetOrCreateAsync(_userId)).CurrentPeriodEnd!.Value;

        _clock.Advance(TimeSpan.FromDays(20));
        await _subs.ActivateProAsync(_userId, BillingIntervals.Monthly, "p2");

        var sub = await _subs.GetOrCreateAsync(_userId);
        Assert.Equal(firstEnd.AddMonths(1), sub.CurrentPeriodEnd);
        Assert.Equal(firstEnd, sub.NextCreditGrantAt);
        Assert.Equal(5000, (await _credits.GetBalanceAsync(_userId)).Plan);

        _clock.Advance(TimeSpan.FromDays(15));
        await _subs.RefreshAsync(_userId);
        Assert.True((await _subs.GetPlanAsync(_userId)).IsPro);
        Assert.Equal(5000, (await _credits.GetBalanceAsync(_userId)).Plan);
    }

    [Fact]
    public void Ziina_signature_verification()
    {
        const string body = "{\"event\":\"payment_intent.status.updated\",\"data\":{\"id\":\"pi_1\"}}";
        const string secret = "s3cret";
        var sig = Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(secret), System.Text.Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        Assert.True(ZiinaClient.VerifySignature(body, sig, secret));
        Assert.True(ZiinaClient.VerifySignature(body, sig.ToUpperInvariant(), secret));
        Assert.False(ZiinaClient.VerifySignature(body + " ", sig, secret));
        Assert.False(ZiinaClient.VerifySignature(body, null, secret));
        Assert.False(ZiinaClient.VerifySignature(body, "not-hex", secret));
        Assert.False(ZiinaClient.VerifySignature(body, sig, ""));
    }
}
