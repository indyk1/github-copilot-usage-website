using Bunit;
using CopilotUsage.Core.Calculations;
using CopilotUsage.Data.Queries;
using CopilotUsage.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using LeaderboardView = CopilotUsage.Web.Components.Ui.Leaderboard;

namespace CopilotUsage.Tests.Ui;

public class ComponentTests : BunitContext
{
    private static PoolSummary Pool(decimal used) => PoolCalculator.Summarize(25, 1900m, used);

    [Fact]
    public void Gauge_shows_percent_and_remaining_without_an_alert_while_within_the_pool()
    {
        var cut = Render<PoolGauge>(p => p.Add(x => x.Pool, Pool(25_324m)).Add(x => x.PacePercent, 36m));
        Assert.Contains("53%", cut.Markup);
        Assert.Contains("22,176", cut.Markup);
        Assert.DoesNotContain("Pool exhausted", cut.Markup);
        Assert.Contains("spending faster than an even pace", cut.Markup);
    }

    [Fact]
    public void Gauge_shows_a_text_state_when_the_pool_is_exhausted()
    {
        var cut = Render<PoolGauge>(p => p.Add(x => x.Pool, Pool(53_200m)));
        Assert.Contains("Pool exhausted", cut.Markup);
        Assert.Contains("112%", cut.Markup);
        Assert.Contains("5,700 credits over the pool", cut.Markup);
        Assert.Contains("$57.00", cut.Markup);
        Assert.Contains("none left", cut.Markup);
    }

    [Fact]
    public void Gauge_without_seats_does_not_divide_by_zero()
    {
        var cut = Render<PoolGauge>(p => p.Add(x => x.Pool, PoolCalculator.Summarize(0, 1900m, 0m)));
        Assert.Contains("No seats", cut.Markup);
    }

    [Fact]
    public void Progress_bar_flags_values_past_the_target()
    {
        var under = Render<ProgressBar>(p => p.Add(x => x.Value, 950m).Add(x => x.Target, 1900m));
        Assert.DoesNotContain("over", under.Find(".pbar").ClassList);
        Assert.Equal("25", under.Find(".fill").GetAttribute("style")!.Split(':')[1].Trim().TrimEnd(';').TrimEnd('%'));

        var over = Render<ProgressBar>(p => p.Add(x => x.Value, 3000m).Add(x => x.Target, 1900m));
        Assert.Contains("over", over.Find(".pbar").ClassList);
        Assert.Contains("over the target", over.Find(".pbar").GetAttribute("aria-valuetext"));
    }

    [Fact]
    public void Avatar_has_initials_and_an_image_that_hides_itself_on_error()
    {
        var cut = Render<Avatar>(p => p.Add(x => x.Login, "maya-chen").Add(x => x.Url, "https://example.test/a.png"));
        Assert.Equal("MC", cut.Find(".initials").TextContent);
        Assert.Contains("display='none'", cut.Find("img").GetAttribute("onerror"));

        var none = Render<Avatar>(p => p.Add(x => x.Login, "maya-chen"));
        Assert.Empty(none.FindAll("img"));
    }

    [Fact]
    public void Leaderboard_links_rows_flags_over_fair_share_in_text_and_collapses()
    {
        var rows = Enumerable.Range(1, 12).Select(i => new LeaderboardRow(
            i, $"user-{i}", null, 3000m - i * 200m, 30m, 5m, (3000m - i * 200m) / 19m, 3000m - i * 200m > 1900m)).ToList();
        var cut = Render<LeaderboardView>(p => p.Add(x => x.Rows, rows).Add(x => x.FairShare, 1900m));

        Assert.Equal(10, cut.FindAll("a.row").Count);
        Assert.Equal("/users/user-1", cut.FindAll("a.row")[0].GetAttribute("href"));
        Assert.Contains("Over fair share", cut.FindAll("a.row")[0].TextContent);
        Assert.DoesNotContain("Over fair share", cut.FindAll("a.row")[9].TextContent);
        Assert.Contains("benchmark set by this dashboard", cut.Markup);

        cut.Find("button").Click();
        Assert.Equal(12, cut.FindAll("a.row").Count);
    }

    [Fact]
    public void Leaderboard_escapes_logins_in_links()
    {
        var rows = new List<LeaderboardRow> { new(1, "a b/c", null, 0m, 0m, 0m, 0m, false) };
        var cut = Render<LeaderboardView>(p => p.Add(x => x.Rows, rows));
        Assert.Equal("/users/a%20b%2Fc", cut.Find("a.row").GetAttribute("href"));
    }

    [Fact]
    public void Model_bars_group_the_tail_and_handle_no_usage()
    {
        var models = Enumerable.Range(1, 8).Select(i => new ModelShare($"m{i}", 100m * (9 - i), 0m, 10m)).ToList();
        var cut = Render<ModelBars>(p => p.Add(x => x.Models, models));
        Assert.Equal(7, cut.FindAll("li").Count);
        Assert.Contains("Other (2 models)", cut.Markup);

        var none = Render<ModelBars>(p => p.Add(x => x.Models, new List<ModelShare>()));
        Assert.Contains("No model usage", none.Markup);
    }

    [Fact]
    public void Burn_chart_draws_actual_only_to_today_and_a_tooltip_per_day()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var month = new BillingMonth(2026, 11);
        var daily = Enumerable.Range(1, 11).ToDictionary(d => new DateOnly(2026, 11, d), _ => 2000m);
        var pts = BurnSeries.Build(month, daily, 47_500m, new DateOnly(2026, 11, 11));
        var cut = Render<BurnChart>(p => p.Add(x => x.Points, pts).Add(x => x.Limit, 47_500m));

        Assert.Equal(30, cut.FindAll("g.day").Count);
        Assert.Single(cut.FindAll("path.line"));          // no over-limit overlay while within the pool
        Assert.Contains("Not reached yet", cut.Markup);
        Assert.Contains("Pool limit", cut.Markup);
        Assert.Contains("22,000", cut.Markup);            // cumulative at day 11
    }

    [Fact]
    public void Burn_chart_marks_the_part_over_the_limit()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var month = new BillingMonth(2026, 11);
        var daily = Enumerable.Range(1, 11).ToDictionary(d => new DateOnly(2026, 11, d), _ => 6000m);
        var pts = BurnSeries.Build(month, daily, 47_500m, new DateOnly(2026, 11, 11));
        var cut = Render<BurnChart>(p => p.Add(x => x.Points, pts).Add(x => x.Limit, 47_500m));
        Assert.Single(cut.FindAll("path.line.over"));
    }

    [Fact]
    public void Stat_tile_and_status_badge_carry_status_as_text()
    {
        var cut = Render<StatTile>(p => p
            .Add(x => x.Label, "Overage so far").Add(x => x.Tone, StatTile.StatTone.Danger).Add(x => x.Status, "Over the pool")
            .Add(x => x.Value, (RenderFragment)(b => b.AddContent(0, "5,700"))));
        Assert.Contains("Over the pool", cut.Markup);
        Assert.Contains("5,700", cut.Markup);
        Assert.NotEmpty(cut.FindAll("svg.icon"));
    }
}
