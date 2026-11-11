namespace CopilotUsage.Core.Calculations;

public sealed record UserCredits(string Login, decimal Credits);

/// <param name="ShareOfPoolPercent">User credits as a percent of the pool limit; null when the limit is zero.</param>
/// <param name="FairSharePercent">User credits as a percent of the per-seat fair share; null when fair share is zero.</param>
public sealed record LeaderboardEntry(
    int Rank,
    string Login,
    decimal Credits,
    decimal? ShareOfPoolPercent,
    decimal? FairSharePercent,
    bool OverFairShare);

public static class Leaderboard
{
    /// <summary>Fair share is a display benchmark of credits-per-seat, not a GitHub limit.</summary>
    public static decimal? FairSharePercent(decimal credits, decimal fairShare) =>
        PoolCalculator.PercentOf(Math.Max(0m, credits), fairShare);

    /// <summary>
    /// Ranks users by credits, highest first. Equal credits share a rank (1, 2, 2, 4);
    /// ties are listed alphabetically.
    /// </summary>
    public static IReadOnlyList<LeaderboardEntry> Rank(
        IEnumerable<UserCredits> users, decimal fairShare, decimal poolLimit)
    {
        var ordered = users
            .Select(u => u with { Credits = Math.Max(0m, u.Credits) })
            .OrderByDescending(u => u.Credits)
            .ThenBy(u => u.Login, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<LeaderboardEntry>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var u = ordered[i];
            var rank = i > 0 && ordered[i - 1].Credits == u.Credits ? result[i - 1].Rank : i + 1;
            result.Add(new LeaderboardEntry(
                rank,
                u.Login,
                u.Credits,
                PoolCalculator.PercentOf(u.Credits, poolLimit),
                FairSharePercent(u.Credits, fairShare),
                fairShare > 0m && u.Credits > fairShare));
        }
        return result;
    }
}
