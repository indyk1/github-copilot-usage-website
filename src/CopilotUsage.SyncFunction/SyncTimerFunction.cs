using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace CopilotUsage.SyncFunction;

/// <summary>Raised so that a run that did not sync shows as failed in the Functions host (and in alerts built on it).</summary>
public sealed class SyncRunFailedException(string message) : Exception(message);

public sealed class SyncTimerFunction(SyncRunner runner, ILogger<SyncTimerFunction> logger)
{
    /// <summary>
    /// Fires on the NCRONTAB expression in the app setting SyncSchedule (six fields, for example "0 0 */4 * * *" = every 4 hours).
    /// A Functions binding expression cannot carry a default, so the setting must exist; the example settings file and the
    /// deployment set it to every 4 hours. RunOnStartup stays off: a new deployment should not call the web app by surprise.
    /// </summary>
    [Function("SyncTimer")]
    public async Task Run([TimerTrigger("%SyncSchedule%", RunOnStartup = false)] TimerInfo timer, CancellationToken ct)
    {
        if (timer.IsPastDue) logger.LogWarning("The timer is running late (the Function app was not running at the scheduled time).");
        var outcome = await runner.RunAsync(ct);
        if (outcome.IsFailure) throw new SyncRunFailedException(outcome.Message);
    }
}
