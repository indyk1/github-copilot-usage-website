using CopilotUsage.GitHub;
using CopilotUsage.GitHub.Dtos;
using CopilotUsage.Tests.Support;

namespace CopilotUsage.Tests.GitHub;

public class NdjsonReaderTests
{
    [Fact]
    public async Task Reads_valid_lines_skips_blank_and_reports_malformed_ones_with_their_line_numbers()
    {
        var bad = new List<int>();
        await using var stream = Fixture.Open("metrics_users.ndjson");
        var lines = await NdjsonReader.ReadAllAsync<UserMetricsLineDto>(stream, (n, _) => bad.Add(n));

        // lines: 1 alice, 2 bob, 3 truncated JSON, 4 blank-ish, 5 no login (valid JSON), 6 a bare string, 7 alice day 2
        Assert.Equal(4, lines.Count); // alice, bob, the no-login object, alice day 2
        Assert.Equal([3, 6], bad.Order());
        Assert.Equal(["octo-alice", "octo-bob", null, "octo-alice"], lines.Select(l => l.UserLogin));
    }

    [Fact]
    public async Task Wrongly_typed_fields_in_a_line_are_tolerated()
    {
        await using var stream = Fixture.Open("metrics_users.ndjson");
        var last = (await NdjsonReader.ReadAllAsync<UserMetricsLineDto>(stream)).Last();

        Assert.Equal(12.5m, last.AiCreditsUsed);                    // "12.5" string
        Assert.True(last.UsedChat);                                  // "true" string
        Assert.Equal(7, last.UserInitiatedInteractionCount);         // "7" string
        Assert.Empty(last.TotalsByIde!);                             // "oops" where an array belongs
        Assert.Single(last.TotalsByFeature!);                        // stray elements dropped
        Assert.Equal(new DateOnly(2026, 11, 10), GitHubMappings.ParseDay(last.Day));
    }

    [Fact]
    public async Task Empty_stream_yields_nothing()
    {
        using var empty = new MemoryStream();
        Assert.Empty(await NdjsonReader.ReadAllAsync<UserMetricsLineDto>(empty));
    }

    [Fact]
    public async Task Handles_crlf_and_missing_trailing_newline()
    {
        using var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"user_login\":\"a\",\"day\":\"2026-11-01\"}\r\n{\"user_login\":\"b\",\"day\":\"2026-11-01\"}"));
        var items = await NdjsonReader.ReadAllAsync<UserMetricsLineDto>(s);
        Assert.Equal(["a", "b"], items.Select(i => i.UserLogin));
    }

    [Fact]
    public async Task JSON_null_line_is_reported_as_malformed()
    {
        var bad = 0;
        using var s = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("null\n{\"user_login\":\"a\"}\n"));
        var items = await NdjsonReader.ReadAllAsync<UserMetricsLineDto>(s, (_, _) => bad++);
        Assert.Single(items);
        Assert.Equal(1, bad);
    }
}
