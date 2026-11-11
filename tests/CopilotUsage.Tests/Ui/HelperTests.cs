using CopilotUsage.Core.Calculations;
using CopilotUsage.Web.Ui;

namespace CopilotUsage.Tests.Ui;

public class FmtTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(0.4, "<1")]
    [InlineData(1234.5, "1,235")]
    [InlineData(47500, "47,500")]
    public void Credits(double v, string expected) => Assert.Equal(expected, Fmt.Credits((decimal)v));

    [Theory]
    [InlineData(0, "$0.00")]
    [InlineData(253.235, "$253.24")]
    [InlineData(12345.5, "$12,345.50")]
    public void Dollars(double v, string expected) => Assert.Equal(expected, Fmt.Dollars((decimal)v));

    [Theory]
    [InlineData(53.27, 0, "53%")]
    [InlineData(53.27, 1, "53.2%")]
    [InlineData(99.97, 0, "99%")]      // never "100%" while credits remain
    [InlineData(100, 0, "100%")]
    [InlineData(112.6, 0, "113%")]
    [InlineData(250, 0, "250%")]
    public void Percent(double v, int d, string expected) => Assert.Equal(expected, Fmt.Percent((decimal)v, d));

    [Fact] public void Percent_without_denominator_is_a_dash() => Assert.Equal("–", Fmt.Percent(null));

    [Theory]
    [InlineData(950, "950")]
    [InlineData(12500, "12.5k")]
    [InlineData(47500, "47.5k")]
    [InlineData(60000, "60k")]
    [InlineData(1200000, "1.2M")]
    public void Compact(double v, string expected) => Assert.Equal(expected, Fmt.Compact((decimal)v));

    [Fact]
    public void Timestamp_is_utc()
    {
        var t = new DateTimeOffset(2026, 11, 11, 12, 5, 0, TimeSpan.FromHours(2));
        Assert.Equal("11 Nov 2026, 10:05 UTC", Fmt.Timestamp(t));
    }

    [Theory]
    [InlineData(20, "just now")]
    [InlineData(300, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(86400, "1 day ago")]
    [InlineData(3 * 86400, "3 days ago")]
    [InlineData(-60, "just now")]
    public void Relative(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, Fmt.Relative(now.AddSeconds(-secondsAgo), now));
    }

    [Fact]
    public void Plural_and_month()
    {
        Assert.Equal("1 day", Fmt.Plural(1, "day"));
        Assert.Equal("20 days", Fmt.Plural(20, "day"));
        Assert.Equal("November 2026", Fmt.Month(new BillingMonth(2026, 11)));
    }
}

public class GaugeGeometryTests
{
    [Theory]
    [InlineData(0, 100, 0.0)]
    [InlineData(50, 100, 0.5)]
    [InlineData(150, 100, 1.0)]
    [InlineData(10, 0, 0.0)]
    [InlineData(-5, 100, 0.0)]
    public void Fraction_is_clamped(double used, double limit, double expected) =>
        Assert.Equal(expected, GaugeGeometry.Fraction((decimal)used, (decimal)limit), 6);

    [Theory]
    [InlineData(0, 0, 0, PoolState.NoPool)]
    [InlineData(25, 1900, 0, PoolState.Ok)]
    [InlineData(25, 1900, 42750, PoolState.Warning)]   // exactly 90%
    [InlineData(25, 1900, 47499, PoolState.Warning)]
    [InlineData(25, 1900, 47500, PoolState.Exhausted)]
    [InlineData(25, 1900, 53200, PoolState.Exhausted)]
    public void State(int seats, double perSeat, double used, PoolState expected) =>
        Assert.Equal(expected, GaugeGeometry.StateOf(PoolCalculator.Summarize(seats, (decimal)perSeat, (decimal)used)));

    [Fact]
    public void Point_is_clockwise_from_twelve()
    {
        var (x, y) = GaugeGeometry.Point(100, 100, 50, 0);
        Assert.Equal(100, x, 6); Assert.Equal(50, y, 6);
        (x, y) = GaugeGeometry.Point(100, 100, 50, 90);
        Assert.Equal(150, x, 6); Assert.Equal(100, y, 6);
        (x, y) = GaugeGeometry.Point(100, 100, 50, GaugeGeometry.StartDeg);   // bottom-left
        Assert.True(x < 100 && y > 100);
    }

    [Fact]
    public void Arc_path_uses_large_arc_flag_over_180_degrees()
    {
        Assert.Contains(" 0 1 1 ", GaugeGeometry.ArcPath(160, 150, 118, GaugeGeometry.StartDeg, 270));
        Assert.Contains(" 0 0 1 ", GaugeGeometry.ArcPath(160, 150, 118, GaugeGeometry.StartDeg, 90));
        Assert.StartsWith("M ", GaugeGeometry.ArcPath(0, 0, 10, 0, 360));   // full circle is clamped, not degenerate
    }

    [Fact]
    public void Angle_runs_from_start_to_start_plus_sweep()
    {
        Assert.Equal(GaugeGeometry.StartDeg, GaugeGeometry.AngleAt(0));
        Assert.Equal(GaugeGeometry.StartDeg + GaugeGeometry.SweepDeg, GaugeGeometry.AngleAt(2));
    }
}

public class ChartScaleTests
{
    [Theory]
    [InlineData(47500, 4, 60000)]
    [InlineData(53200, 4, 60000)]
    [InlineData(950, 4, 1000)]
    [InlineData(7, 4, 8)]
    public void Ticks_start_at_zero_and_cover_the_maximum(double max, int target, double top)
    {
        var t = ChartScale.NiceTicks((decimal)max, target);
        Assert.Equal(0m, t[0]);
        Assert.Equal((decimal)top, t[^1]);
        Assert.True(t.Count is >= 3 and <= 8);
        Assert.Equal(t.OrderBy(x => x), t);
    }

    [Fact] public void Empty_domain_still_has_an_axis() => Assert.Equal(new[] { 0m, 1m }, ChartScale.NiceTicks(0m));

    [Fact]
    public void Map_is_linear_and_safe_for_a_zero_width_domain()
    {
        Assert.Equal(150, ChartScale.Map(5, 0, 10, 100, 200));
        Assert.Equal(100, ChartScale.Map(5, 3, 3, 100, 200));
    }
}

public class InitialsTests
{
    [Theory]
    [InlineData("maya-chen", "MC")]
    [InlineData("octocat", "OC")]
    [InlineData("x", "X")]
    [InlineData("a_b_c", "AB")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    [InlineData("--", "?")]
    public void From(string? login, string expected) => Assert.Equal(expected, Initials.From(login));

    [Fact]
    public void Hue_is_stable_and_in_range()
    {
        Assert.Equal(Initials.Hue("maya-chen"), Initials.Hue("MAYA-CHEN"));
        Assert.All(new[] { "a", "bb", "maya-chen", "" }, l => Assert.InRange(Initials.Hue(l), 0, 359));
        Assert.NotEqual(Initials.Hue("maya-chen"), Initials.Hue("dmitri-volkov"));
    }
}
