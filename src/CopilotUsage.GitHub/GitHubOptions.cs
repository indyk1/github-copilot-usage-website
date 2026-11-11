namespace CopilotUsage.GitHub;

/// <summary>GitHub connection settings (config section <c>GitHub</c>).</summary>
public sealed class GitHubOptions
{
    public const string SectionName = "GitHub";
    public const string DefaultApiVersion = "2026-03-10";

    /// <summary>Organization login. Required unless demo mode is on.</summary>
    public string? Org { get; set; }

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
