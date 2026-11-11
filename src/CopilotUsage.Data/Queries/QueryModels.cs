using CopilotUsage.Core.Calculations;

namespace CopilotUsage.Data.Queries;

/// <summary>Everything the home page needs except the leaderboard (see <see cref="UsageQueries.GetLeaderboardAsync"/>).</summary>
/// <param name="DaysUntilReset">Whole UTC days to the next reset, evaluated now (meaningful for the current month).</param>
/// <param name="Burn">One point per day of the month; Cumulative is null for days not yet reached.</param>
/// <param name="DataAsOf">Finish time of the latest sync that stored data (null if never synced).</param>
public sealed record HomeSummary(
    BillingMonth Month,
    PoolSummary Pool,
    int ActiveThisCycle,
    int DaysUntilReset,
    IReadOnlyList<BurnPoint> Burn,
    IReadOnlyList<ModelShare> ModelSplit,
    DateTimeOffset? DataAsOf,
    SyncInfo? LastSync);

public sealed record ModelShare(string Model, decimal Credits, decimal Amount, decimal? SharePercent);

public sealed record SyncInfo(int Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt, string Status, bool IsDemo, string? Error);

/// <summary>One leaderboard row. Includes every active seat; users with no credits rank last.</summary>
public sealed record LeaderboardRow(
    int Rank,
    string Login,
    string? AvatarUrl,
    decimal Credits,
    decimal Dollars,
    decimal? ShareOfPoolPercent,
    decimal? FairSharePercent,
    bool OverFairShare);

public sealed record UserRow(
    string Login,
    string? AvatarUrl,
    int Rank,
    decimal Credits,
    decimal? FairSharePercent,
    long Prompts,
    decimal? AcceptanceRate,
    string? TopModel,
    DateTimeOffset? LastActiveAt,
    bool SeatActive);

public sealed record DailyCredit(DateOnly Day, decimal Credits, decimal Cumulative);

public sealed record UserModelCredit(string Model, decimal Credits, decimal Dollars, decimal? SharePercent);

/// <summary>Activity counts for one key within a dimension (feature, IDE, client, language or model).</summary>
public sealed record ActivityItem(
    string Key,
    long Interactions,
    long Generations,
    long Acceptances,
    decimal? AcceptanceRate,
    long LocAdded,
    long LocDeleted,
    long Requests,
    long Tokens,
    string? Version);

public sealed record ProductivityTotals(
    long Prompts,
    long SuggestionsGenerated,
    long SuggestionsAccepted,
    decimal? AcceptanceRate,
    long LocSuggestedToAdd,
    long LocAdded,
    long LocDeleted,
    int ActiveDays,
    int ChatDays,
    int AgentDays,
    DateOnly? LastActiveDay);

/// <param name="Credits">Billed credits (sum of per-model billing rows).</param>
/// <param name="DailyCredits">
/// Per-day credits come from the metrics report (<c>ai_credits_used</c>, an analysis signal), because billing is
/// not queried per user per day. They will not sum exactly to <see cref="Credits"/>. Zero-filled for every day up to today.
/// </param>
/// <param name="MetricsCredits">Sum of <see cref="DailyCredits"/>.</param>
/// <param name="PoolLimit">The org pool limit (seats x credits per seat) that <see cref="ShareOfPoolPercent"/> is measured against.</param>
/// <param name="ShareOfPoolPercent">Billed credits as a percent of the pool limit; null when the pool is empty.</param>
public sealed record UserDetail(
    BillingMonth Month,
    string Login,
    string? AvatarUrl,
    string? AssigningTeam,
    bool SeatActive,
    DateTimeOffset? LastActivityAt,
    string? LastActivityEditor,
    decimal Credits,
    decimal Dollars,
    decimal FairShare,
    decimal? FairSharePercent,
    int Rank,
    int RankedUsers,
    decimal MetricsCredits,
    IReadOnlyList<DailyCredit> DailyCredits,
    IReadOnlyList<UserModelCredit> CreditsByModel,
    IReadOnlyList<ActivityItem> ByFeature,
    IReadOnlyList<ActivityItem> ByIde,
    IReadOnlyList<ActivityItem> ByClient,
    IReadOnlyList<ActivityItem> ByLanguage,
    IReadOnlyList<ActivityItem> ByModel,
    ProductivityTotals Productivity,
    decimal PoolLimit,
    decimal? ShareOfPoolPercent);
