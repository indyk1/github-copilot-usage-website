namespace CopilotUsage.GitHub;

public enum GitHubScopeKind { Organization, Enterprise }

/// <summary>
/// Whether the app reads one self-billed organization or a whole enterprise (the billing entity that pools the credits).
/// Enterprise wins when both <c>GitHub:Enterprise</c> and <c>GitHub:Org</c> are set (documented in README.md and docs/DEPLOY.md).
/// </summary>
public sealed record GitHubScope(GitHubScopeKind Kind, string? Slug)
{
    public bool IsEnterprise => Kind == GitHubScopeKind.Enterprise;

    /// <summary>"enterprise" or "organization", for sentences.</summary>
    public string Noun => IsEnterprise ? "enterprise" : "organization";

    /// <summary>Resolves the scope from the options. With neither set (demo mode) it is the organization scope with no slug.</summary>
    public static GitHubScope From(GitHubOptions o) =>
        !string.IsNullOrWhiteSpace(o.Enterprise) ? new(GitHubScopeKind.Enterprise, o.Enterprise.Trim())
        : new(GitHubScopeKind.Organization, string.IsNullOrWhiteSpace(o.Org) ? null : o.Org.Trim());

    /// <summary>Demo data always behaves like an organization.</summary>
    public static GitHubScope Resolve(GitHubOptions o, bool demo) =>
        demo ? new(GitHubScopeKind.Organization, null) : From(o);

    public static bool BothSet(GitHubOptions o) =>
        !string.IsNullOrWhiteSpace(o.Enterprise) && !string.IsNullOrWhiteSpace(o.Org);
}
