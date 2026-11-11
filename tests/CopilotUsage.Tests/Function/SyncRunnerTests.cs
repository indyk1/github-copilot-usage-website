using System.Net;
using CopilotUsage.SyncFunction;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CopilotUsage.Tests.Function;

internal sealed class FakeTokens(string? token = "SECRET-TOKEN-VALUE") : ISyncTokenProvider
{
    public Exception? Throw { get; set; }
    public int Calls { get; private set; }
    public Task<string?> GetTokenAsync(CancellationToken ct)
    {
        Calls++;
        return Throw is null ? Task.FromResult(token) : Task.FromException<string?>(Throw);
    }
}

/// <summary>Plays back a script of answers: a reply, or an exception to throw.</summary>
internal sealed class FakeApi(params object[] script) : ISyncApiClient
{
    private readonly Queue<object> _script = new(script);
    public List<string?> TokensSeen { get; } = [];
    public Func<CancellationToken, Task>? Hang { get; set; }

    public static SyncApiReply Reply(HttpStatusCode code, string? status = null) => new(code, status, 1500, []);

    public async Task<SyncApiReply> PostSyncAsync(string? bearerToken, CancellationToken ct)
    {
        TokensSeen.Add(bearerToken);
        if (Hang is not null) await Hang(ct);
        var next = _script.Count > 0 ? _script.Dequeue() : throw new InvalidOperationException("script exhausted");
        return next switch { Exception e => throw e, SyncApiReply r => r, _ => throw new InvalidOperationException() };
    }
}

internal sealed class FakeDelayer(FakeTimeProvider time) : IDelayer
{
    public List<TimeSpan> Delays { get; } = [];
    public Task DelayAsync(TimeSpan delay, CancellationToken ct) { Delays.Add(delay); time.Advance(delay); return Task.CompletedTask; }
}

internal sealed class CaptureLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
}

public class SyncRunnerTests
{
    private sealed record Rig(SyncRunner Runner, FakeTokens Tokens, FakeApi Api, FakeDelayer Delayer, CaptureLogger<SyncRunner> Log);

    private static Rig Make(object[] script, Action<SyncApiOptions>? tweak = null, FakeTokens? tokens = null)
    {
        var time = new FakeTimeProvider();
        var opts = new SyncApiOptions { BaseUrl = "https://app.example.test/", TimeoutSeconds = 230, MaxAttempts = 3, InitialBackoffSeconds = 15 };
        tweak?.Invoke(opts);
        tokens ??= new FakeTokens();
        var api = new FakeApi(script);
        var delayer = new FakeDelayer(time);
        var log = new CaptureLogger<SyncRunner>();
        return new Rig(new SyncRunner(tokens, api, delayer, time, Options.Create(opts), log), tokens, api, delayer, log);
    }

    private static SyncApiReply Ok(string status = "Succeeded") => FakeApi.Reply(HttpStatusCode.OK, status);
    private static SyncApiReply Code(HttpStatusCode c) => FakeApi.Reply(c);

    [Fact]
    public async Task Success_on_the_first_try_sends_the_token_and_does_not_wait()
    {
        var r = Make([Ok()]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.Succeeded, o.Kind);
        Assert.False(o.IsFailure);
        Assert.Equal(1, o.Attempts);
        Assert.Equal(["SECRET-TOKEN-VALUE"], r.Api.TokensSeen);
        Assert.Empty(r.Delayer.Delays);
    }

    [Fact]
    public async Task A_cold_start_is_ridden_out_with_growing_waits_then_succeeds()
    {
        var r = Make([Code(HttpStatusCode.ServiceUnavailable), new HttpRequestException("connection reset"), Ok()]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.Succeeded, o.Kind);
        Assert.Equal(3, o.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30)], r.Delayer.Delays);
        Assert.Equal(3, r.Tokens.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Transient_statuses_are_retried(HttpStatusCode code)
    {
        var r = Make([Code(code), Ok()]);
        Assert.Equal(SyncOutcomeKind.Succeeded, (await r.Runner.RunAsync(default)).Kind);
        Assert.Single(r.Delayer.Delays);
    }

    [Fact]
    public async Task A_timeout_is_retried()
    {
        var r = Make([new TaskCanceledException("timed out"), Ok()]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.Succeeded, o.Kind);
        Assert.Equal(2, o.Attempts);
    }

    [Fact]
    public async Task An_attempt_that_hangs_is_cut_off_at_the_configured_timeout_and_retried()
    {
        var r = Make([Ok()], o => o.TimeoutSeconds = 1);
        var first = true;
        r.Api.Hang = async ct =>
        {
            if (first) { first = false; await Task.Delay(Timeout.Infinite, ct); }
        };
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.Succeeded, o.Kind);
        Assert.Equal(2, o.Attempts);
    }

    [Fact]
    public async Task Giving_up_after_the_last_attempt_is_reported_as_unavailable_and_a_failure()
    {
        var r = Make([Code(HttpStatusCode.ServiceUnavailable), Code(HttpStatusCode.ServiceUnavailable), Code(HttpStatusCode.BadGateway)]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.Unavailable, o.Kind);
        Assert.True(o.IsFailure);
        Assert.Equal(2, r.Delayer.Delays.Count);
        Assert.Contains(r.Log.Lines, l => l.StartsWith("Error") && l.Contains("next scheduled run"));
    }

    [Fact]
    public async Task A_sync_already_running_is_success_and_is_not_retried()
    {
        var r = Make([Code(HttpStatusCode.Conflict)]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.AlreadyRunning, o.Kind);
        Assert.False(o.IsFailure);
        Assert.Empty(r.Delayer.Delays);
    }

    [Fact]
    public async Task Accepted_means_the_sync_carries_on_in_the_web_app_and_is_not_a_failure()
    {
        var o = await Make([Code(HttpStatusCode.Accepted)]).Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.StillRunning, o.Kind);
        Assert.False(o.IsFailure);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refusals_are_a_configuration_error_and_are_not_retried(HttpStatusCode code)
    {
        var r = Make([Code(code), Ok()]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.ConfigurationError, o.Kind);
        Assert.True(o.IsFailure);
        Assert.Contains("Configuration error", o.Message);
        Assert.Contains("Sync.Run", o.Message);
        Assert.Single(r.Api.TokensSeen);
        Assert.Empty(r.Delayer.Delays);
    }

    [Fact]
    public async Task No_token_from_the_managed_identity_is_a_configuration_error_and_the_web_app_is_not_called()
    {
        var tokens = new FakeTokens { Throw = new InvalidOperationException("identity endpoint said SECRET-TOKEN-VALUE") };
        var r = Make([Ok()], tokens: tokens);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.ConfigurationError, o.Kind);
        Assert.Empty(r.Api.TokensSeen);
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("SECRET-TOKEN-VALUE"));
    }

    [Fact]
    public async Task A_sync_that_ran_and_failed_is_reported_as_a_failure_without_retrying_but_a_partial_one_is_not()
    {
        var failed = Make([Ok("Failed")]);
        var f = await failed.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.SyncFailed, f.Kind);
        Assert.True(f.IsFailure);
        Assert.Single(failed.Api.TokensSeen);

        var partial = await Make([Ok("PartialFailure")]).Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.PartialFailure, partial.Kind);
        Assert.False(partial.IsFailure);
    }

    [Fact]
    public async Task An_unexpected_status_is_reported_and_not_retried()
    {
        var r = Make([Code(HttpStatusCode.NotFound)]);
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.UnexpectedResponse, o.Kind);
        Assert.True(o.IsFailure);
    }

    [Fact]
    public async Task Retries_stop_when_another_attempt_would_not_fit_the_budget()
    {
        var r = Make([Code(HttpStatusCode.ServiceUnavailable), Ok()], o => o.TotalBudgetSeconds = 100); // 15s wait + 230s attempt > 100s
        var o = await r.Runner.RunAsync(default);
        Assert.Equal(SyncOutcomeKind.Unavailable, o.Kind);
        Assert.Empty(r.Delayer.Delays);
    }

    [Fact]
    public async Task Nothing_the_runner_logs_contains_the_token()
    {
        var r = Make([Code(HttpStatusCode.ServiceUnavailable), Ok()]);
        await r.Runner.RunAsync(default);
        Assert.NotEmpty(r.Log.Lines);
        Assert.DoesNotContain(r.Log.Lines, l => l.Contains("SECRET-TOKEN-VALUE"));
    }

    [Fact]
    public async Task Cancellation_by_the_host_is_not_swallowed()
    {
        var r = Make([new TaskCanceledException()]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        r.Api.Hang = ct => Task.FromCanceled(ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Runner.RunAsync(cts.Token));
    }

    [Fact]
    public async Task Without_a_scope_for_local_runs_no_token_is_sent()
    {
        var r = Make([Ok()], tokens: new FakeTokens(token: null));
        Assert.Equal(SyncOutcomeKind.Succeeded, (await r.Runner.RunAsync(default)).Kind);
        Assert.Equal<string?[]>([null], r.Api.TokensSeen.ToArray());
    }

    [Fact]
    public void Settings_are_checked_and_named()
    {
        Assert.Empty(new SyncApiOptions { BaseUrl = "https://x.azurewebsites.net" }.Validate());
        Assert.Contains("SyncApi__BaseUrl", Assert.Single(new SyncApiOptions().Validate()));
        Assert.Single(new SyncApiOptions { BaseUrl = "http://x.example.com" }.Validate());   // plain http to a remote host is refused
        Assert.Empty(new SyncApiOptions { BaseUrl = "http://localhost:5103" }.Validate());   // local runs are fine
        Assert.Single(new SyncApiOptions { BaseUrl = "https://x", MaxAttempts = 0 }.Validate());
    }
}

public class SyncTimerFunctionTests
{
    private static SyncTimerFunction Make(params object[] script)
    {
        var time = new FakeTimeProvider();
        var runner = new SyncRunner(new FakeTokens(), new FakeApi(script), new FakeDelayer(time), time,
            Options.Create(new SyncApiOptions { BaseUrl = "https://app.example.test/", MaxAttempts = 1 }), new CaptureLogger<SyncRunner>());
        return new SyncTimerFunction(runner, new CaptureLogger<SyncTimerFunction>());
    }

    [Fact]
    public async Task A_good_run_completes_quietly() =>
        await Make(FakeApi.Reply(HttpStatusCode.OK, "Succeeded")).Run(new TimerInfo(), default);

    [Fact]
    public async Task A_run_that_did_not_sync_throws_so_the_host_marks_it_failed()
    {
        var ex = await Assert.ThrowsAsync<SyncRunFailedException>(() => Make(FakeApi.Reply(HttpStatusCode.Forbidden)).Run(new TimerInfo(), default));
        Assert.Contains("Configuration error", ex.Message);
    }

    [Fact]
    public async Task Already_running_is_not_a_failure() =>
        await Make(FakeApi.Reply(HttpStatusCode.Conflict)).Run(new TimerInfo { IsPastDue = true }, default);

    [Fact]
    public void The_schedule_comes_from_an_app_setting_and_runs_nothing_at_startup()
    {
        var method = typeof(SyncTimerFunction).GetMethod(nameof(SyncTimerFunction.Run))!;
        var trigger = (TimerTriggerAttribute)method.GetParameters()[0].GetCustomAttributes(typeof(TimerTriggerAttribute), false).Single();
        Assert.Equal("%SyncSchedule%", trigger.Schedule);
        Assert.False(trigger.RunOnStartup);
    }
}
