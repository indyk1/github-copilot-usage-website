using System.Net;
using CopilotUsage.GitHub;
using CopilotUsage.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.GitHub;

/// <summary>
/// The token must never reach a log line. These run the real HttpClient pipeline (logging and resilience handlers included)
/// with every log level on, which is stricter than any production setting, and search everything that was logged.
/// </summary>
public class LoggingSafetyTests
{
    private const string Token = "ghp_TESTSECRETTOKEN0123456789";

    private sealed class CaptureProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Capture(this, categoryName);
        public void Dispose() { }

        private sealed class Capture(CaptureProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            {
                Add($"scope: {state} {Describe(state)}");
                return null;
            }
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formatter) =>
                Add($"{level} {category}: {formatter(state, ex)} {Describe(state)} {ex}");
            private void Add(string s) { lock (owner.Lines) owner.Lines.Add(s); }
            private static string Describe(object? state) =>
                state is IEnumerable<KeyValuePair<string, object?>> kv ? string.Join(";", kv.Select(p => $"{p.Key}={p.Value}")) : "";
        }
    }

    private static (IGitHubClient Client, CaptureProvider Logs) Build(
        Func<HttpRequestMessage, HttpResponseMessage> api, Func<HttpRequestMessage, HttpResponseMessage>? download = null)
    {
        var logs = new CaptureProvider();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(Options.Create(new GitHubOptions { Org = "acme", Token = Token }));
        services.AddGitHubClient(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.MaxRetryAttempts = 2; });
        services.AddHttpClient<IGitHubClient, GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => new StubHandler(api));
        services.AddHttpClient(GitHubClient.DownloadClientName).ConfigurePrimaryHttpMessageHandler(
            () => new StubHandler(download ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        return (services.BuildServiceProvider().GetRequiredService<IGitHubClient>(), logs);
    }

    private static void AssertNoSecrets(CaptureProvider logs)
    {
        Assert.NotEmpty(logs.Lines); // the pipeline did log something, so this is a real search
        var all = string.Join("\n", logs.Lines);
        Assert.DoesNotContain(Token, all);
        Assert.DoesNotContain("ghp_", all);
        Assert.DoesNotContain("Bearer", all, StringComparison.OrdinalIgnoreCase);
        // At Trace level the framework names the header but replaces its value with "*"; anything else would be a leak.
        Assert.Empty(System.Text.RegularExpressions.Regex.Matches(all, @"Authorization:(?>\s*)(?!\*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.DoesNotContain("sig=", all); // signed report URLs carry a credential in the query string
    }

    [Fact]
    public async Task A_successful_call_logs_no_token_or_authorization_header()
    {
        var (client, logs) = Build(_ => StubHandler.Json(Fixture.Text("seat_billing.json")));
        await client.GetSeatSummaryAsync();
        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task Failures_and_retries_log_no_token_or_authorization_header()
    {
        var (client, logs) = Build(r => StubHandler.Json("{\"message\":\"Bad credentials\"}",
            r.RequestUri!.AbsolutePath.EndsWith("billing") ? HttpStatusCode.InternalServerError : HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatSummaryAsync());
        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatsAsync());
        AssertNoSecrets(logs);
    }

    [Fact]
    public async Task Fetching_a_signed_report_logs_neither_the_token_nor_the_signature()
    {
        var (client, logs) = Build(
            _ => StubHandler.Json(Fixture.Text("metrics_report_links.json")),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}\n") });
        await client.GetUserMetricsAsync();
        AssertNoSecrets(logs);
    }
}
