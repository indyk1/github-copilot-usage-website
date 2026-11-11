using CopilotUsage.Data.Queries;
using CopilotUsage.Web.Ui;

namespace CopilotUsage.Tests.Ui;

public class UsersTableTests
{
    private static UserRow Row(string login, decimal credits = 0m, long prompts = 0, decimal? accept = null, string? model = null, DateTimeOffset? last = null, bool seat = true) =>
        new(login, null, 0, credits, credits / 19m, prompts, accept, model, last, seat);

    private static readonly UserRow[] Rows =
    [
        Row("maya-chen", 2400m, 470, 0.31m, "GPT-5", new DateTimeOffset(2026, 11, 11, 0, 0, 0, TimeSpan.Zero)),
        Row("Ana-Gomez", 100m, 20, 0.50m, "Claude", new DateTimeOffset(2026, 11, 3, 0, 0, 0, TimeSpan.Zero)),
        Row("zed", 900m, 90, null, null, null),
        Row("idle-one"),
    ];

    private static string[] Logins(IEnumerable<UserRow> rows) => rows.Select(r => r.Login).ToArray();

    [Fact]
    public void Filter_matches_part_of_the_login_ignoring_case_and_padding()
    {
        Assert.Equal(["maya-chen"], Logins(UsersTable.Filter(Rows, "  CHEN ")));
        Assert.Equal(["maya-chen", "Ana-Gomez", "zed"], Logins(UsersTable.Filter(Rows, "e").Where(r => r.Login != "idle-one")));
        Assert.Empty(UsersTable.Filter(Rows, "nobody"));
        Assert.Equal(4, UsersTable.Filter(Rows, "").Count);
        Assert.Equal(4, UsersTable.Filter(Rows, null).Count);
    }

    [Fact]
    public void Sort_by_credits_puts_the_highest_first_and_can_be_reversed()
    {
        Assert.Equal(["maya-chen", "zed", "Ana-Gomez", "idle-one"], Logins(UsersTable.Sort(Rows, UserSort.Credits, true)));
        Assert.Equal(["idle-one", "Ana-Gomez", "zed", "maya-chen"], Logins(UsersTable.Sort(Rows, UserSort.Credits, false)));
    }

    [Fact]
    public void Sort_by_login_ignores_case()
    {
        Assert.Equal(["Ana-Gomez", "idle-one", "maya-chen", "zed"], Logins(UsersTable.Sort(Rows, UserSort.User, false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rows_with_no_value_stay_last_in_both_directions(bool descending)
    {
        var byAcceptance = Logins(UsersTable.Sort(Rows, UserSort.Acceptance, descending));
        Assert.Equal(["idle-one", "zed"], byAcceptance[^2..]);      // no acceptance rate, A to Z among themselves
        var byModel = Logins(UsersTable.Sort(Rows, UserSort.TopModel, descending));
        Assert.Equal(["idle-one", "zed"], byModel[^2..]);
        var byLast = Logins(UsersTable.Sort(Rows, UserSort.LastActive, descending));
        Assert.Equal(["idle-one", "zed"], byLast[^2..]);
    }

    [Fact]
    public void Sort_by_last_active_and_prompts()
    {
        Assert.Equal("maya-chen", UsersTable.Sort(Rows, UserSort.LastActive, true)[0].Login);
        Assert.Equal("Ana-Gomez", UsersTable.Sort(Rows, UserSort.LastActive, false)[0].Login);
        Assert.Equal("maya-chen", UsersTable.Sort(Rows, UserSort.Prompts, true)[0].Login);
    }

    [Fact]
    public void Ties_fall_back_to_login()
    {
        var tied = new[] { Row("b", 5m), Row("a", 5m), Row("c", 5m) };
        Assert.Equal(["a", "b", "c"], Logins(UsersTable.Sort(tied, UserSort.Credits, true)));
        Assert.Equal(["a", "b", "c"], Logins(UsersTable.Sort(tied, UserSort.Credits, false)));
    }

    [Fact]
    public void Toggle_flips_the_current_column_and_starts_others_at_their_default()
    {
        Assert.Equal((UserSort.Credits, false), UsersTable.Toggle(UserSort.Credits, true, UserSort.Credits));
        Assert.Equal((UserSort.User, false), UsersTable.Toggle(UserSort.Credits, true, UserSort.User));     // text: A to Z
        Assert.Equal((UserSort.Prompts, true), UsersTable.Toggle(UserSort.User, false, UserSort.Prompts));   // numbers: highest first
        Assert.Equal((UserSort.TopModel, false), UsersTable.Toggle(UserSort.Prompts, true, UserSort.TopModel));
    }

    [Fact]
    public void Aria_sort_names_the_current_column_only()
    {
        Assert.Equal("descending", UsersTable.AriaSort(UserSort.Credits, UserSort.Credits, true));
        Assert.Equal("ascending", UsersTable.AriaSort(UserSort.Credits, UserSort.Credits, false));
        Assert.Equal("none", UsersTable.AriaSort(UserSort.Prompts, UserSort.Credits, true));
    }

    [Fact]
    public void Inactive_means_no_credits_and_no_prompts()
    {
        Assert.True(UsersTable.IsInactive(Row("a")));
        Assert.False(UsersTable.IsInactive(Row("a", credits: 1m)));
        Assert.False(UsersTable.IsInactive(Row("a", prompts: 3)));
    }

    [Fact]
    public void Query_keys_round_trip()
    {
        foreach (var s in Enum.GetValues<UserSort>()) Assert.Equal(s, UsersTable.ParseKey(UsersTable.Key(s)));
        Assert.Null(UsersTable.ParseKey("nonsense"));
        Assert.Null(UsersTable.ParseKey(null));
    }
}

public class LabelsTests
{
    [Theory]
    [InlineData("chat_panel_agent_mode", "Chat, agent mode")]
    [InlineData("CODE_COMPLETION", "Code completions")]
    [InlineData("copilot_cli", "Copilot CLI")]
    [InlineData("brand_new_thing", "Brand new thing")]      // unknown keys fall back to readable words
    [InlineData("some-other-key", "Some other key")]
    [InlineData("", "Unknown")]
    [InlineData(null, "Unknown")]
    public void Feature(string? key, string expected) => Assert.Equal(expected, Labels.Feature(key));

    [Theory]
    [InlineData("vscode", "VS Code")]
    [InlineData("intellij", "IntelliJ IDEA")]
    [InlineData("zed_editor", "Zed editor")]
    public void Ide(string key, string expected) => Assert.Equal(expected, Labels.Ide(key));

    [Theory]
    [InlineData("claude-code", "Claude Code")]
    [InlineData("jetbrains", "JetBrains IDEs")]
    [InlineData("some-new-agent", "Some new agent")]
    public void Client(string key, string expected) => Assert.Equal(expected, Labels.Client(key));

    [Theory]
    [InlineData("csharp", "C#")]
    [InlineData("typescript", "TypeScript")]
    [InlineData("elixir", "Elixir")]
    public void Language(string key, string expected) => Assert.Equal(expected, Labels.Language(key));
}

public class ActivityViewTests
{
    private static ActivityItem Item(string key, long inter = 0, long gen = 0, long req = 0, long tokens = 0, long loc = 0, string? version = null) =>
        new(key, inter, gen, 0, null, loc, 0, req, tokens, version);

    [Fact]
    public void Picks_the_most_meaningful_counter_and_names_its_unit()
    {
        Assert.Equal("12 prompts", ActivityView.Describe(Item("a", inter: 12, gen: 99)));
        Assert.Equal("1 prompt", ActivityView.Describe(Item("a", inter: 1)));
        Assert.Equal("2,041 requests", ActivityView.Describe(Item("a", req: 2041)));
        Assert.Equal("1,685 suggestions", ActivityView.Describe(Item("a", gen: 1685)));
        Assert.Equal("40 lines", ActivityView.Describe(Item("a", loc: 40)));
    }

    [Fact]
    public void Detail_shows_version_and_tokens_where_present()
    {
        Assert.Equal("v1.109.2", ActivityView.Detail(Item("a", inter: 1, version: "1.109.2")));
        Assert.Equal("v2026.2.1 · 16.6M tokens", ActivityView.Detail(Item("a", req: 5, tokens: 16_600_000, version: "v2026.2.1")));
        Assert.Null(ActivityView.Detail(Item("a", inter: 1)));
    }

    [Fact]
    public void Rows_are_ranked_skip_empty_items_and_fold_the_tail()
    {
        var items = Enumerable.Range(1, 8).Select(i => Item($"k{i}", inter: i * 10)).Append(Item("zero")).ToList();
        var rows = ActivityView.Rows(items, k => k!.ToUpperInvariant(), max: 6);
        Assert.Equal(7, rows.Count);
        Assert.Equal("K8", rows[0].Label);
        Assert.DoesNotContain(rows, r => r.Label == "ZERO");
        Assert.Equal("Other (2)", rows[^1].Label);
        Assert.Equal(30, rows[^1].Count);       // k2 (20) + k1 (10)
    }
}

public class FmtActivityTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 15, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("2026-11-11T00:00:00Z", "today")]
    [InlineData("2026-11-10T23:00:00Z", "yesterday")]
    [InlineData("2026-11-06T00:00:00Z", "5 days ago")]
    [InlineData("2026-09-20T00:00:00Z", "20 Sep")]
    public void Relative_day_never_claims_hours(string when, string expected) =>
        Assert.Equal(expected, Fmt.RelativeDay(DateTimeOffset.Parse(when), Now));

    [Fact] public void Number_uses_thousands_separators() => Assert.Equal("12,480", Fmt.Number(12480));
}
