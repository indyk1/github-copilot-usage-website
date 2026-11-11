using CopilotUsage.Core.Calculations;
using CopilotUsage.Data.Demo;
using CopilotUsage.Data.Entities;
using CopilotUsage.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace CopilotUsage.Tests.Sync;

public class DemoAndQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static async Task<TestHost> SeededAsync(DateTimeOffset? now = null)
    {
        var host = new TestHost(now ?? Now, demo: true);
        var result = await host.Sync.RunAsync(SyncTrigger.Demo);
        Assert.Equal(SyncStatus.Succeeded, result.Status);
        return host;
    }

    [Fact]
    public void Generator_is_deterministic()
    {
        var a = DemoDataGenerator.Generate(Now, 1900m);
        var b = DemoDataGenerator.Generate(Now, 1900m);
        Assert.Equal(a.OrgDaily, b.OrgDaily);
        Assert.Equal(a.UserMonthly, b.UserMonthly);
        Assert.Equal(a.Seats, b.Seats);
        Assert.Equal(a.Metrics.Count, b.Metrics.Count);
        Assert.Equal(a.Metrics.Sum(m => m.AiCredits), b.Metrics.Sum(m => m.AiCredits));
        Assert.Equal(a.Metrics.SelectMany(m => m.Breakdowns).Sum(x => x.Interactions + x.Requests), b.Metrics.SelectMany(m => m.Breakdowns).Sum(x => x.Interactions + x.Requests));
    }

    [Theory]
    [InlineData(2026, 11, 11)]
    [InlineData(2026, 11, 28)]
    [InlineData(2026, 12, 20)]
    public void Demo_population_has_the_requested_shape(int y, int m, int d)
    {
        var now = new DateTimeOffset(y, m, d, 9, 0, 0, TimeSpan.Zero);
        var set = DemoDataGenerator.Generate(now, 1900m);
        var current = BillingMonth.Containing(now);

        Assert.Equal(25, set.Seats.Count);
        Assert.Equal(25, set.Summary.TotalSeats);
        Assert.Equal(2, set.Months.Count);

        // Per-user credits per month.
        var perUser = set.UserMonthly.GroupBy(l => (l.Month, l.Login)).ToDictionary(g => g.Key, g => g.Sum(l => l.Credits));
        var overCurrent = perUser.Count(kv => kv.Key.Month == current && kv.Value > 1900m);
        var overPrevious = perUser.Count(kv => kv.Key.Month == current.Previous && kv.Value > 1900m);
        Assert.InRange(overCurrent, 3, 6);
        Assert.InRange(overPrevious, 3, 6);

        // Two inactive seats: no credits, no metrics in either month.
        var usersWithCredits = set.UserMonthly.Select(l => l.Login).ToHashSet();
        var inactive = set.Seats.Select(s => s.Login).Where(l => !usersWithCredits.Contains(l)).ToList();
        Assert.Equal(2, inactive.Count);
        Assert.DoesNotContain(set.Metrics, x => inactive.Contains(x.Login));
        Assert.Equal(23, set.Summary.ActiveThisCycle);

        // Light users exist, and nobody has more than ~2.5 fair shares.
        Assert.True(perUser.Count(kv => kv.Key.Month == current && kv.Value < 500m) >= 5);
        Assert.All(perUser.Values, v => Assert.InRange(v, 0m, 4750m));

        // Variety of models, features, IDEs, clients, languages.
        Assert.Equal(6, set.OrgDaily.Select(r => r.Model).Distinct().Count());
        var breakdowns = set.Metrics.SelectMany(x => x.Breakdowns).ToList();
        foreach (var (dim, min) in new[] { (CopilotUsage.Core.Models.MetricDimension.Feature, 5), (CopilotUsage.Core.Models.MetricDimension.Ide, 3), (CopilotUsage.Core.Models.MetricDimension.Client, 4), (CopilotUsage.Core.Models.MetricDimension.Language, 6), (CopilotUsage.Core.Models.MetricDimension.Model, 5) })
            Assert.True(breakdowns.Where(b => b.Dimension == dim).Select(b => b.Key).Distinct().Count() >= min, $"{dim} variety");

        // Data stays inside the two months and never runs past today.
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        Assert.All(set.OrgDaily, r => Assert.True(r.Day <= today && r.Day >= current.Previous.Start));
        Assert.All(set.Metrics, x => Assert.True(x.Day <= today));
        // Seats never claim activity in the future.
        Assert.All(set.Seats, s => Assert.True(s.LastActivityAt is null || s.LastActivityAt <= now));
    }

    [Fact]
    public void Billing_rows_are_consistent_across_the_org_and_user_views_and_metrics_track_billing()
    {
        var set = DemoDataGenerator.Generate(Now, 1900m);
        foreach (var month in set.Months)
        {
            var org = set.OrgDaily.Where(r => r.Day is { } d && month.Contains(d)).Sum(r => r.Credits);
            var users = set.UserMonthly.Where(l => l.Month == month).Sum(l => l.Credits);
            Assert.Equal(org, users);
        }
        // Metrics credits are a noisy signal around billed credits (within 10% in total).
        var billed = set.OrgDaily.Sum(r => r.Credits);
        Assert.InRange(set.Metrics.Sum(m => m.AiCredits) / billed, 0.9m, 1.1m);
        // Amounts are one cent per credit.
        Assert.All(set.OrgDaily, r => Assert.Equal(r.Credits * 0.01m, r.Amount));
    }

    [Fact]
    public async Task Seeding_twice_is_idempotent()
    {
        await using var host = await SeededAsync();
        var first = await host.SnapshotAsync();
        await host.Sync.RunAsync(SyncTrigger.Demo);
        Assert.Equal(first, await host.SnapshotAsync());
    }

    [Fact]
    public async Task Home_summary_reports_pool_burn_models_and_freshness()
    {
        await using var host = await SeededAsync();
        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync());

        Assert.Equal(new BillingMonth(2026, 11), home.Month);
        Assert.Equal(25, home.Pool.Seats);
        Assert.Equal(47_500m, home.Pool.Limit);
        Assert.InRange(home.Pool.Used, 15_000m, 47_500m);
        Assert.Equal(home.Pool.Limit - home.Pool.Used, home.Pool.Remaining);
        Assert.Equal(20, home.DaysUntilReset);
        Assert.Equal(30, home.Burn.Count);
        Assert.Equal(home.Pool.Used, home.Burn[10].Cumulative);   // day 11 is today
        Assert.Null(home.Burn[11].Cumulative);
        Assert.Equal(6, home.ModelSplit.Count);
        Assert.Equal(home.Pool.Used, home.ModelSplit.Sum(m => m.Credits));
        Assert.True(home.ModelSplit.SequenceEqual(home.ModelSplit.OrderByDescending(m => m.Credits)));
        Assert.Equal(23, home.ActiveThisCycle);
        Assert.NotNull(home.DataAsOf);
        Assert.True(home.LastSync!.IsDemo);
    }

    [Fact]
    public async Task Previous_month_is_queryable_too()
    {
        await using var host = await SeededAsync();
        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync(new BillingMonth(2026, 10)));
        Assert.True(home.Pool.Used > 0);
        Assert.All(home.Burn, p => Assert.NotNull(p.Cumulative)); // finished month: fully filled
        Assert.Equal(31, home.Burn.Count);
    }

    [Fact]
    public async Task Leaderboard_ranks_every_seat_and_flags_users_over_fair_share()
    {
        await using var host = await SeededAsync();
        var board = await host.QueryAsync(q => q.GetLeaderboardAsync());

        Assert.Equal(25, board.Count);
        Assert.Equal(1, board[0].Rank);
        Assert.True(board.SequenceEqual(board.OrderByDescending(r => r.Credits)));
        Assert.InRange(board.Count(r => r.OverFairShare), 3, 6);
        Assert.Equal(2, board.Count(r => r.Credits == 0m));
        Assert.All(board.Where(r => r.OverFairShare), r => Assert.True(r.FairSharePercent > 100m));
        Assert.All(board, r => Assert.StartsWith("https://", r.AvatarUrl));
        Assert.Equal(board[0].Credits / 47_500m * 100m, board[0].ShareOfPoolPercent);
    }

    [Fact]
    public async Task Users_table_has_one_row_per_seat_with_monthly_columns()
    {
        await using var host = await SeededAsync();
        var rows = await host.QueryAsync(q => q.GetUsersTableAsync());

        Assert.Equal(25, rows.Count);
        var busy = rows[0];
        Assert.True(busy.Credits > 1900m);
        Assert.True(busy.Prompts > 0);
        Assert.InRange(busy.AcceptanceRate!.Value, 0.1m, 0.6m);
        Assert.NotNull(busy.TopModel);
        Assert.NotNull(busy.LastActiveAt);
        var idle = rows.Where(r => r.Credits == 0m).ToList();
        Assert.Equal(2, idle.Count);
        Assert.All(idle, r => { Assert.Equal(0, r.Prompts); Assert.Null(r.AcceptanceRate); Assert.Null(r.TopModel); });
    }

    [Fact]
    public async Task User_drill_down_covers_credits_models_activity_and_productivity()
    {
        await using var host = await SeededAsync();
        var board = await host.QueryAsync(q => q.GetLeaderboardAsync());
        var login = board[0].Login;
        var d = await host.QueryAsync(q => q.GetUserDetailAsync(login.ToUpperInvariant())); // logins are case-insensitive

        Assert.NotNull(d);
        Assert.Equal(login, d!.Login);
        Assert.Equal(1, d.Rank);
        Assert.Equal(25, d.RankedUsers);
        Assert.Equal(board[0].Credits, d.Credits);
        Assert.Equal(1900m, d.FairShare);
        Assert.True(d.FairSharePercent > 100m);
        Assert.Equal(d.Credits * 0.01m, d.Dollars);

        Assert.Equal(11, d.DailyCredits.Count);                       // 1st..today, zero-filled
        Assert.Equal(d.MetricsCredits, d.DailyCredits[^1].Cumulative);
        Assert.Equal(d.Credits, d.CreditsByModel.Sum(m => m.Credits));
        Assert.InRange(d.CreditsByModel.Sum(m => m.SharePercent!.Value), 99.99m, 100.01m);
        Assert.True(d.CreditsByModel.SequenceEqual(d.CreditsByModel.OrderByDescending(m => m.Credits)));

        Assert.Contains(d.ByFeature, f => f.Key == "code_completion" && f.Generations > 0);
        Assert.NotEmpty(d.ByIde);
        Assert.All(d.ByIde, i => Assert.False(string.IsNullOrEmpty(i.Version)));
        Assert.NotEmpty(d.ByClient);
        Assert.All(d.ByClient, c => Assert.True(c.Requests > 0 && c.Tokens > 0));
        Assert.True(d.ByLanguage.Count >= 2);
        Assert.NotEmpty(d.ByModel);

        var p = d.Productivity;
        Assert.True(p.Prompts > 0 && p.SuggestionsGenerated > 0 && p.LocAdded > 0);
        Assert.Equal((decimal)p.SuggestionsAccepted / p.SuggestionsGenerated, p.AcceptanceRate);
        Assert.InRange(p.ActiveDays, 1, 11);
        Assert.NotNull(p.LastActiveDay);
    }

    [Fact]
    public async Task Unknown_user_is_null_and_inactive_user_has_a_valid_empty_detail()
    {
        await using var host = await SeededAsync();
        Assert.Null(await host.QueryAsync(q => q.GetUserDetailAsync("nobody-here")));

        var idle = (await host.QueryAsync(q => q.GetLeaderboardAsync())).Last().Login;
        var d = await host.QueryAsync(q => q.GetUserDetailAsync(idle));
        Assert.NotNull(d);
        Assert.Equal(0m, d!.Credits);
        Assert.Empty(d.CreditsByModel);
        Assert.Empty(d.ByFeature);
        Assert.Equal(0, d.Productivity.ActiveDays);
        Assert.Null(d.Productivity.AcceptanceRate);
        Assert.Equal(11, d.DailyCredits.Count);
    }

    [Fact]
    public async Task User_detail_reports_the_share_of_the_org_pool()
    {
        await using var host = await SeededAsync();
        var board = await host.QueryAsync(q => q.GetLeaderboardAsync());
        var d = await host.QueryAsync(q => q.GetUserDetailAsync(board[0].Login));
        Assert.Equal(47_500m, d!.PoolLimit);
        Assert.Equal(board[0].ShareOfPoolPercent, d.ShareOfPoolPercent);
    }

    [Fact]
    public async Task User_exists_for_seats_in_any_case_and_not_for_strangers()
    {
        await using var host = await SeededAsync();
        var login = (await host.QueryAsync(q => q.GetLeaderboardAsync()))[0].Login;
        Assert.True(await host.QueryAsync(q => q.UserExistsAsync(login)));
        Assert.True(await host.QueryAsync(q => q.UserExistsAsync(login.ToUpperInvariant())));
        var idle = (await host.QueryAsync(q => q.GetLeaderboardAsync())).Last().Login;
        Assert.True(await host.QueryAsync(q => q.UserExistsAsync(idle)));      // a seat with no usage still exists
        Assert.False(await host.QueryAsync(q => q.UserExistsAsync("nobody-here")));
        Assert.False(await host.QueryAsync(q => q.UserExistsAsync("  ")));
    }

    [Fact]
    public async Task Queries_on_an_empty_database_return_zeroes_instead_of_throwing()
    {
        await using var host = new TestHost(Now);
        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync());
        Assert.Equal(0m, home.Pool.Limit);
        Assert.Equal(0m, home.Pool.Used);
        Assert.Null(home.Pool.PercentUsed);
        Assert.Null(home.DataAsOf);
        Assert.Null(home.LastSync);
        Assert.Equal(30, home.Burn.Count);
        Assert.Empty(await host.QueryAsync(q => q.GetLeaderboardAsync()));
        Assert.Empty(await host.QueryAsync(q => q.GetUsersTableAsync()));
    }

    [Fact]
    public async Task Overage_shows_when_usage_exceeds_the_pool()
    {
        await using var host = new TestHost(Now);
        await host.WithDbAsync(async db =>
        {
            db.SeatSnapshots.Add(new SeatSnapshot { Day = new DateOnly(2026, 11, 11), TotalSeats = 2, ActiveThisCycle = 2, CapturedAt = Now });
            db.CreditUsageDaily.Add(new CreditUsageDaily { Day = new DateOnly(2026, 11, 3), Model = "m", Credits = 4_300m, Amount = 43m });
            await db.SaveChangesAsync();
            return 0;
        });
        var home = await host.QueryAsync(q => q.GetHomeSummaryAsync());
        Assert.Equal(3_800m, home.Pool.Limit);
        Assert.Equal(0m, home.Pool.Remaining);
        Assert.Equal(500m, home.Pool.OverageCredits);
        Assert.Equal(5m, home.Pool.OverageDollars);
    }

    [Fact]
    public async Task Previous_month_rows_do_not_leak_into_the_current_month()
    {
        await using var host = await SeededAsync();
        var oct = await host.QueryAsync(q => q.GetHomeSummaryAsync(new BillingMonth(2026, 10)));
        var nov = await host.QueryAsync(q => q.GetHomeSummaryAsync(new BillingMonth(2026, 11)));
        var total = await host.WithDbAsync(db => db.CreditUsageDaily.Select(r => r.Credits).ToListAsync());
        Assert.Equal(total.Sum(), oct.Pool.Used + nov.Pool.Used);
    }
}
