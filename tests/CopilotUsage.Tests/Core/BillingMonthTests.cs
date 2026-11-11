using CopilotUsage.Core.Calculations;
using Microsoft.Extensions.Time.Testing;

namespace CopilotUsage.Tests.Core;

public class BillingMonthTests
{
    [Fact]
    public void Last_instant_of_a_month_belongs_to_that_month() =>
        Assert.Equal(new BillingMonth(2026, 12), BillingMonth.Containing(new DateTimeOffset(2026, 12, 31, 23, 59, 59, 999, TimeSpan.Zero)));

    [Fact]
    public void First_instant_of_a_month_belongs_to_that_month() =>
        Assert.Equal(new BillingMonth(2027, 1), BillingMonth.Containing(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Month_is_decided_in_utc_not_in_the_local_offset()
    {
        // 23:30 on Dec 31 in UTC-5 is already 04:30 on Jan 1 in UTC.
        Assert.Equal(new BillingMonth(2027, 1), BillingMonth.Containing(new DateTimeOffset(2026, 12, 31, 23, 30, 0, TimeSpan.FromHours(-5))));
        // 00:30 on Jan 1 in UTC+2 is still Dec 31 in UTC.
        Assert.Equal(new BillingMonth(2026, 12), BillingMonth.Containing(new DateTimeOffset(2027, 1, 1, 0, 30, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void Current_uses_the_injected_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.Zero));
        Assert.Equal(new BillingMonth(2026, 11), BillingMonth.Current(clock));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new BillingMonth(2026, 12), BillingMonth.Current(clock));
    }

    [Theory]
    [InlineData(2026, 2, 28)]
    [InlineData(2028, 2, 29)]
    [InlineData(2026, 11, 30)]
    [InlineData(2026, 12, 31)]
    public void Days_in_month_and_last_day(int y, int m, int days)
    {
        var month = new BillingMonth(y, m);
        Assert.Equal(days, month.DaysInMonth);
        Assert.Equal(new DateOnly(y, m, days), month.LastDay);
    }

    [Fact]
    public void Previous_and_next_cross_year_boundaries()
    {
        Assert.Equal(new BillingMonth(2025, 12), new BillingMonth(2026, 1).Previous);
        Assert.Equal(new BillingMonth(2027, 1), new BillingMonth(2026, 12).Next);
        Assert.Equal(new DateOnly(2027, 1, 1), new BillingMonth(2026, 12).NextStart);
    }

    [Fact]
    public void Contains_is_inclusive_of_first_and_last_day_and_exclusive_of_neighbours()
    {
        var m = new BillingMonth(2026, 11);
        Assert.True(m.Contains(new DateOnly(2026, 11, 1)));
        Assert.True(m.Contains(new DateOnly(2026, 11, 30)));
        Assert.False(m.Contains(new DateOnly(2026, 10, 31)));
        Assert.False(m.Contains(new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void Utc_boundaries_are_midnight_utc()
    {
        var m = new BillingMonth(2026, 11);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), m.StartUtc);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), m.NextStartUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void Invalid_month_is_rejected(int month) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BillingMonth(2026, month));
}

public class ResetCalculatorTests
{
    [Fact]
    public void Mid_month_counts_whole_days_to_the_first() =>
        Assert.Equal(20, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Last_day_of_month_is_one_day_even_at_the_last_second() =>
        Assert.Equal(1, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 11, 30, 23, 59, 59, TimeSpan.Zero)));

    [Fact]
    public void First_day_of_month_is_the_whole_month_even_at_midnight_exactly()
    {
        Assert.Equal(31, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(30, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void February_lengths() 
    {
        Assert.Equal(1, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal(2, ResetCalculator.DaysUntilReset(new DateTimeOffset(2028, 2, 28, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1, ResetCalculator.DaysUntilReset(new DateTimeOffset(2028, 2, 29, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Year_end_resets_into_january() =>
        Assert.Equal(1, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 12, 31, 8, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void Offset_input_is_converted_to_utc_first() =>
        // 2026-12-01 00:30 at UTC+1 is still 2026-11-30 23:30 UTC.
        Assert.Equal(1, ResetCalculator.DaysUntilReset(new DateTimeOffset(2026, 12, 1, 0, 30, 0, TimeSpan.FromHours(1))));

    [Fact]
    public void Time_until_reset_is_exact()
    {
        Assert.Equal(TimeSpan.FromHours(1), ResetCalculator.TimeUntilReset(new DateTimeOffset(2026, 11, 30, 23, 0, 0, TimeSpan.Zero)));
        Assert.Equal(TimeSpan.FromDays(30), ResetCalculator.TimeUntilReset(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Works_with_the_injected_clock() =>
        Assert.Equal(20, ResetCalculator.DaysUntilReset(new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero))));
}
