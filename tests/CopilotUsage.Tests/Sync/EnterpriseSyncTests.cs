using System.Net;
using CopilotUsage.Core.Models;
using CopilotUsage.Data.Entities;
using CopilotUsage.GitHub;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Config;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CopilotUsage.Tests.Sync;

/// <summary>Enterprise scope end to end against the fake client, the failure text a sync stores, and the configuration rules.</summary>
public class EnterpriseSyncTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly GitHubScope Ent = new(GitHubScopeKind.Enterprise, "big-corp");
    private static readonly GitHubScope Org = new(GitHubScopeKind.Organization, "acme");

    private static FakeGitHubClient FakeEnterprise() => new()
    {
        Seats =
        [
            new("alice", null, new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero), null, "business", null, new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), null, "org-a, org-b"),
            new("bob", null, new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), null, "business", null, null, null, "org-a"),
            new("carol", null, null, null, "business", null, null, null, "org-b"),
        ],
        ReportedTotal = 3,
    };

    [Fact]
    public async Task Enterprise_sync_runs_every_step_without_calling_the_summary_endpoint()
    {
        var fake = FakeEnterprise();
        await using var host = new TestHost(Now, github: fake, gitHubScope: Ent);

        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        Assert.Equal(SyncStatus.Succeeded, result.Status);
        Assert.Equal(0, fake.SeatSummaryCalls);
        Assert.Equal(1, fake.SeatListCalls);

        await host.WithDbAsync(async db =>
        {
            var snap = await db.SeatSnapshots.SingleAsync();
            Assert.Equal(3, snap.TotalSeats);
            Assert.Equal(1, snap.AddedThisCycle);
            Assert.Equal(1, snap.ActiveThisCycle);
            Assert.Equal(2, snap.InactiveThisCycle);
            Assert.Null(snap.PendingInvitation);
            Assert.Null(snap.PendingCancellation);
            Assert.Equal("org-a, org-b", (await db.Seats.SingleAsync(s => s.Login == "alice")).Organization);
            Assert.Equal(11 * 2, await db.CreditUsageDaily.CountAsync());
            Assert.Equal(3 * 2, await db.UserModelMonthly.CountAsync());
            return 0;
        });

        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync());
        Assert.Equal(3, home.Pool.Seats);
        Assert.Equal(3 * 1900m, home.Pool.Limit);
    }

    [Fact]
    public async Task Enterprise_sync_is_idempotent()
    {
        await using var host = new TestHost(Now, github: FakeEnterprise(), gitHubScope: Ent);
        await host.Sync.RunAsync(SyncTrigger.Manual);
        var first = await host.SnapshotAsync();
        await host.Sync.RunAsync(SyncTrigger.Manual);
        Assert.Equal(first, await host.SnapshotAsync());
    }

    [Fact]
    public async Task An_empty_enterprise_seat_list_with_a_reported_total_is_a_failed_step_and_keeps_the_old_seats()
    {
        var fake = FakeEnterprise();
        await using var host = new TestHost(Now, github: fake, gitHubScope: Ent);
        await host.Sync.RunAsync(SyncTrigger.Manual);

        fake.Seats = [];
        fake.ReportedTotal = 3;
        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        Assert.Equal(SyncStatus.Failed, result.Steps.First(s => s.Step == "Seats").Status);
        await host.WithDbAsync(async db => { Assert.Equal(3, await db.Seats.CountAsync(s => s.IsActive)); return 0; });
    }

    [Theory]
    [InlineData(true, 403, "classic personal access token")]
    [InlineData(true, 404, "enterprise slug")]
    [InlineData(false, 403, "GitHub:Enterprise")]
    [InlineData(false, 404, "GitHub:Enterprise")]
    public async Task The_stored_failure_names_the_step_and_status_and_leaks_neither_path_body_nor_token(bool enterprise, int status, string hintPart)
    {
        var scope = enterprise ? Ent : Org;
        var path = enterprise ? "/enterprises/big-corp/copilot/billing/seats" : "/orgs/acme/copilot/billing";
        var code = (HttpStatusCode)status;
        var hint = GitHubFailureHints.For(scope, GitHubCall.Seats, code);
        var fake = new FakeGitHubClient { BeforeSeats = () => throw new GitHubApiException(
            $"GET {path} failed: {status}. GitHub said: SECRET-BODY ghp_TOKENVALUE", code, path, hint: hint, gitHubMessage: "SECRET-BODY ghp_TOKENVALUE") };
        await using var host = new TestHost(Now, github: fake, gitHubScope: scope);

        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        var run = await host.WithDbAsync(db => db.SyncRuns.AsNoTracking().SingleAsync());
        Assert.Contains("Seats step", run.Error);
        Assert.Contains($"HTTP {status}", run.Error);
        Assert.Contains(hintPart, run.Error);
        foreach (var text in new[] { run.Error!, run.StepsJson!, result.Steps.First(s => s.Step == "Seats").Error! })
        {
            Assert.DoesNotContain("SECRET-BODY", text);
            Assert.DoesNotContain("ghp_", text);
            Assert.DoesNotContain("big-corp", text);
            Assert.DoesNotContain("acme", text);
            Assert.DoesNotContain("/copilot/", text);
        }
        Assert.Contains("Seats", run.StepsJson);
    }

    // ---- configuration ----

    private static IReadOnlyList<string> Validate(params (string Key, string? Value)[] values) =>
        StartupConfigValidator.Validate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Data Source=x.db", ["Auth:Enabled"] = "false" }
                .Concat(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).ToDictionary())
            .Build(), isDevelopment: true);

    [Fact]
    public void Neither_enterprise_nor_org_is_an_error_that_names_both()
    {
        var errors = Validate(("GitHub:Token", "t"));
        var e = Assert.Single(errors);
        Assert.Contains("GitHub:Enterprise", e);
        Assert.Contains("GitHub:Org", e);
        Assert.Contains("enterprise is used", e);
    }

    [Fact]
    public void Either_one_alone_is_valid()
    {
        Assert.Empty(Validate(("GitHub:Token", "t"), ("GitHub:Enterprise", "big-corp")));
        Assert.Empty(Validate(("GitHub:Token", "t"), ("GitHub:Org", "acme")));
    }

    [Fact]
    public void Both_set_is_valid_because_the_enterprise_simply_wins() =>
        Assert.Empty(Validate(("GitHub:Token", "t"), ("GitHub:Enterprise", "big-corp"), ("GitHub:Org", "acme")));

    [Fact]
    public void Whitespace_enterprise_counts_as_missing_and_urls_are_refused()
    {
        Assert.Single(Validate(("GitHub:Token", "t"), ("GitHub:Enterprise", "  ")));
        Assert.Contains(Validate(("GitHub:Token", "t"), ("GitHub:Enterprise", "https://github.com/enterprises/x")), e => e.Contains("slug"));
    }

    [Fact]
    public void Demo_mode_needs_neither() => Assert.Empty(Validate(("Demo:Enabled", "true")));
}
