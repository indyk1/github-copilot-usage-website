using CopilotUsage.Core.Calculations;

namespace CopilotUsage.Tests.Core;

public class BurnSeriesTests
{
    private static readonly BillingMonth Nov = new(2026, 11);

    private static Dictionary<DateOnly, decimal> Daily(params (int Day, decimal Credits)[] items) =>
        items.ToDictionary(i => new DateOnly(2026, 11, i.Day), i => i.Credits);

    [Fact]
    public void Has_one_point_per_day_and_cumulative_only_up_to_today()
    {
        var series = BurnSeries.Build(Nov, Daily((1, 100), (2, 50), (3, 0), (4, 200)), 3000m, new DateOnly(2026, 11, 4));
        Assert.Equal(30, series.Count);
        Assert.Equal([100m, 150m, 150m, 350m], series.Take(4).Select(p => p.Cumulative!.Value));
        Assert.All(series.Skip(4), p => Assert.Null(p.Cumulative));
    }

    [Fact]
    public void Even_pace_rises_linearly_to_the_full_limit_on_the_last_day()
    {
        var series = BurnSeries.Build(Nov, Daily(), 3000m, new DateOnly(2026, 11, 15));
        Assert.Equal(100m, series[0].EvenPace);
        Assert.Equal(1500m, series[14].EvenPace);
        Assert.Equal(3000m, series[29].EvenPace);
        Assert.All(series, p => Assert.Equal(3000m, p.Limit));
    }

    [Fact]
    public void First_day_of_month_has_a_single_filled_point()
    {
        var series = BurnSeries.Build(Nov, Daily((1, 40)), 3000m, new DateOnly(2026, 11, 1));
        Assert.Equal(40m, series[0].Cumulative);
        Assert.Null(series[1].Cumulative);
    }

    [Fact]
    public void Last_day_of_month_fills_everything()
    {
        var series = BurnSeries.Build(Nov, Daily((1, 10), (30, 5)), 3000m, new DateOnly(2026, 11, 30));
        Assert.All(series, p => Assert.NotNull(p.Cumulative));
        Assert.Equal(15m, series[^1].Cumulative);
    }

    [Fact]
    public void Past_month_is_fully_filled_and_future_month_is_empty()
    {
        Assert.All(BurnSeries.Build(Nov, Daily(), 100m, new DateOnly(2027, 3, 1)), p => Assert.NotNull(p.Cumulative));
        Assert.All(BurnSeries.Build(Nov, Daily((5, 99)), 100m, new DateOnly(2026, 10, 31)), p => { Assert.Null(p.Cumulative); Assert.Equal(0m, p.Credits); });
    }

    [Fact]
    public void Data_outside_the_month_and_after_today_is_ignored()
    {
        var data = Daily((2, 10), (20, 999));
        data[new DateOnly(2026, 10, 31)] = 500m;
        data[new DateOnly(2026, 12, 1)] = 500m;
        var series = BurnSeries.Build(Nov, data, 100m, new DateOnly(2026, 11, 10));
        Assert.Equal(10m, series[9].Cumulative);
    }

    [Fact]
    public void Usage_can_pass_the_limit_line()
    {
        var series = BurnSeries.Build(Nov, Daily((1, 4000)), 3000m, new DateOnly(2026, 11, 2));
        Assert.True(series[1].Cumulative > series[1].Limit);
    }

    [Fact]
    public void Zero_limit_gives_a_flat_zero_pace_line()
    {
        var series = BurnSeries.Build(Nov, Daily((1, 5)), 0m, new DateOnly(2026, 11, 3));
        Assert.All(series, p => Assert.Equal(0m, p.EvenPace));
    }

    [Fact]
    public void February_leap_year_has_29_points() =>
        Assert.Equal(29, BurnSeries.Build(new BillingMonth(2028, 2), new Dictionary<DateOnly, decimal>(), 100m, new DateOnly(2028, 2, 10)).Count);
}
