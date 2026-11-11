using CopilotUsage.Data;
using CopilotUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Web.Sync;

/// <summary>
/// On the Free plan the app can be stopped while a sync is running (idle unload, restart, deployment). The run row would then say
/// "Running" forever and nobody would know the sync was lost. At startup nothing can be running, so any such row is closed as failed:
/// /healthz then reports it, and the next call from the Function repeats the work (every step is idempotent).
/// </summary>
public static class SyncRecovery
{
    public static async Task<int> CloseInterruptedRunsAsync(CopilotUsageDbContext db, TimeProvider time, ILogger logger, CancellationToken ct = default)
    {
        var stuck = await db.SyncRuns.Where(r => r.Status == SyncStatus.Running).ToListAsync(ct);
        foreach (var run in stuck)
        {
            run.Status = SyncStatus.Failed;
            run.FinishedAt = time.GetUtcNow();
            run.Error = "Interrupted: the app stopped or restarted while this sync was running. The next scheduled call repeats it.";
        }
        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("{Count} sync run(s) were still marked Running at startup; closed as failed (the app had stopped mid-sync).", stuck.Count);
        }
        return stuck.Count;
    }
}
