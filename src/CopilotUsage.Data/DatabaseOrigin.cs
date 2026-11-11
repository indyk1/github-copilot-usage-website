using CopilotUsage.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Data;

/// <summary>What a database holds, judged from what is stored in it (the sync log records whether each run was demo), never from its file name.</summary>
public enum DataOrigin
{
    /// <summary>Nothing worth protecting: no completed sync and no data rows.</summary>
    Empty,
    /// <summary>Only synthetic data: at least one completed demo run and no real run.</summary>
    DemoOnly,
    /// <summary>A real (non-demo) sync has run, or there is data that no demo run explains. Never cleared by demo mode.</summary>
    Real,
}

public static class DatabaseOrigin
{
    /// <summary>
    /// Real if any non-demo run exists that did not fail outright (a failed run wrote nothing; a running or partial one may have).
    /// Otherwise demo-only if a completed demo run exists. Data rows with no run at all are treated as real, because
    /// they are not known to be synthetic.
    /// </summary>
    public static async Task<DataOrigin> GetAsync(CopilotUsageDbContext db, CancellationToken ct = default)
    {
        if (await db.SyncRuns.AnyAsync(r => !r.IsDemo && r.Status != SyncStatus.Failed, ct)) return DataOrigin.Real;
        if (await db.SyncRuns.AnyAsync(r => r.IsDemo && r.Status != SyncStatus.Failed, ct)) return DataOrigin.DemoOnly;
        var anyData = await db.Seats.AnyAsync(ct) || await db.SeatSnapshots.AnyAsync(ct) || await db.CreditUsageDaily.AnyAsync(ct)
            || await db.UserModelMonthly.AnyAsync(ct) || await db.UserMetricDaily.AnyAsync(ct) || await db.UserMetricBreakdown.AnyAsync(ct);
        return anyData ? DataOrigin.Real : DataOrigin.Empty;
    }

    /// <summary>Deletes all data and the demo sync runs. Only call when <see cref="GetAsync"/> said <see cref="DataOrigin.DemoOnly"/>.</summary>
    public static async Task ClearDemoAsync(CopilotUsageDbContext db, CancellationToken ct = default)
    {
        await db.UserMetricBreakdown.ExecuteDeleteAsync(ct);
        await db.UserMetricDaily.ExecuteDeleteAsync(ct);
        await db.UserModelMonthly.ExecuteDeleteAsync(ct);
        await db.CreditUsageDaily.ExecuteDeleteAsync(ct);
        await db.SeatSnapshots.ExecuteDeleteAsync(ct);
        await db.Seats.ExecuteDeleteAsync(ct);
        await db.SyncRuns.Where(r => r.IsDemo).ExecuteDeleteAsync(ct);
    }

    public const string DemoOnRealDataMessage =
        "Demo mode is on, but this database already holds data from a real GitHub sync. Demo mode replaces the data it finds, so nothing was changed. " +
        "Either turn demo mode off (Demo__Enabled=false), or point ConnectionStrings__Default at a separate database file for demo use.";
}
