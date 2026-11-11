using System.Text.Json;
using CopilotUsage.Core.Models;
using CopilotUsage.GitHub;
using CopilotUsage.GitHub.Dtos;
using CopilotUsage.GitHub.Json;
using CopilotUsage.Tests.Support;

namespace CopilotUsage.Tests.GitHub;

// All fixtures are synthetic (see Fixtures/README.md).
public class ParsingTests
{
    private static T Parse<T>(string fixture) => JsonSerializer.Deserialize<T>(Fixture.Text(fixture), GitHubJson.Options)!;

    [Fact]
    public void Seat_summary_maps_every_count()
    {
        var s = GitHubMappings.ToSeatSummary(Parse<SeatBillingDto>("seat_billing.json"));
        Assert.Equal(new SeatSummary("business", 25, 2, 3, 1, 20, 5), s);
    }

    [Fact]
    public void Missing_seat_summary_is_all_zero_not_an_error() =>
        Assert.Equal(new SeatSummary(null, 0, 0, 0, 0, 0, 0), GitHubMappings.ToSeatSummary(null));

    [Fact]
    public void Seats_page_maps_assignee_team_and_dates_and_tolerates_missing_fields()
    {
        var seats = Parse<SeatsPageDto>("seats_page1.json").Seats!.Select(GitHubMappings.ToSeat).OfType<SeatInfo>().ToList();
        Assert.Equal(2, seats.Count);

        var alice = seats[0];
        Assert.Equal("octo-alice", alice.Login);
        Assert.Equal("https://avatars.githubusercontent.com/u/1001?v=4", alice.AvatarUrl);
        Assert.Equal("platform", alice.AssigningTeam);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 14, 22, 5, TimeSpan.Zero), alice.LastActivityAt);
        Assert.Equal("vscode/1.109.2/copilot-chat/0.36.1", alice.LastActivityEditor);

        var bob = seats[1];
        Assert.Null(bob.LastActivityAt);
        Assert.Null(bob.AssigningTeam);
        Assert.Null(bob.LastActivityEditor);
    }

    [Fact]
    public void Seat_without_an_assignee_login_is_dropped() =>
        Assert.Null(GitHubMappings.ToSeat(new SeatDto { PlanType = "business" }));

    [Fact]
    public void Credit_usage_for_a_day_maps_credits_amount_and_day()
    {
        var query = new CreditUsageQuery(2026, 11, 9);
        var records = GitHubMappings.ToCreditRecords(Parse<CreditUsageResponseDto>("ai_credit_usage_day.json"), query);

        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(new DateOnly(2026, 11, 9), r.Day));
        Assert.All(records, r => Assert.Null(r.Login));
        var sonnet = records[0];
        Assert.Equal("Claude Sonnet 4.5", sonnet.Model);
        Assert.Equal(812.5m, sonnet.Credits);
        Assert.Equal(8.125m, sonnet.Amount);
    }

    [Fact]
    public void Credit_usage_per_user_takes_login_from_the_response_and_falls_back_when_fields_are_missing()
    {
        var records = GitHubMappings.ToCreditRecords(Parse<CreditUsageResponseDto>("ai_credit_usage_user_month.json"), new CreditUsageQuery(2026, 11, null, "octo-alice"));
        Assert.All(records, r => { Assert.Equal("octo-alice", r.Login); Assert.Null(r.Day); });
        Assert.Equal(1200m, records[0].Credits);
        Assert.Equal(250m, records[1].Credits);          // derived from grossAmount 2.5 at $0.01 per credit
        Assert.Equal(2.5m, records[1].Amount);
        Assert.Equal("ai_credits_unknown_model", records[2].Model); // falls back to sku
        Assert.Equal(0.10m, records[2].Amount);                      // derived from credits
    }

    [Fact]
    public void Unexpected_types_become_null_or_empty_instead_of_throwing()
    {
        var page = Parse<SeatsPageDto>("tolerant_shapes.json");
        Assert.Null(page.TotalSeats);
        Assert.Empty(page.Seats!);

        var usage = Parse<CreditUsageResponseDto>("tolerant_shapes.json");
        var records = GitHubMappings.ToCreditRecords(usage, new CreditUsageQuery(2026, 11, 3));
        var only = Assert.Single(records);
        Assert.Equal("42", only.Model);
        Assert.Equal(750m, only.Credits); // grossQuantity was an object -> ignored; derived from "7.5" dollars

        Assert.Empty(Parse<MetricsReportLinksDto>("tolerant_shapes.json").DownloadLinks!);
    }

    [Fact]
    public void Empty_object_parses_to_empty_dtos()
    {
        Assert.Empty(GitHubMappings.ToCreditRecords(JsonSerializer.Deserialize<CreditUsageResponseDto>("{}", GitHubJson.Options), new CreditUsageQuery()));
        Assert.Empty(GitHubMappings.ToCreditRecords(null, new CreditUsageQuery()));
    }

    [Fact]
    public void Metrics_line_maps_totals_and_aggregates_each_dimension()
    {
        var dto = JsonSerializer.Deserialize<UserMetricsLineDto>(File.ReadLines(Fixture.Path("metrics_users.ndjson")).First(), GitHubJson.Options);
        var m = GitHubMappings.ToUserMetrics(dto)!;

        Assert.Equal("octo-alice", m.Login);
        Assert.Equal(new DateOnly(2026, 11, 9), m.Day);
        Assert.Equal(42.75m, m.AiCredits);
        Assert.Equal(18, m.Prompts);
        Assert.Equal(140, m.SuggestionsGenerated);
        Assert.Equal(52, m.SuggestionsAccepted);
        Assert.Equal(190, m.LocAdded);
        Assert.True(m.UsedChat);
        Assert.True(m.UsedAgent);

        var features = m.Breakdowns.Where(b => b.Dimension == MetricDimension.Feature).ToDictionary(b => b.Key);
        Assert.Equal(3, features.Count);
        Assert.Equal(12, features["chat_panel_agent_mode"].Interactions);
        Assert.Equal(45, features["code_completion"].Acceptances);

        var ide = Assert.Single(m.Breakdowns, b => b.Dimension == MetricDimension.Ide);
        Assert.Equal("vscode", ide.Key);
        Assert.Equal("1.109.2", ide.Version);

        // csharp appears under two features and must be summed into one row.
        var langs = m.Breakdowns.Where(b => b.Dimension == MetricDimension.Language).ToDictionary(b => b.Key);
        Assert.Equal(2, langs.Count);
        Assert.Equal(90, langs["csharp"].Generations);
        Assert.Equal(34, langs["csharp"].Acceptances);
        Assert.Equal(110, langs["csharp"].LocAdded);

        var models = m.Breakdowns.Where(b => b.Dimension == MetricDimension.Model).ToDictionary(b => b.Key);
        Assert.Equal(16, models["claude-sonnet-4.5"].Interactions);
        Assert.Equal(2, models["gpt-5.2"].Interactions);

        var clients = m.Breakdowns.Where(b => b.Dimension == MetricDimension.Client).ToDictionary(b => b.Key);
        Assert.Equal(410_000, clients["vscode"].Tokens);
        Assert.Equal(9, clients["claude-code"].Requests);
        Assert.Equal(42_000, clients["claude-code"].Tokens); // input + output when no total is given
    }

    [Fact]
    public void Metrics_line_without_login_or_day_is_rejected_without_throwing()
    {
        Assert.Null(GitHubMappings.ToUserMetrics(new UserMetricsLineDto { Day = "2026-11-09" }));
        Assert.Null(GitHubMappings.ToUserMetrics(new UserMetricsLineDto { UserLogin = "x", Day = "not a date" }));
        Assert.Null(GitHubMappings.ToUserMetrics(null));
    }

    [Theory]
    [InlineData("2026-11-09", 2026, 11, 9)]
    [InlineData("2026-11-09T23:59:59Z", 2026, 11, 9)]
    [InlineData("2026-11-10T01:00:00+05:00", 2026, 11, 9)] // converted to UTC
    public void Day_parsing(string input, int y, int m, int d) =>
        Assert.Equal(new DateOnly(y, m, d), GitHubMappings.ParseDay(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    public void Bad_days_are_null(string? input) => Assert.Null(GitHubMappings.ParseDay(input));
}
