using System.Net;

namespace CopilotUsage.SyncFunction;

/// <summary>Gets an access token for the web app. The token is a secret: it is passed on, never logged.</summary>
public interface ISyncTokenProvider
{
    /// <summary>Returns a bearer token, or null when none is wanted (local use against a web app with sign-in off).</summary>
    Task<string?> GetTokenAsync(CancellationToken ct);
}

/// <summary>What the web app said. <paramref name="Status"/> is the summary's status field when a body could be read.</summary>
public sealed record SyncApiReply(HttpStatusCode StatusCode, string? Status, int? DurationMs, IReadOnlyList<string> StepSummaries);

/// <summary>One POST to the web app's sync endpoint. Throws HttpRequestException or TaskCanceledException on network trouble and timeouts.</summary>
public interface ISyncApiClient
{
    Task<SyncApiReply> PostSyncAsync(string? bearerToken, CancellationToken ct);
}

/// <summary>Waiting between attempts, replaceable so tests need not sleep.</summary>
public interface IDelayer
{
    Task DelayAsync(TimeSpan delay, CancellationToken ct);
}

public sealed class TaskDelayer : IDelayer
{
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
