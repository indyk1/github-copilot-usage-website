using CopilotUsage.Core.Models;
using CopilotUsage.GitHub;

namespace CopilotUsage.Tests.Support;

/// <summary>Scriptable IGitHubClient for sync tests.</summary>
public sealed class FakeGitHubClient : IGitHubClient
{
    public SeatSummary Summary { get; set; } = new("business", 3, 0, 0, 0, 3, 0);
    public List<SeatInfo> Seats { get; set; } =
    [
        new("alice", null, null, null, "business", "platform", null, null),
        new("bob", null, null, null, "business", null, null, null),
        new("carol", null, null, null, "business", null, null, null),
    ];

    /// <summary>Credits for the org-wide per-day call, by model.</summary>
    public Dictionary<string, decimal> DayCredits { get; set; } = new() { ["GPT-5.2"] = 10m, ["Claude Sonnet 4.5"] = 5m };

    /// <summary>Credits for a per-user month call, by model.</summary>
    public Dictionary<string, decimal> UserCredits { get; set; } = new() { ["GPT-5.2"] = 100m, ["Claude Sonnet 4.5"] = 50m };

    public List<UserDailyMetrics> Metrics { get; set; } = [];

    public Func<CreditUsageQuery, Exception?>? FailCredits { get; set; }
    public Func<Exception?>? FailMetrics { get; set; }
    public Func<Task>? BeforeSeats { get; set; }
    public int CreditCalls { get; private set; }

    public async Task<SeatSummary> GetSeatSummaryAsync(CancellationToken ct = default)
    {
        SeatSummaryCalls++;
        if (BeforeSeats is not null) await BeforeSeats();
        return Summary;
    }

    /// <summary>What the fake reports as total_seats (null: none).</summary>
    public int? ReportedTotal { get; set; }
    public int SeatSummaryCalls { get; private set; }
    public int SeatListCalls { get; private set; }

    public async Task<IReadOnlyList<SeatInfo>> GetSeatsAsync(CancellationToken ct = default) =>
        (await GetSeatListAsync(ct)).Seats;

    public async Task<SeatList> GetSeatListAsync(CancellationToken ct = default)
    {
        SeatListCalls++;
        if (BeforeSeats is not null) await BeforeSeats();
        return new SeatList(Seats.ToList(), ReportedTotal);
    }

    public Task<IReadOnlyList<CreditUsageRecord>> GetCreditUsageAsync(CreditUsageQuery query, CancellationToken ct = default)
    {
        CreditCalls++;
        if (FailCredits?.Invoke(query) is { } ex) throw ex;
        var source = query.User is null ? DayCredits : UserCredits;
        IReadOnlyList<CreditUsageRecord> r = source
            .Select(kv => new CreditUsageRecord(null, query.User, kv.Key, kv.Value, kv.Value * 0.01m)).ToList();
        return Task.FromResult(r);
    }

    public Task<MetricsReport> GetUserMetricsAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        if (FailMetrics?.Invoke() is { } ex) throw ex;
        return Task.FromResult(new MetricsReport(null, null, Metrics.ToList(), 0));
    }
}
