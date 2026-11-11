namespace CopilotUsage.Core.Calculations;

/// <summary>One day of the burn chart.</summary>
/// <param name="Credits">Credits consumed that day.</param>
/// <param name="Cumulative">Running total at end of day; null for days that have not happened yet.</param>
/// <param name="EvenPace">Where an even burn of the whole pool would be at end of this day.</param>
public sealed record BurnPoint(
    DateOnly Day,
    int DayOfMonth,
    decimal Credits,
    decimal? Cumulative,
    decimal EvenPace,
    decimal Limit);

public static class BurnSeries
{
    /// <summary>
    /// One point per day of the month. Cumulative credits are filled up to and including
    /// <paramref name="today"/> (all days for a past month, none for a future month).
    /// The even-pace line runs from <c>limit/daysInMonth</c> on day 1 to the full limit on the last day.
    /// Entries in <paramref name="dailyCredits"/> outside the month are ignored.
    /// </summary>
    public static IReadOnlyList<BurnPoint> Build(
        BillingMonth month,
        IReadOnlyDictionary<DateOnly, decimal> dailyCredits,
        decimal limit,
        DateOnly today)
    {
        var days = month.DaysInMonth;
        var points = new List<BurnPoint>(days);
        decimal running = 0m;
        for (var d = 1; d <= days; d++)
        {
            var day = new DateOnly(month.Year, month.Month, d);
            var credits = day <= today && dailyCredits.TryGetValue(day, out var c) ? Math.Max(0m, c) : 0m;
            running += credits;
            points.Add(new BurnPoint(
                day,
                d,
                credits,
                day <= today ? running : null,
                limit * d / days,
                limit));
        }
        return points;
    }
}
