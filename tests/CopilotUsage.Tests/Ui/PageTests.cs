using Bunit;
using CopilotUsage.Core;
using CopilotUsage.Data.Queries;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Components.Pages;
using CopilotUsage.Data.Entities;
using UserDetailPage = CopilotUsage.Web.Components.Pages.UserDetail;
using CopilotUsage.Web.Components.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.Ui;

/// <summary>The Users and user pages, rendered against a real (in-memory) database filled with demo data.</summary>
public class PageTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private async Task<TestHost> HostAsync(bool seed = true)
    {
        var host = new TestHost(Now, demo: true);
        if (seed) Assert.Equal(SyncStatus.Succeeded, (await host.Sync.RunAsync(SyncTrigger.Demo)).Status);
        // bUnit has its own container, so the page's query service is built from the test host (which owns the database).
        Services.AddScoped(_ => host.Services.CreateScope().ServiceProvider.GetRequiredService<UsageQueries>());
        Services.AddSingleton<TimeProvider>(host.Time);
        Services.AddSingleton(Options.Create(new CopilotOptions()));
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        JSInterop.Mode = JSRuntimeMode.Loose;
        return host;
    }

    [Fact]
    public async Task Users_lists_every_seat_sorted_by_credits_with_inactive_seats_marked()
    {
        await using var host = await HostAsync();
        var cut = Render<Users>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);

        Assert.Equal(25, cut.FindAll("tbody tr").Count);
        Assert.Equal("descending", cut.Find("th.c-credits").GetAttribute("aria-sort"));
        Assert.Equal("none", cut.Find("th.c-prompts").GetAttribute("aria-sort"));
        Assert.StartsWith("/users/", cut.Find("tbody tr a.link").GetAttribute("href"));
        Assert.Equal(2, cut.FindAll("tr.idle").Count);
        Assert.All(cut.FindAll("tr.idle"), r => Assert.Contains("No usage this month", r.TextContent));
        Assert.Contains("Over fair share", cut.Find("tbody tr").TextContent);
        Assert.Contains("25 users", cut.Find(".count").TextContent);
    }

    [Fact]
    public async Task Users_filters_as_you_type_and_explains_no_match()
    {
        await using var host = await HostAsync();
        var cut = Render<Users>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);

        cut.Find("#user-search").Input("chen");
        Assert.Single(cut.FindAll("tbody tr"));
        Assert.Contains("1 of 25 users match", cut.Find(".count").TextContent);

        cut.Find("#user-search").Input("zzzz");
        Assert.Empty(cut.FindAll("tbody tr"));
        Assert.Contains("No users match", cut.Markup);

        cut.Find(".nomatch .btn").Click();
        Assert.Equal(25, cut.FindAll("tbody tr").Count);
    }

    [Fact]
    public async Task Users_sorts_by_a_clicked_column_and_flips_on_a_second_click()
    {
        await using var host = await HostAsync();
        var cut = Render<Users>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);

        cut.Find("th.c-user button").Click();
        Assert.Equal("ascending", cut.Find("th.c-user").GetAttribute("aria-sort"));
        Assert.Equal("none", cut.Find("th.c-credits").GetAttribute("aria-sort"));
        var first = cut.Find("tbody tr .login").TextContent;
        Assert.Equal("aisha-khan", first);

        cut.Find("th.c-user button").Click();
        Assert.Equal("descending", cut.Find("th.c-user").GetAttribute("aria-sort"));
        Assert.NotEqual(first, cut.Find("tbody tr .login").TextContent);
    }

    [Fact]
    public async Task Users_shows_an_empty_state_without_seats()
    {
        await using var host = await HostAsync(seed: false);
        var cut = Render<Users>();
        cut.WaitForState(() => cut.Markup.Contains("No seats yet"));
        Assert.Empty(cut.FindAll("table"));
    }

    [Fact]
    public async Task Users_shows_an_error_state_when_the_database_cannot_be_read()
    {
        await using var host = await HostAsync(seed: false);
        await host.DisposeAsync();      // the scope factory now throws
        var cut = Render<Users>();
        cut.WaitForState(() => cut.Markup.Contains("Could not load"));
        Assert.Contains("could not be read", cut.Markup);
        Assert.Empty(cut.FindAll("table"));
    }

    [Fact]
    public async Task User_page_shows_credits_models_mix_and_productivity_for_a_heavy_user()
    {
        await using var host = await HostAsync();
        var login = (await host.QueryAsync(q => q.GetLeaderboardAsync()))[0].Login;
        var cut = Render<UserDetailPage>(p => p.Add(x => x.Login, login.ToUpperInvariant()));
        cut.WaitForState(() => cut.Markup.Contains("Credits and cost"));

        Assert.Equal(login, cut.Find("h1").TextContent);
        Assert.Contains("Rank 1 of", cut.Markup);
        Assert.Contains("Over fair share", cut.Markup);
        Assert.Contains("not a GitHub limit", cut.Markup);
        Assert.Equal(1, cut.Markup.Split("not a GitHub limit").Length - 1);       // the reminder appears once
        Assert.Contains("Estimate", cut.Markup);
        Assert.Contains("will not add up exactly to the billed total", cut.Markup);
        Assert.Equal(11, cut.FindAll("g.day").Count);                              // one per day so far
        Assert.Contains("Credits by model", cut.Markup);
        Assert.Contains("Chat, agent mode", cut.Markup);                          // readable feature label
        Assert.DoesNotContain("chat_panel_agent_mode", cut.Markup);
        Assert.Contains("does not split credits by feature", cut.Markup);
        Assert.Contains("Acceptance rate", cut.Markup);
        Assert.Contains("Languages", cut.Markup);
        Assert.Equal("/users", cut.Find("a.back").GetAttribute("href"));
    }

    [Fact]
    public async Task User_page_marks_a_seat_with_no_usage()
    {
        await using var host = await HostAsync();
        var idle = (await host.QueryAsync(q => q.GetLeaderboardAsync())).Last().Login;
        var cut = Render<UserDetailPage>(p => p.Add(x => x.Login, idle));
        cut.WaitForState(() => cut.Markup.Contains("Credits and cost"));

        Assert.Contains("No usage this month", cut.Markup);
        Assert.Contains("Unranked", cut.Markup);
        Assert.Empty(cut.FindAll("svg g.day"));
        Assert.Contains("No billed credits this month", cut.Markup);
    }

    [Fact]
    public async Task User_page_for_an_unknown_login_says_so_and_links_back()
    {
        await using var host = await HostAsync();
        var cut = Render<UserDetailPage>(p => p.Add(x => x.Login, "ghost-user"));
        cut.WaitForState(() => cut.Markup.Contains("No user named"));

        Assert.Contains("ghost-user", cut.Find("h1").TextContent);
        Assert.Contains(cut.FindAll("a"), a => a.GetAttribute("href") == "/users" && a.TextContent.Contains("Back to all users"));
        Assert.DoesNotContain("Credits and cost", cut.Markup);
    }

    [Fact]
    public async Task User_page_shows_an_error_state_when_the_database_cannot_be_read()
    {
        await using var host = await HostAsync(seed: false);
        await host.DisposeAsync();
        var cut = Render<UserDetailPage>(p => p.Add(x => x.Login, "anyone"));
        cut.WaitForState(() => cut.Markup.Contains("Could not load"));
        Assert.Contains("could not be read", cut.Markup);
        Assert.DoesNotContain("No user named", cut.Markup);
    }

    [Fact]
    public void Fair_share_meter_flags_over_in_text_and_stays_plain_under()
    {
        var over = Render<FairShareMeter>(p => p.Add(x => x.Credits, 2400m).Add(x => x.FairShare, 1900m).Add(x => x.Percent, 126.3m));
        Assert.Contains("Over fair share 126%", over.Find(".over-flag").TextContent);
        var under = Render<FairShareMeter>(p => p.Add(x => x.Credits, 950m).Add(x => x.FairShare, 1900m).Add(x => x.Percent, 50m));
        Assert.Empty(under.FindAll(".over-flag"));
        Assert.Contains("50% of fair share", under.Markup);
    }

    [Fact]
    public void Activity_bars_show_labels_units_and_an_empty_message()
    {
        var items = new List<ActivityItem> { new("chat_panel_agent_mode", 242, 0, 0, null, 0, 0, 0, 0, null) };
        var cut = Render<ActivityBars>(p => p.Add(x => x.Items, items).Add(x => x.Label, CopilotUsage.Web.Ui.Labels.Feature));
        Assert.Contains("Chat, agent mode", cut.Markup);
        Assert.Contains("242 prompts", cut.Markup);

        var none = Render<ActivityBars>(p => p.Add(x => x.Items, new List<ActivityItem>()).Add(x => x.EmptyText, "Nothing here."));
        Assert.Contains("Nothing here.", none.Markup);
    }

    [Fact]
    public void Daily_chart_draws_a_bar_per_active_day_over_the_whole_month()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var days = Enumerable.Range(1, 11).Select(d => new DailyCredit(new DateOnly(2026, 11, d), d % 3 == 0 ? 0m : 100m + d, 0m)).ToList();
        var cut = Render<DailyChart>(p => p.Add(x => x.Days, days).Add(x => x.DaysInMonth, 30));
        Assert.Equal(11, cut.FindAll("g.day").Count);
        Assert.Equal(8, cut.FindAll("rect.bar").Count);
        Assert.Contains("No activity", cut.Markup);
        Assert.Contains("Estimated", cut.Markup);
    }

    [Fact]
    public void Burn_chart_label_avoids_the_data_line_on_a_narrow_chart()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var month = new CopilotUsage.Core.Calculations.BillingMonth(2026, 11);
        var daily = Enumerable.Range(1, 11).ToDictionary(d => new DateOnly(2026, 11, d), _ => 6000m);
        var pts = CopilotUsage.Core.Calculations.BurnSeries.Build(month, daily, 47_500m, new DateOnly(2026, 11, 11));
        var cut = Render<BurnChart>(p => p.Add(x => x.Points, pts).Add(x => x.Limit, 47_500m));
        cut.InvokeAsync(() => cut.Instance.OnWidth(358));
        // The limit is always named in the legend; the in-chart label sits at the right end where the line is not.
        Assert.Contains("Pool limit · 47,500", cut.Find(".legend").TextContent);
        var label = cut.Find("text.limit-label");
        Assert.Equal("end", label.GetAttribute("text-anchor"));
    }
}
