using CopilotUsage.Core.Models;
using CopilotUsage.Data.Entities;
using CopilotUsage.GitHub;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Sync;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Tests.Sync;

public class SyncServiceTests
{
    // 2026-11-11: day 11, so only the current month is synced (the previous month is re-checked on days 1-3).
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static UserDailyMetrics Metric(string login, int day, decimal credits, params MetricBreakdown[] extra) =>
        new(login, new DateOnly(2026, 11, day), credits, 10, 100, 40, 300, 60, 150, 30, true, false,
            [new MetricBreakdown(MetricDimension.Feature, "code_completion", 0, 100, 40, 100, 10, 0, 0, null),
             new MetricBreakdown(MetricDimension.Ide, "vscode", 10, 100, 40, 150, 30, 0, 0, "1.109.2"),
             .. extra]);

    private static FakeGitHubClient Fake() => new()
    {
        Metrics =
        [
            Metric("alice", 9, 40m), Metric("alice", 10, 25m),
            Metric("bob", 10, 5m, new MetricBreakdown(MetricDimension.Client, "claude-code", 0, 0, 0, 0, 0, 7, 70_000, null)),
        ],
    };

    [Fact]
    public async Task Running_twice_yields_the_same_rows()
    {
        await using var host = new TestHost(Now, github: Fake());

        var first = await host.Sync.RunAsync(SyncTrigger.Manual);
        var afterFirst = await host.SnapshotAsync();
        var second = await host.Sync.RunAsync(SyncTrigger.Scheduled);
        var afterSecond = await host.SnapshotAsync();

        Assert.Equal(SyncStatus.Succeeded, first.Status);
        Assert.Equal(SyncStatus.Succeeded, second.Status);
        Assert.Equal(afterFirst, afterSecond);

        await host.WithDbAsync(async db =>
        {
            Assert.Equal(3, await db.Seats.CountAsync());
            Assert.Equal(1, await db.SeatSnapshots.CountAsync());
            Assert.Equal(11 * 2, await db.CreditUsageDaily.CountAsync());   // 11 days x 2 models
            Assert.Equal(3 * 2, await db.UserModelMonthly.CountAsync());    // 3 users x 2 models
            Assert.Equal(3, await db.UserMetricDaily.CountAsync());
            Assert.Equal(3 * 2 + 1, await db.UserMetricBreakdown.CountAsync());
            Assert.Equal(2, await db.SyncRuns.CountAsync());                // runs are history, not upserted
            return 0;
        });
    }

    [Fact]
    public async Task Each_run_is_recorded_with_per_step_results()
    {
        await using var host = new TestHost(Now, github: Fake());
        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        Assert.Equal(["Seats", "OrgCreditsByDay", "UserCreditsByModel", "UserMetrics"], result.Steps.Select(s => s.Step));
        var run = await host.WithDbAsync(db => db.SyncRuns.SingleAsync());
        Assert.Equal(SyncStatus.Succeeded, run.Status);
        Assert.Equal(SyncTrigger.Manual, run.Trigger);
        Assert.False(run.IsDemo);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains("OrgCreditsByDay", run.StepsJson);
    }

    [Fact]
    public async Task Changed_upstream_values_update_in_place_and_vanished_rows_are_removed()
    {
        var fake = Fake();
        await using var host = new TestHost(Now, github: fake);
        await host.Sync.RunAsync(SyncTrigger.Manual);

        fake.DayCredits = new() { ["GPT-5.2"] = 99m };                  // Sonnet no longer reported
        fake.Seats.RemoveAll(s => s.Login == "carol");                  // carol lost her seat
        fake.Metrics = [Metric("alice", 10, 26m)];                      // alice's day 10 changed, bob's row not in this report
        await host.Sync.RunAsync(SyncTrigger.Manual);

        await host.WithDbAsync(async db =>
        {
            Assert.Equal(11, await db.CreditUsageDaily.CountAsync());
            Assert.All(await db.CreditUsageDaily.ToListAsync(), r => { Assert.Equal("GPT-5.2", r.Model); Assert.Equal(99m, r.Credits); });
            Assert.False((await db.Seats.SingleAsync(s => s.Login == "carol")).IsActive); // kept, deactivated
            Assert.Equal(3, await db.Seats.CountAsync());
            Assert.Equal(26m, (await db.UserMetricDaily.SingleAsync(m => m.UserLogin == "alice" && m.Day == new DateOnly(2026, 11, 10))).AiCredits);
            Assert.Equal(3, await db.UserMetricDaily.CountAsync()); // bob and alice's other day untouched
            Assert.Equal(2, await db.UserModelMonthly.CountAsync(m => m.UserLogin == "alice"));
            return 0;
        });
    }

    [Fact]
    public async Task First_days_of_a_month_also_refresh_the_previous_month()
    {
        await using var host = new TestHost(new DateTimeOffset(2026, 12, 2, 8, 0, 0, TimeSpan.Zero), github: Fake());
        await host.Sync.RunAsync(SyncTrigger.Scheduled);

        await host.WithDbAsync(async db =>
        {
            var months = await db.UserModelMonthly.Select(m => m.MonthStart).Distinct().ToListAsync();
            Assert.Equal([new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)], months.Order());
            Assert.Equal((30 + 2) * 2, await db.CreditUsageDaily.CountAsync());
            return 0;
        });
    }

    [Fact]
    public async Task A_failing_step_is_recorded_and_the_other_steps_still_run()
    {
        var fake = Fake();
        fake.FailMetrics = () => new GitHubApiException("boom: metrics policy disabled", System.Net.HttpStatusCode.NotFound, "/orgs/acme/copilot/metrics/reports/users-28-day/latest", hint: "Check the metrics policy.");
        await using var host = new TestHost(Now, github: fake);

        var result = await host.Sync.RunAsync(SyncTrigger.Manual); // must not throw

        Assert.Equal(SyncStatus.PartialFailure, result.Status);
        Assert.Equal(SyncStatus.Failed, result.Steps.Single(s => s.Step == "UserMetrics").Status);
        Assert.All(result.Steps.Where(s => s.Step != "UserMetrics"), s => Assert.Equal(SyncStatus.Succeeded, s.Status));

        var run = await host.WithDbAsync(db => db.SyncRuns.SingleAsync());
        Assert.Equal(SyncStatus.PartialFailure, run.Status);
        Assert.Contains("UserMetrics step", run.Error);   // which step
        Assert.Contains("HTTP 404", run.Error);           // which status
        Assert.Contains("Check the metrics policy.", run.Error);
        Assert.DoesNotContain("boom", run.Error);         // the exception text (GitHub's wording, the path) stays out of the stored log
        Assert.DoesNotContain("/orgs/acme", run.Error);
        await host.WithDbAsync(async db => { Assert.Equal(3, await db.Seats.CountAsync()); Assert.NotEmpty(db.CreditUsageDaily); return 0; });
    }

    [Fact]
    public async Task One_bad_day_does_not_stop_the_other_days()
    {
        var fake = Fake();
        fake.FailCredits = q => q.User is null && q.Day == 5 ? new GitHubApiException("500 for day 5") : null;
        await using var host = new TestHost(Now, github: fake);

        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        var step = result.Steps.Single(s => s.Step == "OrgCreditsByDay");
        Assert.Equal(SyncStatus.PartialFailure, step.Status);
        Assert.Equal(1, step.Errors);
        await host.WithDbAsync(async db => { Assert.Equal(10 * 2, await db.CreditUsageDaily.CountAsync()); return 0; });
    }

    [Fact]
    public async Task A_step_gives_up_after_three_consecutive_failures_instead_of_hammering_the_api()
    {
        var fake = Fake();
        fake.FailCredits = _ => new GitHubApiException("endpoint is wrong");
        await using var host = new TestHost(Now, github: fake);

        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        Assert.Equal(3 + 3, fake.CreditCalls); // 3 for the org-by-day step, 3 for the per-user step
        Assert.Equal(SyncStatus.Failed, result.Steps.Single(s => s.Step == "OrgCreditsByDay").Status);
        Assert.Equal(SyncStatus.PartialFailure, result.Status); // seats and metrics still succeeded
    }

    [Fact]
    public async Task Everything_failing_is_recorded_as_failed_and_never_throws()
    {
        var fake = Fake();
        await using var host = new TestHost(Now, github: fake);
        await host.Sync.RunAsync(SyncTrigger.Manual); // seeds seats so every step has work to attempt
        var before = await host.SnapshotAsync();

        fake.FailCredits = _ => new HttpRequestException("network down");
        fake.FailMetrics = () => new HttpRequestException("network down");
        fake.BeforeSeats = () => throw new HttpRequestException("network down");
        var result = await host.Sync.RunAsync(SyncTrigger.Scheduled);

        Assert.Equal(SyncStatus.Failed, result.Status);
        Assert.All(result.Steps, s => Assert.Equal(SyncStatus.Failed, s.Status));
        var run = await host.WithDbAsync(db => db.SyncRuns.OrderByDescending(r => r.Id).FirstAsync());
        Assert.Equal(SyncStatus.Failed, run.Status);
        Assert.Contains("network down", run.Error);
        Assert.Equal(before, await host.SnapshotAsync()); // a failed run leaves the previous data intact
    }

    [Fact]
    public async Task An_empty_seat_list_does_not_deactivate_existing_seats()
    {
        var fake = Fake();
        await using var host = new TestHost(Now, github: fake);
        await host.Sync.RunAsync(SyncTrigger.Manual);

        fake.Seats.Clear(); // summary still says 3 seats: almost certainly a bad response
        var result = await host.Sync.RunAsync(SyncTrigger.Manual);

        Assert.Equal(SyncStatus.PartialFailure, result.Steps.Single(s => s.Step == "Seats").Status);
        await host.WithDbAsync(async db => { Assert.Equal(3, await db.Seats.CountAsync(s => s.IsActive)); return 0; });
    }

    [Fact]
    public async Task A_second_request_while_a_sync_is_running_is_skipped_not_queued()
    {
        var fake = Fake();
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        fake.BeforeSeats = async () => { entered.TrySetResult(); await gate.Task; };
        await using var host = new TestHost(Now, github: fake);

        var running = host.Sync.RunAsync(SyncTrigger.Scheduled);
        await entered.Task;
        Assert.True(host.Sync.IsRunning);

        var skipped = await host.Sync.RunAsync(SyncTrigger.Manual);
        Assert.True(skipped.Skipped);
        Assert.Null(skipped.RunId);

        gate.SetResult();
        Assert.Equal(SyncStatus.Succeeded, (await running).Status);
        Assert.False(host.Sync.IsRunning);
        Assert.Equal(1, await host.WithDbAsync(db => db.SyncRuns.CountAsync()));
    }

    [Fact]
    public async Task Cancellation_propagates_and_marks_the_run_failed()
    {
        var fake = Fake();
        using var cts = new CancellationTokenSource();
        fake.BeforeSeats = () => { cts.Cancel(); cts.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await using var host = new TestHost(Now, github: fake);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Sync.RunAsync(SyncTrigger.Scheduled, cts.Token));
        var run = await host.WithDbAsync(db => db.SyncRuns.SingleAsync());
        Assert.Equal(SyncStatus.Failed, run.Status);
        Assert.False(host.Sync.IsRunning);
    }
}
