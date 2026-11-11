namespace CopilotUsage.SyncFunction;

/// <summary>Settings of the call to the web app. App setting names use "__" for the colon (SyncApi__BaseUrl).</summary>
public sealed class SyncApiOptions
{
    public const string SectionName = "SyncApi";

    /// <summary>Base URL of the web app, for example https://my-dashboard.azurewebsites.net/. Required.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Token scope: the web app's Application ID URI plus /.default, for example api://{web app client id}/.default. Required in Azure.
    /// Leave empty only for a local run against a web app started with sign-in off (Development): then no token is sent.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>Client id of a user-assigned managed identity. Leave empty to use the Function app's system-assigned identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Per-attempt timeout. App Service ends a request at about 230 s, and the web app answers by 200 s at the latest.</summary>
    public int TimeoutSeconds { get; set; } = 230;

    /// <summary>Total attempts, first try included. Retries cover a sleeping web app waking up.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the first retry; doubles each time.</summary>
    public int InitialBackoffSeconds { get; set; } = 15;

    /// <summary>No new attempt starts if it could not finish inside this many seconds from the first one (the host's function timeout is 10 minutes).</summary>
    public int TotalBudgetSeconds { get; set; } = 540;

    /// <summary>Problems that make the Function unable to run at all, named for the log.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && !u.IsLoopback))
            errors.Add("SyncApi__BaseUrl must be the web app's absolute https URL.");
        if (TimeoutSeconds is < 5 or > 600) errors.Add("SyncApi__TimeoutSeconds must be between 5 and 600.");
        if (MaxAttempts is < 1 or > 10) errors.Add("SyncApi__MaxAttempts must be between 1 and 10.");
        if (InitialBackoffSeconds is < 0 or > 300) errors.Add("SyncApi__InitialBackoffSeconds must be between 0 and 300.");
        return errors;
    }
}
