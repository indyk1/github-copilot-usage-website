using CopilotUsage.Data.Entities;
using CopilotUsage.Web.Config;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Web.Sync;

/// <summary>Runs a sync every <c>Sync:IntervalHours</c>. Never lets an exception escape and take the host down.</summary>
public sealed class SyncWorker(
    ISyncService sync,
    IOptions<SyncOptions> options,
    IOptions<DemoOptions> demo,
    ILogger<SyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Demo data is seeded in Program before the host starts, so there is nothing to do at startup in demo mode.
        if (options.Value.RunOnStartup && !demo.Value.Enabled)
            await RunSafelyAsync(SyncTrigger.Startup, stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromHours(options.Value.IntervalHours));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await RunSafelyAsync(SyncTrigger.Scheduled, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    private async Task RunSafelyAsync(SyncTrigger trigger, CancellationToken ct)
    {
        try
        {
            await sync.RunAsync(trigger, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error in {Trigger} sync; will retry at the next interval.", trigger);
        }
    }
}
