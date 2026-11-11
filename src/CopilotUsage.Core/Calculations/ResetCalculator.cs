namespace CopilotUsage.Core.Calculations;

public static class ResetCalculator
{
    /// <summary>
    /// Whole UTC calendar days until the pool resets at 00:00 UTC on the 1st.
    /// On the last day of the month this is 1 (it resets tonight); on the 1st it is the length of the month.
    /// </summary>
    public static int DaysUntilReset(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return BillingMonth.From(today).NextStart.DayNumber - today.DayNumber;
    }

    public static TimeSpan TimeUntilReset(DateTimeOffset now) =>
        BillingMonth.Containing(now).NextStartUtc - now.ToUniversalTime();

    public static int DaysUntilReset(TimeProvider time) => DaysUntilReset(time.GetUtcNow());
}
