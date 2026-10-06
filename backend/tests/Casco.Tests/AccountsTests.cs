using Casco.Api.Features.Admin;

namespace Casco.Tests;

public class AccountsTests
{
    private static readonly DateTime Today = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_month_view_counts_each_cadence_once()
    {
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(30m, AccountsMath.Charge("month", 30, new DateTime(2026, 1, 1), from, to, Today));
        Assert.Equal(10m, AccountsMath.Charge("year", 120, new DateTime(2026, 1, 1), from, to, Today));
        Assert.Equal(15m, AccountsMath.Charge("day", 1, new DateTime(2026, 10, 1), from, to, Today));
        Assert.Equal(8m, AccountsMath.Charge("once", 8, new DateTime(2026, 10, 3), from, to, Today));
        Assert.Equal(0m, AccountsMath.Charge("once", 8, new DateTime(2026, 9, 3), from, to, Today));
        Assert.Equal(0m, AccountsMath.Charge("month", 30, new DateTime(2026, 11, 1), from, to, Today));
    }

    [Fact]
    public void A_year_view_adds_the_months_and_stops_at_today()
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(300m, AccountsMath.Charge("month", 30, new DateTime(2026, 1, 1), from, to, Today));
        Assert.Equal(80m, AccountsMath.Charge("year", 120, new DateTime(2026, 3, 1), from, to, Today));
    }

    [Fact]
    public void Monthly_run_rate_ignores_one_time_payments_and_future_items()
    {
        var rate = AccountsMath.MonthlyRunRate(
        [
            ("month", 20m, new DateTime(2026, 1, 1)),
            ("year", 120m, new DateTime(2026, 1, 1)),
            ("day", 1m, new DateTime(2026, 1, 1)),
            ("once", 500m, new DateTime(2026, 2, 1)),
            ("month", 99m, new DateTime(2026, 12, 1))
        ], Today);
        Assert.Equal(20m + 10m + 30m, rate);
    }
}
