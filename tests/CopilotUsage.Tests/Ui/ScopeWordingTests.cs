using Bunit;
using CopilotUsage.Core;
using CopilotUsage.Data.Entities;
using CopilotUsage.Data.Queries;
using CopilotUsage.GitHub;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Components.Pages;
using CopilotUsage.Web.Config;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LeaderboardView = CopilotUsage.Web.Components.Ui.Leaderboard;
using UserDetailPage = CopilotUsage.Web.Components.Pages.UserDetail;

namespace CopilotUsage.Tests.Ui;

/// <summary>The wording of the pages in organization and enterprise scope, and the conditional organization column.</summary>
public class ScopeWordingTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private static readonly GitHubScope Ent = new(GitHubScopeKind.Enterprise, "big-corp");
    private static readonly GitHubScope Org = new(GitHubScopeKind.Organization, "acme");

    private async Task<TestHost> HostAsync(GitHubScope scope)
    {
        var host = new TestHost(Now, demo: true, gitHubScope: scope);
        await host.Sync.RunAsync(SyncTrigger.Demo);
        Services.AddScoped(_ => host.Services.CreateScope().ServiceProvider.GetRequiredService<UsageQueries>());
        Services.AddSingleton<TimeProvider>(host.Time);
        Services.AddSingleton(Options.Create(new CopilotOptions()));
        Services.AddSingleton(Options.Create(new DemoOptions { Enabled = true }));
        Services.AddSingleton(host.Sync);
        Services.AddSingleton(scope);
        Services.AddSingleton(new AuthMode(false));
        Services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        JSInterop.Mode = JSRuntimeMode.Loose;
        AddAuthorization();
        return host;
    }

    private static void AssertNone(string markup, params string[] words)
    {
        foreach (var w in words) Assert.DoesNotContain(w, markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Home_reads_enterprise_in_enterprise_scope()
    {
        await using var host = await HostAsync(Ent);
        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Credit pool"));

        Assert.Contains("Enterprise overview", cut.Find(".eyebrow").TextContent);
        Assert.Contains("Enterprise-wide credits by model", cut.Markup);
        Assert.Contains("the whole enterprise", cut.Markup);
        AssertNone(cut.Markup, "organization", "org-wide");
        Assert.DoesNotContain("big-corp", cut.Markup);       // the slug is not shown anywhere
    }

    [Fact]
    public async Task Home_reads_organization_in_organization_scope_as_before()
    {
        await using var host = await HostAsync(Org);
        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Credit pool"));

        Assert.Contains("Organization overview", cut.Find(".eyebrow").TextContent);
        Assert.Contains("Org-wide credits by model", cut.Markup);
        Assert.Contains("the whole organization", cut.Markup);
        AssertNone(cut.Markup, "enterprise");
    }

    [Fact]
    public async Task Home_empty_state_names_the_scope()
    {
        await using var host = await HostAsync(Ent);
        await host.WithDbAsync(async db => { db.SeatSnapshots.RemoveRange(db.SeatSnapshots); db.Seats.RemoveRange(db.Seats); await db.SaveChangesAsync(); return 0; });
        await host.WithDbAsync(async db => { db.SeatSnapshots.Add(new SeatSnapshot { Day = new DateOnly(2026, 11, 11), TotalSeats = 0 }); await db.SaveChangesAsync(); return 0; });
        var cut = Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("No Copilot seats found"));
        Assert.Contains("no Copilot seats in the enterprise", cut.Markup);
    }

    [Theory]
    [InlineData(true, "enterprise")]
    [InlineData(false, "organization")]
    public async Task Footer_users_and_user_pages_use_the_scope_noun(bool enterprise, string noun)
    {
        var other = enterprise ? "organization" : "enterprise";
        await using var host = await HostAsync(enterprise ? Ent : Org);

        var layout = Render<CopilotUsage.Web.Components.Layout.MainLayout>(p => p.Add(x => x.Body, (RenderFragment)(b => b.AddContent(0, "x"))));
        Assert.Contains($"shared across the {noun} and resets", layout.Find("footer").TextContent);
        Assert.DoesNotContain(other, layout.Find("footer").TextContent);

        var users = Render<Users>();
        users.WaitForState(() => users.FindAll("tbody tr").Count > 0);
        Assert.Contains($"shared by the whole {noun}.", users.Find(".note").TextContent);
        Assert.DoesNotContain(other, users.Find(".note").TextContent);

        var login = (await host.QueryAsync(q => q.GetLeaderboardAsync()))[0].Login;
        var detail = Render<UserDetailPage>(p => p.Add(x => x.Login, login));
        detail.WaitForState(() => detail.Markup.Contains("Credits and cost"));
        Assert.Contains($"Share of {noun} pool", detail.Markup);
        Assert.Contains($"of the {noun}'s", detail.Markup);
        Assert.Contains($"shared by the whole {noun}.", detail.Markup);
        Assert.DoesNotContain(other, detail.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Leaderboard_note_follows_its_scope_parameter()
    {
        var rows = new List<LeaderboardRow> { new(1, "a", null, 10m, 0.1m, 1m, 1m, false) };
        Assert.Contains("whole enterprise", Render<LeaderboardView>(p => p.Add(x => x.Rows, rows).Add(x => x.ScopeNoun, "enterprise")).Markup);
        Assert.Contains("whole organization", Render<LeaderboardView>(p => p.Add(x => x.Rows, rows)).Markup);
    }

    // ---- organization column ----

    private static async Task SetOrganizationsAsync(TestHost host, Func<int, string?> orgOf) =>
        await host.WithDbAsync(async db =>
        {
            var i = 0;
            foreach (var s in await db.Seats.OrderBy(s => s.Login).ToListAsync()) s.Organization = orgOf(i++);
            await db.SaveChangesAsync();
            return 0;
        });

    [Fact]
    public async Task No_organization_column_when_every_seat_has_the_same_organization_or_none()
    {
        await using var host = await HostAsync(Ent);
        var cut = Render<Users>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);
        Assert.Empty(cut.FindAll("th.c-org"));                       // no organization data at all

        await SetOrganizationsAsync(host, _ => "org-a");
        var same = Render<Users>();
        same.WaitForState(() => same.FindAll("tbody tr").Count > 0);
        Assert.Empty(same.FindAll("th.c-org"));
        Assert.DoesNotContain("org-a", same.Markup);

        var login = (await host.QueryAsync(q => q.GetLeaderboardAsync()))[0].Login;
        var detail = Render<UserDetailPage>(p => p.Add(x => x.Login, login));
        detail.WaitForState(() => detail.Markup.Contains("Credits and cost"));
        Assert.DoesNotContain("org-a", detail.Markup);
    }

    [Fact]
    public async Task Organization_column_and_chip_appear_when_more_than_one_organization_is_present()
    {
        await using var host = await HostAsync(Ent);
        await SetOrganizationsAsync(host, i => i % 2 == 0 ? "org-a" : "org-b");

        var cut = Render<Users>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);
        Assert.Single(cut.FindAll("th.c-org"));
        Assert.Equal(cut.FindAll("tbody tr").Count, cut.FindAll("td.c-org").Count);
        Assert.Contains("org-b", cut.Find("td.c-org").TextContent + string.Join("", cut.FindAll("td.c-org").Select(c => c.TextContent)));

        var login = (await host.QueryAsync(q => q.GetLeaderboardAsync()))[0].Login;
        var detail = Render<UserDetailPage>(p => p.Add(x => x.Login, login));
        detail.WaitForState(() => detail.Markup.Contains("Credits and cost"));
        Assert.Matches("Organization org-[ab]", detail.Markup);
    }

    [Fact]
    public async Task A_seat_with_two_organizations_counts_as_many()
    {
        await using var host = await HostAsync(Ent);
        await SetOrganizationsAsync(host, _ => "org-a, org-b");
        var cut = Render<Users>();
        cut.WaitForState(() => cut.FindAll("tbody tr").Count > 0);
        Assert.Single(cut.FindAll("th.c-org"));
    }
}
