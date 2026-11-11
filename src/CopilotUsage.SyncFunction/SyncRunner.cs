using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.SyncFunction;

public enum SyncOutcomeKind
{
    /// <summary>The web app ran the sync and it succeeded.</summary>
    Succeeded,
    /// <summary>The sync ran; some steps failed. Counts as done, logged as a warning.</summary>
    PartialFailure,
    /// <summary>Another sync was already running: nothing to do, and not a problem.</summary>
    AlreadyRunning,
    /// <summary>The sync outlasted the request and carries on inside the web app.</summary>
    StillRunning,
    /// <summary>The sync ran and failed (for example GitHub unreachable). Reported, not retried: the next tick tries again.</summary>
    SyncFailed,
    /// <summary>401/403 or no token: the managed identity, app role or audience is set up wrongly. Retrying cannot help.</summary>
    ConfigurationError,
    /// <summary>The web app could not be reached or kept failing after all attempts.</summary>
    Unavailable,
    /// <summary>Some other answer the Function does not understand.</summary>
    UnexpectedResponse,
}

public sealed record SyncOutcome(SyncOutcomeKind Kind, int Attempts, string Message)
{
    /// <summary>Whether the timer invocation should be reported as failed.</summary>
    public bool IsFailure => Kind is SyncOutcomeKind.SyncFailed or SyncOutcomeKind.ConfigurationError or SyncOutcomeKind.Unavailable or SyncOutcomeKind.UnexpectedResponse;
}

/// <summary>Get a token, call the web app, retry what a cold start can explain, and say what happened. Holds no secrets and logs none.</summary>
public sealed class SyncRunner(
    ISyncTokenProvider tokens,
    ISyncApiClient api,
    IDelayer delayer,
    TimeProvider time,
    IOptions<SyncApiOptions> options,
    ILogger<SyncRunner> logger)
{
    public async Task<SyncOutcome> RunAsync(CancellationToken ct)
    {
        var o = options.Value;
        var started = time.GetTimestamp();
        var backoff = TimeSpan.FromSeconds(o.InitialBackoffSeconds);
        var perAttempt = TimeSpan.FromSeconds(o.TimeoutSeconds);
        string lastProblem = "no attempt was made";

        for (var attempt = 1; attempt <= o.MaxAttempts; attempt++)
        {
            string? token;
            try
            {
                token = await tokens.GetTokenAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Typically: no managed identity on this Function app, or it cannot get a token for that scope.
                logger.LogError("Could not get an access token for the web app ({Type}). Check that the managed identity is enabled and that SyncApi__Scope is api://<web app client id>/.default.", ex.GetType().Name);
                return new SyncOutcome(SyncOutcomeKind.ConfigurationError, attempt, "No access token could be obtained from the managed identity.");
            }

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(perAttempt);
                var reply = await api.PostSyncAsync(token, timeout.Token);
                var outcome = Classify(reply, attempt, out var retry, out lastProblem);
                if (outcome is not null) return Log(outcome, reply);
                if (!retry) break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                lastProblem = $"no answer within {o.TimeoutSeconds} seconds";
            }
            catch (HttpRequestException ex)
            {
                lastProblem = $"the web app could not be reached ({ex.HttpRequestError})";
            }

            if (attempt == o.MaxAttempts) break;
            if (time.GetElapsedTime(started) + backoff + perAttempt > TimeSpan.FromSeconds(o.TotalBudgetSeconds))
            {
                logger.LogWarning("Not retrying: another attempt would not fit in the {Budget}s budget.", o.TotalBudgetSeconds);
                break;
            }
            logger.LogWarning("Attempt {Attempt} of {Max} failed: {Problem}. Retrying in {Delay}s (the web app may be waking up).", attempt, o.MaxAttempts, lastProblem, (int)backoff.TotalSeconds);
            await delayer.DelayAsync(backoff, ct);
            backoff *= 2;
        }

        logger.LogError("The sync could not be started: {Problem}. The next scheduled run will try again.", lastProblem);
        return new SyncOutcome(SyncOutcomeKind.Unavailable, o.MaxAttempts, $"The web app did not take the sync request: {lastProblem}.");
    }

    /// <summary>Returns the final outcome, or null when the attempt should be retried (<paramref name="retry"/>) or the loop ended (not retry).</summary>
    private static SyncOutcome? Classify(SyncApiReply r, int attempt, out bool retry, out string problem)
    {
        retry = false;
        problem = $"HTTP {(int)r.StatusCode}";
        switch (r.StatusCode)
        {
            case HttpStatusCode.OK:
                return r.Status switch
                {
                    "Succeeded" => new SyncOutcome(SyncOutcomeKind.Succeeded, attempt, "Sync succeeded."),
                    "PartialFailure" => new SyncOutcome(SyncOutcomeKind.PartialFailure, attempt, "Sync finished with some failed steps."),
                    "Failed" => new SyncOutcome(SyncOutcomeKind.SyncFailed, attempt, "The sync ran but failed."),
                    _ => new SyncOutcome(SyncOutcomeKind.Succeeded, attempt, "Sync request accepted (no summary status)."),
                };
            case HttpStatusCode.Accepted:
                return new SyncOutcome(SyncOutcomeKind.StillRunning, attempt, "The sync is still running inside the web app.");
            case HttpStatusCode.Conflict:
                return new SyncOutcome(SyncOutcomeKind.AlreadyRunning, attempt, "A sync was already running.");
            case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                return new SyncOutcome(SyncOutcomeKind.ConfigurationError, attempt,
                    $"The web app refused the call (HTTP {(int)r.StatusCode}). Configuration error: check the app role Sync.Run assignment, SyncApi__Scope and the web app's Sync__AllowedCallerObjectId.");
            case HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout:
                retry = true;
                return null;
            default:
                return new SyncOutcome(SyncOutcomeKind.UnexpectedResponse, attempt, $"Unexpected answer from the web app: HTTP {(int)r.StatusCode}.");
        }
    }

    private SyncOutcome Log(SyncOutcome outcome, SyncApiReply reply)
    {
        var steps = reply.StepSummaries.Count == 0 ? "" : " Steps: " + string.Join(", ", reply.StepSummaries) + ".";
        var duration = reply.DurationMs is { } ms ? $" Took {ms / 1000.0:0.#}s." : "";
        switch (outcome.Kind)
        {
            case SyncOutcomeKind.Succeeded:
                logger.LogInformation("{Message} Attempts: {Attempts}.{Duration}{Steps}", outcome.Message, outcome.Attempts, duration, steps); break;
            case SyncOutcomeKind.AlreadyRunning:
                logger.LogInformation("{Message} Treated as success.", outcome.Message); break;
            case SyncOutcomeKind.PartialFailure or SyncOutcomeKind.StillRunning:
                logger.LogWarning("{Message}{Duration}{Steps}", outcome.Message, duration, steps); break;
            default:
                logger.LogError("{Message}{Duration}{Steps}", outcome.Message, duration, steps); break;
        }
        return outcome;
    }
}
