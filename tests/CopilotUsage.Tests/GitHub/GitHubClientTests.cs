using System.Net;
using CopilotUsage.Core.Models;
using CopilotUsage.GitHub;
using CopilotUsage.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.GitHub;

public class GitHubClientTests
{
    private static (IGitHubClient Client, StubHandler Api, StubHandler Download) Build(
        Func<HttpRequestMessage, HttpResponseMessage> api,
        Func<HttpRequestMessage, HttpResponseMessage>? download = null,
        int pageSize = 100)
    {
        var apiStub = new StubHandler(api);
        var dlStub = new StubHandler(download ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(Options.Create(new GitHubOptions { Org = "acme", Token = "secret-token", PageSize = pageSize }));
        services.AddGitHubClient(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.MaxRetryAttempts = 3; });
        services.AddHttpClient<IGitHubClient, GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => apiStub);
        services.AddHttpClient(GitHubClient.DownloadClientName).ConfigurePrimaryHttpMessageHandler(() => dlStub);
        return (services.BuildServiceProvider().GetRequiredService<IGitHubClient>(), apiStub, dlStub);
    }

    private static string PathOf(HttpRequestMessage r) => r.RequestUri!.PathAndQuery;

    [Fact]
    public async Task Sends_required_headers_and_hits_the_seat_summary_path()
    {
        var (client, api, _) = Build(_ => StubHandler.Json(Fixture.Text("seat_billing.json")));
        var summary = await client.GetSeatSummaryAsync();

        Assert.Equal(25, summary.TotalSeats);
        var req = Assert.Single(api.Requests);
        Assert.Equal("/orgs/acme/copilot/billing", req.Uri.AbsolutePath);
        Assert.Equal("2026-03-10", Assert.Single(req.Headers.GetValues("X-GitHub-Api-Version")));
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("secret-token", req.Headers.Authorization.Parameter);
        Assert.Contains(req.Headers.Accept, a => a.MediaType == "application/vnd.github+json");
        Assert.NotEmpty(req.Headers.UserAgent);
    }

    [Fact]
    public async Task Seats_follow_pagination_until_a_short_page()
    {
        var (client, api, _) = Build(r => PathOf(r).Contains("page=1")
            ? StubHandler.Json(Fixture.Text("seats_page1.json"))
            : StubHandler.Json(Fixture.Text("seats_page2.json")), pageSize: 2);

        var seats = await client.GetSeatsAsync();

        Assert.Equal(["octo-alice", "octo-bob", "octo-carol"], seats.Select(s => s.Login));
        Assert.Equal(2, api.Requests.Count);
        Assert.Contains("per_page=2&page=1", api.Requests[0].Uri.Query);
        Assert.Contains("per_page=2&page=2", api.Requests[1].Uri.Query);
    }

    [Fact]
    public async Task Seats_stop_on_an_empty_first_page()
    {
        var (client, api, _) = Build(_ => StubHandler.Json("{\"total_seats\":0,\"seats\":[]}"));
        Assert.Empty(await client.GetSeatsAsync());
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Credit_usage_builds_the_filter_query_and_uses_the_organizations_path()
    {
        var (client, api, _) = Build(_ => StubHandler.Json(Fixture.Text("ai_credit_usage_day.json")));
        var records = await client.GetCreditUsageAsync(new CreditUsageQuery(2026, 11, 9, "octo alice", "GPT-5.2"));

        Assert.Equal(3, records.Count);
        var uri = api.Requests[0].Uri;
        Assert.Equal("/organizations/acme/settings/billing/ai_credit/usage", uri.AbsolutePath);
        Assert.Equal("?year=2026&month=11&day=9&user=octo%20alice&model=GPT-5.2", uri.Query);
    }

    [Fact]
    public async Task Credit_usage_without_filters_has_no_query_string()
    {
        var (client, api, _) = Build(_ => StubHandler.Json("{}"));
        Assert.Empty(await client.GetCreditUsageAsync(new CreditUsageQuery()));
        Assert.Equal("", api.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Day_filter_without_year_and_month_is_rejected_up_front()
    {
        var (client, api, _) = Build(_ => StubHandler.Json("{}"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetCreditUsageAsync(new CreditUsageQuery(Day: 3)));
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task Metrics_resolve_signed_urls_without_the_token_and_parse_the_ndjson()
    {
        var (client, api, download) = Build(
            _ => StubHandler.Json(Fixture.Text("metrics_report_links.json")),
            r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                r.RequestUri!.AbsolutePath.EndsWith("part-1.ndjson") ? Fixture.Text("metrics_users.ndjson")
                : "{\"user_login\":\"octo-carol\",\"day\":\"2026-11-10\",\"user_initiated_interaction_count\":4}\n") });

        var report = await client.GetUserMetricsAsync();

        Assert.Equal("/orgs/acme/copilot/metrics/reports/users-28-day/latest", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal(new DateOnly(2026, 10, 14), report.StartDay);
        Assert.Equal(new DateOnly(2026, 11, 10), report.EndDay);

        // 3 usable lines from part 1 (alice, bob, alice day 2) + 1 from part 2; the plain-http link is ignored.
        Assert.Equal(4, report.Users.Count);
        Assert.Equal(3, report.MalformedLines);  // truncated JSON, bare string, no-login object
        Assert.Equal(2, download.Requests.Count);
        Assert.All(download.Requests, r => Assert.Null(r.Headers.Authorization));
        Assert.Contains("sig=abc", download.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Metrics_for_one_day_use_the_single_day_path()
    {
        var (client, api, _) = Build(_ => StubHandler.Json("{\"download_links\":[]}"));
        var report = await client.GetUserMetricsAsync(new DateOnly(2026, 11, 9));
        Assert.Empty(report.Users);
        Assert.Equal("/orgs/acme/copilot/metrics/reports/users-1-day", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?day=2026-11-09", api.Requests[0].Uri.Query);
    }

    [Fact]
    public async Task Not_found_becomes_a_descriptive_exception_without_retrying()
    {
        var (client, api, _) = Build(_ => StubHandler.Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetUserMetricsAsync());
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Contains("policy", ex.Message);
        Assert.Single(api.Requests);
    }

    [Fact]
    public async Task Server_errors_are_retried_then_succeed()
    {
        var calls = 0;
        var (client, api, _) = Build(_ => ++calls < 3
            ? StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json(Fixture.Text("seat_billing.json")));

        Assert.Equal(25, (await client.GetSeatSummaryAsync()).TotalSeats);
        Assert.Equal(3, api.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_429_with_retry_after_is_retried()
    {
        var calls = 0;
        var (client, api, _) = Build(_ =>
        {
            if (++calls > 1) return StubHandler.Json(Fixture.Text("seat_billing.json"));
            var r = StubHandler.Json("{}", (HttpStatusCode)429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });

        await client.GetSeatSummaryAsync();
        Assert.Equal(2, api.Requests.Count);
    }

    [Fact]
    public async Task Secondary_rate_limit_403_with_retry_after_is_retried_but_plain_403_is_not()
    {
        var calls = 0;
        var (client, api, _) = Build(_ =>
        {
            if (++calls > 1) return StubHandler.Json(Fixture.Text("seat_billing.json"));
            var r = StubHandler.Json("{}", HttpStatusCode.Forbidden);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        });
        await client.GetSeatSummaryAsync();
        Assert.Equal(2, api.Requests.Count);

        var (client2, api2, _) = Build(_ => StubHandler.Json("{\"message\":\"Resource not accessible\"}", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client2.GetSeatSummaryAsync());
        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Single(api2.Requests);
    }

    [Fact]
    public async Task Persistent_server_errors_give_up_after_the_configured_retries()
    {
        var (client, api, _) = Build(_ => StubHandler.Json("{}", HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatSummaryAsync());
        Assert.Equal(4, api.Requests.Count); // 1 try + 3 retries
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_a_clear_error_not_a_crash()
    {
        var (client, _, _) = Build(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>oops</html>") });
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatSummaryAsync());
        Assert.Contains("not the expected JSON", ex.Message);
    }

    [Fact]
    public void Every_endpoint_path_is_defined_in_GitHubEndpoints()
    {
        Assert.Equal("/orgs/o%2Fx/copilot/billing", GitHubEndpoints.SeatSummary("o/x"));
        Assert.Equal("/orgs/acme/copilot/billing/seats?per_page=100&page=3", GitHubEndpoints.Seats(new GitHubScope(GitHubScopeKind.Organization, "acme"), 3, 100));
    }
}
