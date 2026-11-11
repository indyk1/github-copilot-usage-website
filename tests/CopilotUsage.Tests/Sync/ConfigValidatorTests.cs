using CopilotUsage.Web.Config;
using Microsoft.Extensions.Configuration;

namespace CopilotUsage.Tests.Sync;

public class ConfigValidatorTests
{
    private static IReadOnlyList<string> Validate(params (string Key, string? Value)[] values) =>
        StartupConfigValidator.Validate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Data Source=x.db" }
                .Concat(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).ToDictionary())
            .Build());

    [Fact]
    public void Demo_mode_needs_no_github_settings() =>
        Assert.Empty(Validate(("Demo:Enabled", "true")));

    [Fact]
    public void Live_mode_without_org_and_token_names_both_keys()
    {
        var errors = Validate(("Demo:Enabled", "false"));
        Assert.Contains(errors, e => e.Contains("GitHub:Org") && e.Contains("Demo:Enabled"));
        Assert.Contains(errors, e => e.Contains("GitHub:Token") && e.Contains("never from a file"));
    }

    [Fact]
    public void Live_mode_with_only_an_org_still_fails_on_the_token()
    {
        var errors = Validate(("GitHub:Org", "acme"));
        var single = Assert.Single(errors);
        Assert.Contains("GitHub:Token", single);
    }

    [Fact]
    public void Live_mode_with_org_and_token_is_valid() =>
        Assert.Empty(Validate(("GitHub:Org", "acme"), ("GitHub:Token", "t")));

    [Fact]
    public void Whitespace_counts_as_missing() =>
        Assert.Equal(2, Validate(("GitHub:Org", "  "), ("GitHub:Token", " ")).Count);

    [Fact]
    public void Bad_numbers_are_reported()
    {
        var errors = Validate(("Demo:Enabled", "true"), ("Copilot:CreditsPerSeat", "0"), ("Sync:IntervalHours", "-1"));
        Assert.Contains(errors, e => e.Contains("CreditsPerSeat"));
        Assert.Contains(errors, e => e.Contains("IntervalHours"));
    }

    [Fact]
    public void Unparseable_values_give_a_message_instead_of_an_exception() =>
        Assert.Single(Validate(("Demo:Enabled", "true"), ("Sync:IntervalHours", "often")));
}
