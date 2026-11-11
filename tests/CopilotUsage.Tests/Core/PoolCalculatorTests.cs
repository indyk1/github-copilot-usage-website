using CopilotUsage.Core.Calculations;

namespace CopilotUsage.Tests.Core;

public class PoolCalculatorTests
{
    [Fact]
    public void Limit_is_seats_times_credits_per_seat() =>
        Assert.Equal(38_000m, PoolCalculator.Limit(20, 1900m));

    [Theory]
    [InlineData(0, 1900, 0)]
    [InlineData(-3, 1900, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(1, 1900, 1900)]
    public void Limit_edge_cases(int seats, decimal perSeat, decimal expected) =>
        Assert.Equal(expected, PoolCalculator.Limit(seats, perSeat));

    [Fact]
    public void Within_pool_has_remaining_and_no_overage()
    {
        var p = PoolCalculator.Summarize(20, 1900m, 9_500m);
        Assert.Equal(38_000m, p.Limit);
        Assert.Equal(9_500m, p.Used);
        Assert.Equal(28_500m, p.Remaining);
        Assert.Equal(0m, p.OverageCredits);
        Assert.Equal(0m, p.OverageDollars);
        Assert.Equal(95m, p.UsedDollars);
        Assert.Equal(25m, p.PercentUsed);
        Assert.False(p.IsOverLimit);
    }

    [Fact]
    public void Exactly_at_limit_is_not_overage()
    {
        var p = PoolCalculator.Summarize(2, 1900m, 3_800m);
        Assert.Equal(0m, p.Remaining);
        Assert.Equal(0m, p.OverageCredits);
        Assert.Equal(100m, p.PercentUsed);
        Assert.False(p.IsOverLimit);
    }

    [Fact]
    public void Usage_over_pool_floors_remaining_at_zero_and_bills_overage_at_one_cent()
    {
        var p = PoolCalculator.Summarize(20, 1900m, 40_000m);
        Assert.Equal(0m, p.Remaining);
        Assert.Equal(2_000m, p.OverageCredits);
        Assert.Equal(20.00m, p.OverageDollars);
        Assert.Equal(400.00m, p.UsedDollars);
        Assert.True(p.IsOverLimit);
        Assert.True(p.PercentUsed > 100m);
    }

    [Fact]
    public void Zero_seats_means_empty_pool_everything_used_is_overage_and_percent_is_undefined()
    {
        var p = PoolCalculator.Summarize(0, 1900m, 150m);
        Assert.Equal(0m, p.Limit);
        Assert.Equal(0m, p.Remaining);
        Assert.Equal(150m, p.OverageCredits);
        Assert.Equal(1.50m, p.OverageDollars);
        Assert.Null(p.PercentUsed);
    }

    [Fact]
    public void Zero_seats_zero_usage_is_all_zero()
    {
        var p = PoolCalculator.Summarize(0, 1900m, 0m);
        Assert.Equal(0m, p.Limit);
        Assert.Equal(0m, p.OverageCredits);
        Assert.Null(p.PercentUsed);
    }

    [Fact]
    public void Negative_usage_is_treated_as_zero()
    {
        var p = PoolCalculator.Summarize(1, 1900m, -50m);
        Assert.Equal(0m, p.Used);
        Assert.Equal(1900m, p.Remaining);
    }

    [Fact]
    public void Dollars_are_one_cent_per_credit() => Assert.Equal(12.34m, PoolCalculator.ToDollars(1234m));
}
