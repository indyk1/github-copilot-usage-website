namespace CopilotUsage.Web.Config;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>
    /// How often a sync is EXPECTED to happen (hours). In production the Azure Function's timer is the scheduler and this app
    /// does not run one; this value only decides when /healthz calls the last successful sync stale (three intervals).
    /// Keep it equal to the Function's schedule. With <see cref="InAppTimer"/> it is also the timer's period.
    /// </summary>
    public double IntervalHours { get; set; } = 4;

    /// <summary>
    /// Run the in-process timer (SyncWorker). Off by default: the web app sleeps on the Free plan, so the Azure Function
    /// calls POST /api/sync instead. Switch on for local live runs only.
    /// </summary>
    public bool InAppTimer { get; set; }

    /// <summary>With <see cref="InAppTimer"/>: sync once at startup (real mode only; demo data is seeded before the host starts).</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>
    /// How long POST /api/sync waits for the sync to finish before answering 202 and letting it carry on in the background.
    /// Must stay below App Service's roughly 230 second request limit.
    /// </summary>
    public int RequestWaitSeconds { get; set; } = 200;

    /// <summary>Object id (oid) of the managed identity allowed to call POST /api/sync. Required whenever sign-in is on.</summary>
    public string? AllowedCallerObjectId { get; set; }

    /// <summary>Optional: also require the caller's application (client) id (azp / appid claim) to equal this.</summary>
    public string? AllowedCallerClientId { get; set; }
}

/// <summary>Microsoft Entra ID settings. The app registration is created by hand; everything about it arrives here.</summary>
public sealed class AzureAdOptions
{
    public const string SectionName = "AzureAd";

    /// <summary>Login host, with trailing slash. Default is the public cloud.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>Directory (tenant) id, a GUID. Never "common", "organizations" or "consumers".</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) id of the app registration, a GUID.</summary>
    public string? ClientId { get; set; }

    /// <summary>Application ID URI, used as an accepted audience for the sync token. Default: api://{ClientId}.</summary>
    public string? AppIdUri { get; set; }

    /// <summary>
    /// Optional public origin of the site (for example https://dashboard.contoso.com). When set, the sign-in and sign-out redirect URIs are built
    /// from it instead of the request's host. Leave empty to use the host the browser used.
    /// </summary>
    public string? PublicBaseUrl { get; set; }
}

/// <summary>Sign-in switch. Only honoured in Development; any other environment always requires sign-in.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public bool Enabled { get; set; } = true;
}

/// <summary>Whether sign-in is in force for this process, resolved once at startup.</summary>
public sealed record AuthMode(bool Enabled)
{
    public static bool Resolve(IConfiguration config, bool isDevelopment) =>
        !(isDevelopment && config.GetSection(AuthOptions.SectionName).Get<AuthOptions>() is { Enabled: false });
}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>When true GitHub is never called; the database is filled with deterministic demo data.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which demo dataset to show: normal (default), over (pool exhausted with overage) or empty (seats, no usage yet).</summary>
    public string Scenario { get; set; } = "normal";
}
