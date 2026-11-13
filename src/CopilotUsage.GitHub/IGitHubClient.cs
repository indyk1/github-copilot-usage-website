using CopilotUsage.Core.Models;

namespace CopilotUsage.GitHub;

/// <summary>Result of a usage-metrics report download.</summary>
public sealed record MetricsReport(
    DateOnly? StartDay,
    DateOnly? EndDay,
    IReadOnlyList<UserDailyMetrics> Users,
    int MalformedLines);

/// <summary>The seat list plus the total GitHub itself reported (null when the response had none).</summary>
public sealed record SeatList(IReadOnlyList<SeatInfo> Seats, int? ReportedTotal);

public interface IGitHubClient
{
    /// <summary>Organization scope: the billing summary call. Enterprise scope: derived from the seat list (no such endpoint is known).</summary>
    Task<SeatSummary> GetSeatSummaryAsync(CancellationToken ct = default);

    /// <summary>All seats, following pagination and de-duplicated by login.</summary>
    Task<IReadOnlyList<SeatInfo>> GetSeatsAsync(CancellationToken ct = default);

    /// <summary>Like <see cref="GetSeatsAsync"/> but also returns GitHub's <c>total_seats</c>, so the caller can cross-check an empty list.</summary>
    Task<SeatList> GetSeatListAsync(CancellationToken ct = default);

    /// <summary>Billed AI-credit usage with optional year/month/day/user/model filters.</summary>
    Task<IReadOnlyList<CreditUsageRecord>> GetCreditUsageAsync(CreditUsageQuery query, CancellationToken ct = default);

    /// <summary>
    /// Per-user daily metrics. With <paramref name="day"/> null, the latest rolling 28-day report;
    /// otherwise the single-day report. Resolves the signed URLs and parses the NDJSON files.
    /// </summary>
    Task<MetricsReport> GetUserMetricsAsync(DateOnly? day = null, CancellationToken ct = default);
}
