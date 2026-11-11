namespace CopilotUsage.Core.Calculations;

/// <summary>
/// A Copilot billing month. The pool resets at 00:00 UTC on the 1st, so a billing month
/// is exactly a calendar month in UTC.
/// </summary>
public readonly record struct BillingMonth
{
    public BillingMonth(int year, int month)
    {
        if (year < 1 || year > 9998) throw new ArgumentOutOfRangeException(nameof(year));
        if (month < 1 || month > 12) throw new ArgumentOutOfRangeException(nameof(month));
        Year = year;
        Month = month;
    }

    public int Year { get; }
    public int Month { get; }

    /// <summary>First day of the month.</summary>
    public DateOnly Start => new(Year, Month, 1);

    /// <summary>First day of the following month (exclusive end of this month).</summary>
    public DateOnly NextStart => Start.AddMonths(1);

    public DateOnly LastDay => NextStart.AddDays(-1);

    public int DaysInMonth => DateTime.DaysInMonth(Year, Month);

    public DateTimeOffset StartUtc => new(Start.Year, Start.Month, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset NextStartUtc => StartUtc.AddMonths(1);

    public BillingMonth Previous => From(Start.AddMonths(-1));

    public BillingMonth Next => From(NextStart);

    public bool Contains(DateOnly day) => day >= Start && day < NextStart;

    public static BillingMonth From(DateOnly day) => new(day.Year, day.Month);

    /// <summary>The billing month containing an instant, evaluated in UTC.</summary>
    public static BillingMonth Containing(DateTimeOffset instant)
    {
        var utc = instant.UtcDateTime;
        return new BillingMonth(utc.Year, utc.Month);
    }

    public static BillingMonth Current(TimeProvider time) => Containing(time.GetUtcNow());

    public override string ToString() => $"{Year:D4}-{Month:D2}";
}
