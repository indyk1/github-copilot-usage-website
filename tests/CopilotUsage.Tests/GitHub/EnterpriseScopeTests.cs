using System.Net;
using CopilotUsage.Core.Models;
using CopilotUsage.GitHub;
using CopilotUsage.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.GitHub;

/// <summary>Enterprise scope and failure messages. All fixtures and response bodies are synthetic.</summary>
public class EnterpriseScopeTests
{
    private const string Token = "ghp_TESTSECRETTOKEN0123456789";
    private static readonly GitHubScope Org = new(GitHubScopeKind.Organization, "acme");
    private static readonly GitHubScope Ent = new(GitHubScopeKind.Enterprise, "big-corp");

    private sealed class Capture : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string c) => new L(this);
        public void Dispose() { }
        private sealed class L(Capture o) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel l) => true;
            public void Log<TState>(LogLevel l, EventId id, TState s, Exception? ex, Func<TState, Exception?, string> f)
            { lock (o.Lines) o.Lines.Add($"{l}: {f(s, ex)}"); }
        }
    }

    private static (IGitHubClient Client, StubHandler Api, Capture Logs) Build(
        GitHubOptions opts, Func<HttpRequestMessage, HttpResponseMessage> api)
    {
        var apiStub = new StubHandler(api);
        var logs = new Capture();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Information));
        opts.Token = Token;
        services.AddSingleton(Options.Create(opts));
        services.AddGitHubClient(o => { o.Retry.Delay = TimeSpan.Zero; o.Retry.MaxRetryAttempts = 1; });
        services.AddHttpClient<IGitHubClient, GitHubClient>().ConfigurePrimaryHttpMessageHandler(() => apiStub);
        return (services.BuildServiceProvider().GetRequiredService<IGitHubClient>(), apiStub, logs);
    }

    private static GitHubOptions EntOptions(int pageSize = 100) => new() { Enterprise = "big-corp", PageSize = pageSize };
    private static GitHubOptions OrgOptions() => new() { Org = "acme" };

    // ---- scope resolution ----

    [Fact]
    public void Enterprise_wins_when_both_are_set_and_demo_is_always_an_organization()
    {
        var both = new GitHubOptions { Org = "acme", Enterprise = " big-corp " };
        Assert.True(GitHubScope.BothSet(both));
        Assert.Equal(new GitHubScope(GitHubScopeKind.Enterprise, "big-corp"), GitHubScope.From(both));
        Assert.Equal(GitHubScopeKind.Organization, GitHubScope.Resolve(both, demo: true).Kind);
        Assert.Equal(new GitHubScope(GitHubScopeKind.Organization, "acme"), GitHubScope.From(new GitHubOptions { Org = "acme" }));
        Assert.Equal("enterprise", Ent.Noun);
        Assert.Equal("organization", Org.Noun);
    }

    // ---- endpoint paths ----

    [Fact]
    public void Endpoint_paths_for_both_scopes()
    {
        Assert.Equal("/orgs/acme/copilot/billing", GitHubEndpoints.SeatSummary("acme"));
        Assert.Equal("/orgs/acme/copilot/billing/seats?per_page=100&page=2", GitHubEndpoints.Seats(Org, 2, 100));
        Assert.Equal("/enterprises/big-corp/copilot/billing/seats?per_page=50&page=1", GitHubEndpoints.Seats(Ent, 1, 50));

        var q = new CreditUsageQuery(2026, 11, 9, "a b", "GPT-5.2");
        Assert.Equal("/organizations/acme/settings/billing/ai_credit/usage?year=2026&month=11&day=9&user=a%20b&model=GPT-5.2", GitHubEndpoints.CreditUsage(Org, q));
        Assert.Equal("/enterprises/big-corp/settings/billing/ai_credit/usage?year=2026&month=11&day=9&user=a%20b&model=GPT-5.2", GitHubEndpoints.CreditUsage(Ent, q));
        Assert.Equal("/enterprises/big-corp/settings/billing/ai_credit/usage", GitHubEndpoints.CreditUsage(Ent, new CreditUsageQuery(null, null, null)));

        Assert.Equal("/orgs/acme/copilot/metrics/reports/users-28-day/latest", GitHubEndpoints.UserMetricsLatest28Day(Org));
        Assert.Equal("/enterprises/big-corp/copilot/metrics/reports/users-28-day/latest", GitHubEndpoints.UserMetricsLatest28Day(Ent));
        Assert.Equal("/orgs/acme/copilot/metrics/reports/users-1-day?day=2026-11-09", GitHubEndpoints.UserMetricsForDay(Org, new DateOnly(2026, 11, 9)));
        Assert.Equal("/enterprises/big-corp/copilot/metrics/reports/users-1-day?day=2026-11-09", GitHubEndpoints.UserMetricsForDay(Ent, new DateOnly(2026, 11, 9)));
        Assert.Throws<InvalidOperationException>(() => GitHubEndpoints.Seats(new GitHubScope(GitHubScopeKind.Enterprise, null), 1, 1));
    }

    // ---- client ----

    [Fact]
    public async Task Enterprise_seats_paginate_and_a_user_in_two_organizations_is_one_seat()
    {
        var (client, api, _) = Build(EntOptions(pageSize: 2), r => r.RequestUri!.Query.EndsWith("&page=1")
            ? StubHandler.Json(Fixture.Text("enterprise_seats_page1.json"))
            : r.RequestUri.Query.EndsWith("&page=2") ? StubHandler.Json(Fixture.Text("enterprise_seats_page2.json"))
            : StubHandler.Json("{\"total_seats\":3,\"seats\":[]}"));

        var list = await client.GetSeatListAsync();

        Assert.Equal(3, list.ReportedTotal);
        Assert.Equal(["octo-alice", "octo-bob", "octo-carol"], list.Seats.Select(s => s.Login).Order());
        var alice = list.Seats.Single(s => s.Login == "octo-alice");
        Assert.Equal("org-a, org-b", alice.Organization);
        Assert.Equal("platform", alice.AssigningTeam);
        Assert.Equal("org-a", list.Seats.Single(s => s.Login == "octo-bob").Organization);
        Assert.Equal(3, api.Requests.Count);
        Assert.All(api.Requests, r => Assert.StartsWith("/enterprises/big-corp/copilot/billing/seats", r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Enterprise_summary_is_derived_from_the_seats_and_no_summary_endpoint_is_called()
    {
        var (client, api, _) = Build(EntOptions(pageSize: 2), r => r.RequestUri!.Query.EndsWith("&page=1")
            ? StubHandler.Json(Fixture.Text("enterprise_seats_page1.json")) : StubHandler.Json(Fixture.Text("enterprise_seats_page2.json")));

        var summary = await client.GetSeatSummaryAsync();

        Assert.Equal(3, summary.TotalSeats);
        Assert.DoesNotContain(api.Requests, r => r.Uri.AbsolutePath.EndsWith("/copilot/billing"));
        Assert.Equal("business", summary.PlanType);
        Assert.Null(summary.PendingInvitation);
        Assert.Null(summary.PendingCancellation);
    }

    [Fact]
    public void Derived_summary_counts_this_months_additions_and_activity_and_leaves_the_rest_unavailable()
    {
        var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var seats = new List<SeatInfo>
        {
            new("a", null, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), null, "business", null, new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), null),
            new("b", null, new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero), null, "enterprise", null, new DateTimeOffset(2026, 10, 31, 23, 0, 0, TimeSpan.Zero), null),
            new("c", null, null, null, null, null, null, null),
        };
        var s = GitHubMappings.DeriveSeatSummary(seats, now);
        Assert.Equal(3, s.TotalSeats);
        Assert.Equal(1, s.AddedThisCycle);
        Assert.Equal(1, s.ActiveThisCycle);
        Assert.Equal(2, s.InactiveThisCycle);
        Assert.Equal("business+enterprise", s.PlanType);
        Assert.Null(s.PendingInvitation);
        Assert.Null(s.PendingCancellation);
        Assert.Null(GitHubMappings.DeriveSeatSummary([], now).PlanType);
    }

    [Fact]
    public async Task Enterprise_credit_usage_and_metrics_use_the_enterprise_paths()
    {
        var (client, api, _) = Build(EntOptions(), r => r.RequestUri!.AbsolutePath.Contains("metrics")
            ? StubHandler.Json("{\"download_links\":[]}") : StubHandler.Json(Fixture.Text("ai_credit_usage_day.json")));

        var records = await client.GetCreditUsageAsync(new CreditUsageQuery(2026, 11, 9, "octo-alice"));
        await client.GetUserMetricsAsync();
        await client.GetUserMetricsAsync(new DateOnly(2026, 11, 9));

        Assert.Equal(3, records.Count);
        Assert.Equal("/enterprises/big-corp/settings/billing/ai_credit/usage", api.Requests[0].Uri.AbsolutePath);
        Assert.Equal("?year=2026&month=11&day=9&user=octo-alice", api.Requests[0].Uri.Query);
        Assert.Equal("/enterprises/big-corp/copilot/metrics/reports/users-28-day/latest", api.Requests[1].Uri.AbsolutePath);
        Assert.Equal("/enterprises/big-corp/copilot/metrics/reports/users-1-day", api.Requests[2].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Org_scope_still_uses_the_org_paths_when_only_the_org_is_set()
    {
        var (client, api, _) = Build(OrgOptions(), _ => StubHandler.Json(Fixture.Text("seat_billing.json")));
        await client.GetSeatSummaryAsync();
        Assert.Equal("/orgs/acme/copilot/billing", api.Requests[0].Uri.AbsolutePath);
    }

    // ---- failure messages ----

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Org_scope_seat_and_billing_failures_hint_at_an_enterprise(int status)
    {
        var (client, _, logs) = Build(OrgOptions(), _ => StubHandler.Json("{\"message\":\"Resource not accessible by personal access token\"}", (HttpStatusCode)status));

        var calls = new Func<Task>[]
        {
            () => client.GetSeatSummaryAsync(),
            () => client.GetSeatsAsync(),
            () => client.GetCreditUsageAsync(new CreditUsageQuery(2026, 11, 1)),
        };
        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<GitHubApiException>(call);
            Assert.Equal(status, (int)ex.StatusCode!);
            Assert.Contains("GitHub:Enterprise", ex.Hint);
            Assert.Contains("billed through a GitHub Enterprise", ex.Hint);
            Assert.DoesNotContain(Token, ex.Message);
        }

        var all = string.Join("\n", logs.Lines);
        Assert.Contains("/orgs/acme/copilot/billing", all);                       // the path
        Assert.Contains($"HTTP {status}", all);                                    // the status
        Assert.Contains("Resource not accessible by personal access token", all);  // GitHub's message
        Assert.Contains("GitHub:Enterprise", all);                                 // the hint
        Assert.DoesNotContain(Token, all);
        Assert.DoesNotContain("Bearer", all);
    }

    [Fact]
    public async Task Org_scope_metrics_403_gets_no_enterprise_hint()
    {
        var (client, _, _) = Build(OrgOptions(), _ => StubHandler.Json("{\"message\":\"nope\"}", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetUserMetricsAsync());
        Assert.DoesNotContain("GitHub:Enterprise", ex.Hint);
    }

    [Fact]
    public async Task Enterprise_403_hints_at_token_type_and_scopes_and_404_at_the_slug()
    {
        var (client, _, logs) = Build(EntOptions(), r => r.RequestUri!.AbsolutePath.Contains("settings")
            ? StubHandler.Json("{\"message\":\"Must have admin rights\"}", HttpStatusCode.Forbidden)
            : StubHandler.Json("{\"message\":\"Not Found\"}", HttpStatusCode.NotFound));

        var forbidden = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetCreditUsageAsync(new CreditUsageQuery(2026, 11, 1, "someone")));
        Assert.Contains("classic personal access token", forbidden.Hint);
        Assert.Contains("manage_billing:copilot", forbidden.Hint);
        Assert.Contains("read:enterprise", forbidden.Hint);
        Assert.Contains("SAML", forbidden.Hint);
        Assert.DoesNotContain("GitHub:Enterprise", forbidden.Hint);

        var notFound = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatsAsync());
        Assert.Contains("enterprise slug", notFound.Hint);

        var all = string.Join("\n", logs.Lines);
        Assert.Contains("/enterprises/big-corp/settings/billing/ai_credit/usage", all);
        Assert.DoesNotContain("someone", all);          // the query string (user filter) is never logged
        Assert.Contains("Must have admin rights", all);
        Assert.DoesNotContain(Token, all);
    }

    [Fact]
    public async Task A_token_echoed_in_the_body_is_removed_and_long_bodies_are_truncated()
    {
        var body = "{\"message\":\"bad " + Token + " " + new string('x', 1000) + "\"}";
        var (client, _, logs) = Build(OrgOptions(), _ => StubHandler.Json(body, HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatsAsync());

        Assert.DoesNotContain(Token, ex.Message);
        Assert.DoesNotContain(Token, ex.GitHubMessage);
        Assert.DoesNotContain(Token, string.Join("\n", logs.Lines));
        Assert.True(ex.GitHubMessage!.Length <= 303);
    }

    [Fact]
    public async Task Unauthorized_says_the_token_was_rejected()
    {
        var (client, _, _) = Build(EntOptions(), _ => StubHandler.Json("{\"message\":\"Bad credentials\"}", HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsAsync<GitHubApiException>(() => client.GetSeatsAsync());
        Assert.Contains("token was rejected", ex.Hint);
    }
}
