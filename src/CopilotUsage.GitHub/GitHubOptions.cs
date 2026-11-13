namespace CopilotUsage.GitHub;

/// <summary>GitHub connection settings (config section <c>GitHub</c>).</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";
    public const string DefaultApiVersion = "2026-03-10";

    /// <summary>
    /// Organization login (self-billed organization scope). Either this or <see cref="Enterprise"/> is required unless demo mode is on.
    /// Ignored when <see cref="Enterprise"/> is set.
    /// </summary>
    public string? Org { get; set; }

    /// <summary>
    /// Enterprise slug. When set the app runs in enterprise scope: every call goes to the enterprise endpoints and the
    /// pool is the whole enterprise. Wins over <see cref="Org"/> when both are set.
    /// </summary>
    public string? Enterprise { get; set; }

    /// <summary>
    /// Access token. Comes only from configuration (user secrets / environment / Key Vault reference),
    /// never from a file in the repository.
    /// </summary>
    public string? Token { get; set; }

    public string BaseUrl { get; set; } = "https://api.github.com/";

    /// <summary>Sent as <c>X-GitHub-Api-Version</c>.</summary>
    public string ApiVersion { get; set; } = DefaultApiVersion;

    /// <summary>Page size for paginated endpoints (GitHub maximum is 100).</summary>
    public int PageSize { get; set; } = 100;
}
