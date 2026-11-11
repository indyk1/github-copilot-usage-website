namespace CopilotUsage.Core.Calculations;

/// <summary>Org-wide pool figures for one billing month.</summary>
/// <param name="PercentUsed">Used as a percent of the limit (may exceed 100). Null when the limit is zero.</param>
public sealed record PoolSummary(
    int Seats,
    decimal CreditsPerSeat,
    decimal Limit,
    decimal Used,
    decimal Remaining,
    decimal OverageCredits,
    decimal UsedDollars,
    decimal OverageDollars,
    decimal? PercentUsed)
{
    public bool IsOverLimit => OverageCredits > 0;
}

/// <summary>Pure pool arithmetic. 1 credit = $0.01.</summary>
public static class PoolCalculator
{
    public const decimal DollarsPerCredit = 0.01m;

    /// <summary>Pool size: seats x credits per seat. Negative inputs count as zero.</summary>
    public static decimal Limit(int seats, decimal creditsPerSeat) =>
        Math.Max(0, seats) * Math.Max(0m, creditsPerSeat);

    /// <summary>Credits left in the pool, floored at zero.</summary>
    public static decimal Remaining(decimal limit, decimal used) =>
        Math.Max(0m, limit - Math.Max(0m, used));

    /// <summary>Credits consumed beyond the pool (zero while within the pool).</summary>
    public static decimal OverageCredits(decimal limit, decimal used) =>
        Math.Max(0m, Math.Max(0m, used) - limit);

    public static decimal ToDollars(decimal credits) => credits * DollarsPerCredit;

    public static decimal? PercentOf(decimal value, decimal whole) =>
        whole <= 0m ? null : value / whole * 100m;

    public static PoolSummary Summarize(int seats, decimal creditsPerSeat, decimal usedCredits)
    {
        var limit = Limit(seats, creditsPerSeat);
        var used = Math.Max(0m, usedCredits);
        var overage = OverageCredits(limit, used);
        return new PoolSummary(
            Seats: Math.Max(0, seats),
            CreditsPerSeat: creditsPerSeat,
            Limit: limit,
            Used: used,
            Remaining: Remaining(limit, used),
            OverageCredits: overage,
            UsedDollars: ToDollars(used),
            OverageDollars: ToDollars(overage),
            PercentUsed: PercentOf(used, limit));
    }
}
