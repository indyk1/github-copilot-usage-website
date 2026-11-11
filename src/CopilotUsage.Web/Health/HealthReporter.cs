using System.Text.Json.Serialization;
using CopilotUsage.Data.Queries;
using CopilotUsage.Web.Config;

namespace CopilotUsage.Web.Health;

/// <summary>
/// What /healthz returns. Deliberately small: no configuration values, no token, no error text from GitHub.
/// <c>Status</c> is "ok", "degraded" (the app works but data is missing or stale) or "unhealthy" (database unreachable).
/// </summary>
public sealed record HealthReport(
    string Status,
    bool DatabaseReachable,
    DateTimeOffset? LastSuccessfulSync,
    string? LastSyncStatus,
    DateTimeOffset? LastSyncAt,
    bool Demo,
    string Version,
    DateTimeOffset CheckedAt)
{
    /// <summary>Only an unreachable database fails the probe; stale or failed syncs are visible but keep the app serving.</summary>
    [JsonIgnore] public bool IsServing => DatabaseReachable;
}

public static class HealthReporter
{
    /// <summary>A sync older than this many intervals counts as stale.</summary>
    public const double StaleAfterIntervals = 3;

    public static async Task<HealthReport> BuildAsync(
        UsageQueries queries, SyncOptions sync, DemoOptions demo, TimeProvider time, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var good = await queries.GetLastFullySuccessfulSyncAsync(timeout.Token);
            var last = await queries.GetLastSyncAsync(timeout.Token);

            var lastGoodAt = good?.FinishedAt ?? good?.StartedAt;
            var stale = lastGoodAt is null || now - lastGoodAt > TimeSpan.FromHours(sync.IntervalHours * StaleAfterIntervals);
            var failing = last is { Status: "Failed" or "PartialFailure" };
            return new HealthReport(stale || failing ? "degraded" : "ok", true, lastGoodAt, last?.Status,
                last is null ? null : last.FinishedAt ?? last.StartedAt, demo.Enabled, AppVersion.Full, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new HealthReport("unhealthy", false, null, null, null, demo.Enabled, AppVersion.Full, now);
        }
    }
}
