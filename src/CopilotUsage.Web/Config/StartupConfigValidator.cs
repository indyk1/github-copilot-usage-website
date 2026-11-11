using CopilotUsage.Core;
using CopilotUsage.GitHub;

namespace CopilotUsage.Web.Config;

/// <summary>Fail-fast configuration check, run before the host is built. Returns human-readable problems.</summary>
public static class StartupConfigValidator
{
    public static IReadOnlyList<string> Validate(IConfiguration config)
    {
        var errors = new List<string>();
        DemoOptions demo;
        GitHubOptions gh;
        CopilotOptions copilot;
        SyncOptions sync;
        try
        {
            demo = config.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new();
            gh = config.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new();
            copilot = config.GetSection(CopilotOptions.SectionName).Get<CopilotOptions>() ?? new();
            sync = config.GetSection(SyncOptions.SectionName).Get<SyncOptions>() ?? new();
        }
        catch (InvalidOperationException ex)
        {
            return [$"A configuration value has the wrong type: {ex.InnerException?.Message ?? ex.Message}"];
        }

        if (!demo.Enabled)
        {
            if (string.IsNullOrWhiteSpace(gh.Org))
                errors.Add("GitHub:Org is required when Demo:Enabled is false. Set it in configuration (e.g. environment variable GitHub__Org).");
            if (string.IsNullOrWhiteSpace(gh.Token))
                errors.Add("GitHub:Token is required when Demo:Enabled is false. Supply it from configuration only (user secrets, environment variable GitHub__Token, or a Key Vault reference), never from a file in the repo. To explore without GitHub set Demo:Enabled=true.");
            else if (gh.Token.TrimStart().StartsWith("@Microsoft.KeyVault", StringComparison.OrdinalIgnoreCase))
                errors.Add("GitHub:Token is still the text of a Key Vault reference, so App Service could not resolve it. Check that the web app's managed identity can read the secret and that the reference is spelled correctly (Configuration, then the green tick next to the setting).");
        }

        if (!CopilotUsage.Data.Demo.DemoScenarios.TryParse(demo.Scenario, out _))
            errors.Add($"Demo:Scenario '{demo.Scenario}' is not recognised. Use one of: {CopilotUsage.Data.Demo.DemoScenarios.Allowed}.");
        if (copilot.CreditsPerSeat <= 0)
            errors.Add("Copilot:CreditsPerSeat must be greater than zero.");
        if (!(sync.IntervalHours > 0) || double.IsInfinity(sync.IntervalHours))
            errors.Add("Sync:IntervalHours must be greater than zero.");
        if (!Uri.TryCreate(gh.BaseUrl, UriKind.Absolute, out _))
            errors.Add("GitHub:BaseUrl must be an absolute URL.");
        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Default")))
            errors.Add("ConnectionStrings:Default is required (SQLite, e.g. \"Data Source=data/copilot-usage.db\").");

        return errors;
    }
}
