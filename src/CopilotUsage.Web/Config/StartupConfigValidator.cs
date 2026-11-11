using CopilotUsage.Core;
using CopilotUsage.GitHub;

namespace CopilotUsage.Web.Config;

/// <summary>Fail-fast configuration check, run before the host is built. Returns human-readable problems.</summary>
public static class StartupConfigValidator
{
    /// <param name="config">The merged configuration.</param>
    /// <param name="isDevelopment">True only for the Development environment, the only one where sign-in may be switched off.</param>
    public static IReadOnlyList<string> Validate(IConfiguration config, bool isDevelopment)
    {
        var errors = new List<string>();
        DemoOptions demo;
        GitHubOptions gh;
        CopilotOptions copilot;
        SyncOptions sync;
        AzureAdOptions ad;
        AuthOptions auth;
        try
        {
            demo = config.GetSection(DemoOptions.SectionName).Get<DemoOptions>() ?? new();
            gh = config.GetSection(GitHubOptions.SectionName).Get<GitHubOptions>() ?? new();
            copilot = config.GetSection(CopilotOptions.SectionName).Get<CopilotOptions>() ?? new();
            sync = config.GetSection(SyncOptions.SectionName).Get<SyncOptions>() ?? new();
            ad = config.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>() ?? new();
            auth = config.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new();
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
        if (sync.RequestWaitSeconds is < 1 or > 220)
            errors.Add("Sync:RequestWaitSeconds must be between 1 and 220 (App Service cuts a request at about 230 seconds).");

        if (!auth.Enabled && !isDevelopment)
            errors.Add("Auth:Enabled=false is only honoured in the Development environment. Sign-in cannot be switched off here; remove the setting.");
        if (AuthMode.Resolve(config, isDevelopment))
            ValidateSignIn(ad, sync, errors);

        if (string.IsNullOrWhiteSpace(config.GetConnectionString("Default")))
            errors.Add("ConnectionStrings:Default is required (SQLite, e.g. \"Data Source=data/copilot-usage.db\").");

        return errors;
    }

    private static bool IsGuid(string? v) => Guid.TryParseExact(v?.Trim(), "D", out _);

    private static void ValidateSignIn(AzureAdOptions ad, SyncOptions sync, List<string> errors)
    {
        const string How = "Create the Entra app registration as described in docs/AUTH.md and supply the value from configuration (for example the environment variable {0}).";
        if (string.IsNullOrWhiteSpace(ad.TenantId))
            errors.Add("AzureAd:TenantId is required: the directory (tenant) id, a GUID. " + string.Format(How, "AzureAd__TenantId"));
        else if (!IsGuid(ad.TenantId))
            errors.Add("AzureAd:TenantId must be the tenant's GUID. Aliases such as 'common', 'organizations' and 'consumers' accept accounts from other tenants and are refused.");
        if (string.IsNullOrWhiteSpace(ad.ClientId))
            errors.Add("AzureAd:ClientId is required: the application (client) id of the app registration, a GUID. " + string.Format(How, "AzureAd__ClientId"));
        else if (!IsGuid(ad.ClientId))
            errors.Add("AzureAd:ClientId must be a GUID.");
        if (!Uri.TryCreate(ad.Instance, UriKind.Absolute, out var instance) || instance.Scheme != Uri.UriSchemeHttps)
            errors.Add("AzureAd:Instance must be an absolute https URL (default https://login.microsoftonline.com/).");
        if (!string.IsNullOrWhiteSpace(ad.PublicBaseUrl) && (!Uri.TryCreate(ad.PublicBaseUrl, UriKind.Absolute, out var pub) || pub.Scheme != Uri.UriSchemeHttps))
            errors.Add("AzureAd:PublicBaseUrl, when set, must be an absolute https URL such as https://dashboard.contoso.com.");
        if (string.IsNullOrWhiteSpace(sync.AllowedCallerObjectId))
            errors.Add("Sync:AllowedCallerObjectId is required: the object (principal) id of the Azure Function's managed identity, the only caller allowed to run POST /api/sync. Supply it from configuration (Sync__AllowedCallerObjectId).");
        else if (!IsGuid(sync.AllowedCallerObjectId))
            errors.Add("Sync:AllowedCallerObjectId must be a GUID.");
        if (!string.IsNullOrWhiteSpace(sync.AllowedCallerClientId) && !IsGuid(sync.AllowedCallerClientId))
            errors.Add("Sync:AllowedCallerClientId, when set, must be a GUID.");
    }
}
