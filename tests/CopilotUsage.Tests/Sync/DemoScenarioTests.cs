using CopilotUsage.Core.Calculations;
using CopilotUsage.Data.Demo;
using CopilotUsage.Data.Entities;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotUsage.Tests.Sync;

public class DemoScenarioTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static async Task<TestHost> SeededAsync(string scenario, DateTimeOffset? now = null)
    {
        var host = new TestHost(now ?? Now, demo: true, scenario: scenario);
        var result = await host.Sync.RunAsync(SyncTrigger.Demo);
        Assert.Equal(SyncStatus.Succeeded, result.Status);
        return host;
    }

    [Theory]
    [InlineData(null, DemoScenario.Normal)]
    [InlineData("", DemoScenario.Normal)]
    [InlineData("normal", DemoScenario.Normal)]
    [InlineData("OVER", DemoScenario.Over)]
    [InlineData(" Empty ", DemoScenario.Empty)]
    public void Scenario_names_parse(string? text, DemoScenario expected)
    {
        Assert.True(DemoScenarios.TryParse(text, out var s));
        Assert.Equal(expected, s);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("7")]
    public void Unknown_scenario_names_are_rejected(string text) => Assert.False(DemoScenarios.TryParse(text, out _));

    [Fact]
    public void Config_validation_rejects_an_unknown_scenario_and_accepts_known_ones()
    {
        static IReadOnlyList<string> Validate(string scenario) => StartupConfigValidator.Validate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Demo:Enabled"] = "true", ["Demo:Scenario"] = scenario,
                ["GitHub:BaseUrl"] = "https://api.github.com/", ["ConnectionStrings:Default"] = "Data Source=x.db",
                ["Copilot:CreditsPerSeat"] = "1900", ["Sync:IntervalHours"] = "4", ["Auth:Enabled"] = "false",
            }).Build(), isDevelopment: true);
        Assert.Empty(Validate("over"));
        Assert.Empty(Validate("empty"));
        Assert.Contains(Validate("busy"), e => e.Contains("Demo:Scenario") && e.Contains("over"));
    }

    [Fact]
    public void Normal_is_the_default_and_matches_the_explicit_normal_scenario()
    {
        var a = DemoDataGenerator.Generate(Now, 1900m);
        var b = DemoDataGenerator.Generate(Now, 1900m, DemoScenario.Normal);
        Assert.Equal(a.OrgDaily, b.OrgDaily);
        Assert.Equal(a.UserMonthly, b.UserMonthly);
    }

    [Theory]
    [InlineData(2026, 11, 3)]
    [InlineData(2026, 11, 11)]
    [InlineData(2026, 11, 28)]
    public void Over_scenario_exhausts_the_pool_and_is_deterministic(int y, int m, int d)
    {
        var now = new DateTimeOffset(y, m, d, 9, 0, 0, TimeSpan.Zero);
        var set = DemoDataGenerator.Generate(now, 1900m, DemoScenario.Over);
        var again = DemoDataGenerator.Generate(now, 1900m, DemoScenario.Over);
        Assert.Equal(set.OrgDaily, again.OrgDaily);
        Assert.Equal(set.UserMonthly, again.UserMonthly);

        var current = BillingMonth.Containing(now);
        var used = set.UserMonthly.Where(l => l.Month == current).Sum(l => l.Credits);
        var limit = set.Summary.TotalSeats * 1900m;
        Assert.InRange(used / limit, 1.11m, 1.13m);
        Assert.Equal(used, set.OrgDaily.Where(r => r.Day is { } day && current.Contains(day)).Sum(r => r.Credits));
        Assert.Equal(25, set.Seats.Count);
    }

    [Fact]
    public async Task Over_scenario_shows_an_exhausted_pool_with_overage_through_the_queries()
    {
        await using var host = await SeededAsync("over");
        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync());
        Assert.True(home.Pool.IsOverLimit);
        Assert.Equal(0m, home.Pool.Remaining);
        Assert.InRange(home.Pool.PercentUsed!.Value, 111m, 113m);
        Assert.Equal(home.Pool.OverageCredits * 0.01m, home.Pool.OverageDollars);
        Assert.True(home.Pool.OverageCredits > 5_000m);
        Assert.Equal(home.Pool.Used, home.Burn.Last(p => p.Cumulative is not null).Cumulative);

        var board = await host.QueryAsync(q => q.GetLeaderboardAsync());
        Assert.Equal(25, board.Count);
        Assert.True(board.Count(r => r.OverFairShare) > 4);
    }

    [Fact]
    public async Task Empty_scenario_keeps_the_seats_but_records_no_usage_this_month()
    {
        await using var host = await SeededAsync("empty");
        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync());
        Assert.Equal(25, home.Pool.Seats);
        Assert.Equal(47_500m, home.Pool.Limit);
        Assert.Equal(0m, home.Pool.Used);
        Assert.Equal(0m, home.Pool.PercentUsed);
        Assert.Equal(0, home.ActiveThisCycle);
        Assert.Empty(home.ModelSplit);
        Assert.All(home.Burn.Where(p => p.Cumulative is not null), p => Assert.Equal(0m, p.Cumulative));
        Assert.NotNull(home.DataAsOf);

        var board = await host.QueryAsync(q => q.GetLeaderboardAsync());
        Assert.Equal(25, board.Count);
        Assert.All(board, r => Assert.Equal(0m, r.Credits));
        Assert.DoesNotContain(board, r => r.OverFairShare);
    }

    [Fact]
    public async Task Reseeding_with_another_scenario_leaves_no_stale_usage_behind()
    {
        // The same database seeded as normal, then as empty, must equal a database seeded as empty from scratch.
        await using var fresh = await SeededAsync("empty");
        var expected = await fresh.SnapshotAsync();

        await using var host = await SeededAsync("normal");
        Assert.NotEqual(expected, await host.SnapshotAsync());
        await using var scope = host.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<DemoSeeder>();
        await seeder.SeedAsync(DemoScenario.Empty);
        Assert.Equal(expected, await host.SnapshotAsync());
    }
}
