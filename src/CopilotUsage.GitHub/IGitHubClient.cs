using CopilotUsage.Core.Models;

namespace CopilotUsage.GitHub;

/// <summary>Result of a usage-metrics report download.</summary>
public sealed record MetricsReport(
    DateOnly? StartDay,
    DateOnly? EndDay,
    IReadOnlyList<UserDailyMetrics> Users,
    int MalformedLines);

public interface IGitHubClient
{
    Task<SeatSummary> GetSeatSummaryAsync(CancellationToken ct = default);

    /// <summary>All seats, following pagination.</summary>
    Task<IReadOnlyList<SeatInfo>> GetSeatsAsync(CancellationToken ct = default);

    /// <summary>Billed AI-credit usage with optional year/month/day/user/model filters.</summary>
    Task<IReadOnlyList<CreditUsageRecord>> GetCreditUsageAsync(CreditUsageQuery query, CancellationToken ct = default);

    /// <summary>
    /// Per-user daily metrics. With <paramref name="day"/> null, the latest rolling 28-day report;
    /// otherwise the single-day report. Resolves the signed URLs and parses the NDJSON files.
    /// </summary>
    Task<MetricsReport> GetUserMetricsAsync(DateOnly? day = null, CancellationToken ct = default);
}
