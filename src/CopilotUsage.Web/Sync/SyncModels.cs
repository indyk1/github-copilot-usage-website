using CopilotUsage.Data.Entities;

namespace CopilotUsage.Web.Sync;

public sealed record SyncStepResult(string Step, SyncStatus Status, int Rows, int Errors, string? Error);

/// <param name="Skipped">True when another sync was already running, so this call did nothing.</param>
public sealed record SyncResult(int? RunId, SyncStatus Status, bool Skipped, IReadOnlyList<SyncStepResult> Steps);

public interface ISyncService
{
    /// <summary>True while a sync is executing.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Runs one full sync now (what the UI's refresh button will call). Never throws for step failures:
    /// they are logged and recorded in SyncRun. Only cancellation propagates. If a sync is already
    /// running the call returns immediately with <see cref="SyncResult.Skipped"/> set.
    /// </summary>
    Task<SyncResult> RunAsync(SyncTrigger trigger, CancellationToken ct = default);
}
