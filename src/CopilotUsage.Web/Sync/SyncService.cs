using System.Text.Json;
using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;
using CopilotUsage.Data;
using CopilotUsage.Data.Demo;
using CopilotUsage.Data.Entities;
using CopilotUsage.GitHub;
using CopilotUsage.Web.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Web.Sync;

/// <summary>
/// The four sync steps from PLAN.md section 5 (seats; org credits by day; credits per user per model;
/// per-user metrics), or a single demo-seed step in demo mode. Each step gets its own DI scope and
/// DbContext, so one failing step cannot poison the next. Every upsert is idempotent.
/// </summary>
public sealed class SyncService(
    IServiceScopeFactory scopes,
    TimeProvider time,
    IOptions<DemoOptions> demo,
    ILogger<SyncService> logger) : ISyncService
{
    private const int GiveUpAfterConsecutiveFailures = 3;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsRunning => _gate.CurrentCount == 0;

    public async Task<SyncResult> RunAsync(SyncTrigger trigger, CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
        {
            logger.LogInformation("Sync requested ({Trigger}) but one is already running; skipping.", trigger);
            return new SyncResult(null, SyncStatus.Running, true, []);
        }

        try
        {
            return await RunCoreAsync(trigger, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SyncResult> RunCoreAsync(SyncTrigger trigger, CancellationToken ct)
    {
        var isDemo = demo.Value.Enabled;
        var runId = await CreateRunAsync(trigger, isDemo, ct);
        var steps = new List<SyncStepResult>();

        try
        {
            if (isDemo)
            {
                steps.Add(await RunStepAsync("DemoSeed", DemoSeedAsync, ct));
            }
            else
            {
                steps.Add(await RunStepAsync("Seats", SeatsAsync, ct));
                steps.Add(await RunStepAsync("OrgCreditsByDay", OrgCreditsAsync, ct));
                steps.Add(await RunStepAsync("UserCreditsByModel", UserCreditsAsync, ct));
                steps.Add(await RunStepAsync("UserMetrics", UserMetricsAsync, ct));
            }
        }
        catch (OperationCanceledException)
        {
            await FinishRunAsync(runId, SyncStatus.Failed, steps, "Cancelled (host shutting down).");
            throw;
        }

        var status = steps.All(s => s.Status == SyncStatus.Succeeded) ? SyncStatus.Succeeded
            : steps.All(s => s.Status == SyncStatus.Failed) ? SyncStatus.Failed
            : SyncStatus.PartialFailure;
        var firstError = steps.FirstOrDefault(s => s.Error is not null)?.Error;
        await FinishRunAsync(runId, status, steps, firstError);

        if (status == SyncStatus.Succeeded) logger.LogInformation("Sync {RunId} succeeded: {Steps}", runId, Describe(steps));
        else logger.LogWarning("Sync {RunId} finished as {Status}: {Steps}", runId, status, Describe(steps));
        return new SyncResult(runId, status, false, steps);
    }

    // ---- steps ----

    private sealed record StepOutcome(int Rows, int Errors, string? FirstError);

    private async Task<SyncStepResult> RunStepAsync(string name, Func<IServiceProvider, CancellationToken, Task<StepOutcome>> body, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var o = await body(scope.ServiceProvider, ct);
            var status = o.Errors == 0 ? SyncStatus.Succeeded : o.Rows > 0 ? SyncStatus.PartialFailure : SyncStatus.Failed;
            return new SyncStepResult(name, status, o.Rows, o.Errors, o.FirstError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync step {Step} failed.", name);
            return new SyncStepResult(name, SyncStatus.Failed, 0, 1, Truncate($"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private async Task<StepOutcome> DemoSeedAsync(IServiceProvider sp, CancellationToken ct) =>
        new(await sp.GetRequiredService<DemoSeeder>().SeedAsync(
            DemoScenarios.TryParse(demo.Value.Scenario, out var scenario) ? scenario : DemoScenario.Normal, ct), 0, null);

    private async Task<StepOutcome> SeatsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IGitHubClient>();
        var writer = sp.GetRequiredService<UsageWriter>();
        var now = time.GetUtcNow();
        int rows = 0, errors = 0;
        string? first = null;

        SeatSummary? summary = null;
        try
        {
            summary = await client.GetSeatSummaryAsync(ct);
            await writer.UpsertSeatSummaryAsync(summary, now, ct);
            rows++;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex, "seat summary", ref errors, ref first);
        }

        try
        {
            var seats = await client.GetSeatsAsync(ct);
            if (seats.Count == 0 && summary?.TotalSeats > 0)
            {
                errors++;
                first ??= $"Seats endpoint returned no seats but the summary reports {summary.TotalSeats}; seat list left unchanged.";
                logger.LogWarning("{Message}", first);
            }
            else
            {
                rows += await writer.UpsertSeatsAsync(seats, now, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            Fail(ex, "seats", ref errors, ref first);
        }
        return new StepOutcome(rows, errors, first);
    }

    private async Task<StepOutcome> OrgCreditsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IGitHubClient>();
        var writer = sp.GetRequiredService<UsageWriter>();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        int rows = 0, errors = 0, consecutive = 0;
        string? first = null;

        foreach (var month in MonthsToSync(today))
        {
            var last = month.Contains(today) ? today : month.LastDay;
            for (var day = month.Start; day <= last; day = day.AddDays(1))
            {
                try
                {
                    var records = await client.GetCreditUsageAsync(new CreditUsageQuery(day.Year, day.Month, day.Day), ct);
                    rows += await writer.UpsertOrgDailyCreditsAsync(records.Select(r => r with { Day = day }), [day], ct);
                    consecutive = 0;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Fail(ex, $"org credits for {day:yyyy-MM-dd}", ref errors, ref first);
                    if (++consecutive >= GiveUpAfterConsecutiveFailures) return Abandoned(rows, errors, first);
                }
            }
        }
        return new StepOutcome(rows, errors, first);
    }

    private async Task<StepOutcome> UserCreditsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IGitHubClient>();
        var writer = sp.GetRequiredService<UsageWriter>();
        var db = sp.GetRequiredService<CopilotUsageDbContext>();
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        var logins = await db.Seats.AsNoTracking().Where(s => s.IsActive).Select(s => s.Login).ToListAsync(ct);
        int rows = 0, errors = 0, consecutive = 0;
        string? first = null;

        foreach (var month in MonthsToSync(today))
        foreach (var login in logins)
        {
            try
            {
                var records = await client.GetCreditUsageAsync(new CreditUsageQuery(month.Year, month.Month, null, login), ct);
                rows += await writer.UpsertUserModelMonthAsync(month, login, records, ct);
                consecutive = 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Fail(ex, $"credits for {login} in {month}", ref errors, ref first);
                if (++consecutive >= GiveUpAfterConsecutiveFailures) return Abandoned(rows, errors, first);
            }
        }
        return new StepOutcome(rows, errors, first);
    }

    private async Task<StepOutcome> UserMetricsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var client = sp.GetRequiredService<IGitHubClient>();
        var writer = sp.GetRequiredService<UsageWriter>();
        var report = await client.GetUserMetricsAsync(null, ct);
        var rows = await writer.UpsertUserMetricsAsync(report.Users, ct);
        if (rows == 0 && report.MalformedLines > 0)
            return new StepOutcome(0, 1, $"Metrics report contained no usable lines ({report.MalformedLines} malformed).");
        return new StepOutcome(rows, 0, null);
    }

    // ---- helpers ----

    /// <summary>The current month, plus the previous one for the first days of a month so its final days are completed.</summary>
    private static IEnumerable<BillingMonth> MonthsToSync(DateOnly today)
    {
        var current = BillingMonth.From(today);
        if (today.Day <= 3) yield return current.Previous;
        yield return current;
    }

    private void Fail(Exception ex, string what, ref int errors, ref string? first)
    {
        errors++;
        first ??= Truncate($"{what}: {ex.Message}");
        logger.LogError(ex, "Sync failed fetching {What}.", what);
    }

    private StepOutcome Abandoned(int rows, int errors, string? first)
    {
        logger.LogError("Giving up on this sync step after {Count} consecutive failures.", GiveUpAfterConsecutiveFailures);
        return new StepOutcome(rows, errors, first);
    }

    private async Task<int> CreateRunAsync(SyncTrigger trigger, bool isDemo, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CopilotUsageDbContext>();
        var run = new SyncRun { StartedAt = time.GetUtcNow(), Trigger = trigger, Status = SyncStatus.Running, IsDemo = isDemo };
        db.SyncRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return run.Id;
    }

    private async Task FinishRunAsync(int runId, SyncStatus status, List<SyncStepResult> steps, string? error)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CopilotUsageDbContext>();
            var run = await db.SyncRuns.FirstAsync(r => r.Id == runId, CancellationToken.None);
            run.FinishedAt = time.GetUtcNow();
            run.Status = status;
            run.Error = error;
            run.StepsJson = JsonSerializer.Serialize(steps.Select(s => new { step = s.Step, status = s.Status.ToString(), rows = s.Rows, errors = s.Errors, error = s.Error }));
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not record the result of sync run {RunId}.", runId);
        }
    }

    private static string Describe(IEnumerable<SyncStepResult> steps) =>
        string.Join(", ", steps.Select(s => $"{s.Step}={s.Status}({s.Rows} rows, {s.Errors} errors)"));

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..500];
}
