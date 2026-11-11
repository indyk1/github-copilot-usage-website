using System.Diagnostics;
using CopilotUsage.Data.Entities;
using CopilotUsage.Web.Auth;
using CopilotUsage.Web.Config;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Web.Sync;

/// <summary>What POST /api/sync returns. No error text, no token, no organization name.</summary>
public sealed record SyncApiStep(string Step, string Status, int Rows, int Errors);

/// <param name="Status">Succeeded, PartialFailure or Failed (finished); Running (accepted, still going); AlreadyRunning (nothing started); Error.</param>
public sealed record SyncApiResponse(string Status, int DurationMs, int? RunId, IReadOnlyList<SyncApiStep> Steps);

public static class SyncEndpoint
{
    public const string Path = "/api/sync";

    public static IEndpointRouteBuilder MapSyncApi(this IEndpointRouteBuilder app, bool authEnabled)
    {
        var post = app.MapPost(Path, RunAsync).DisableAntiforgery().WithName("RunSync");
        // Development without sign-in is the only way this is open; the validator refuses that setup anywhere else.
        if (authEnabled) post.RequireAuthorization(AuthSetup.SyncPolicy); else post.AllowAnonymous();

        // A plain, anonymous 405 for every other verb, so a probe (or a mistaken GET) never reaches the login redirect or the sync.
        app.MapMethods(Path, ["GET", "HEAD", "PUT", "PATCH", "DELETE", "OPTIONS"], (HttpContext ctx) =>
        {
            ctx.Response.Headers.Allow = "POST";
            return Results.StatusCode(StatusCodes.Status405MethodNotAllowed);
        }).AllowAnonymous().DisableAntiforgery();
        return app;
    }

    /// <summary>
    /// Starts a sync and waits for it, but only as long as the request can live. If it finishes in time the answer is 200 with the
    /// outcome. If not, the answer is 202 and the sync carries on: it is tied to the application's lifetime, not to this request,
    /// so App Service cutting the connection (about 230 s) does not cancel it. A sync that is already running is never joined or
    /// doubled: the answer is 409 straight away.
    /// </summary>
    private static async Task<IResult> RunAsync(
        HttpContext ctx, ISyncService sync, IOptions<SyncOptions> options, IHostApplicationLifetime lifetime,
        TimeProvider time, ILoggerFactory logs)
    {
        var logger = logs.CreateLogger("CopilotUsage.Web.SyncEndpoint");
        var started = time.GetTimestamp();
        int Ms() => (int)time.GetElapsedTime(started).TotalMilliseconds;

        if (sync.IsRunning)
        {
            logger.LogInformation("Sync call received while a sync is running; nothing started.");
            return Results.Json(new SyncApiResponse("AlreadyRunning", Ms(), null, []), statusCode: StatusCodes.Status409Conflict);
        }

        var run = Task.Run(() => sync.RunAsync(SyncTrigger.Api, lifetime.ApplicationStopping));
        // Whatever happens to the request, the outcome of the sync is always observed and logged.
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted) logger.LogError(t.Exception!.GetBaseException(), "Sync started from the API failed unexpectedly.");
        }, TaskScheduler.Default);

        try
        {
            var wait = Task.Delay(TimeSpan.FromSeconds(options.Value.RequestWaitSeconds), time, ctx.RequestAborted);
            if (await Task.WhenAny(run, wait) != run)
            {
                logger.LogInformation("Sync is still running after {Seconds}s; answering 202 and letting it finish in the background.", options.Value.RequestWaitSeconds);
                return Results.Json(new SyncApiResponse("Running", Ms(), null, []), statusCode: StatusCodes.Status202Accepted);
            }
            var result = await run;
            if (result.Skipped)
                return Results.Json(new SyncApiResponse("AlreadyRunning", Ms(), null, []), statusCode: StatusCodes.Status409Conflict);
            return Results.Json(new SyncApiResponse(result.Status.ToString(), Ms(), result.RunId,
                result.Steps.Select(s => new SyncApiStep(s.Step, s.Status.ToString(), s.Rows, s.Errors)).ToList()));
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("The caller went away before the sync finished; it continues in the background.");
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync started from the API failed unexpectedly.");
            return Results.Json(new SyncApiResponse("Error", Ms(), null, []), statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
