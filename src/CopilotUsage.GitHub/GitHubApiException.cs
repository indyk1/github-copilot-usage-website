using System.Net;

namespace CopilotUsage.GitHub;

/// <summary>Which kind of GitHub call failed (used to pick a hint).</summary>
public enum GitHubCall { SeatSummary, Seats, CreditUsage, Metrics, ReportDownload }

/// <summary>A GitHub call failed after retries, or returned something that is not JSON at all.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message, HttpStatusCode? statusCode = null, string? path = null, Exception? inner = null,
        string? hint = null, string? gitHubMessage = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Path = path;
        Hint = hint;
        GitHubMessage = gitHubMessage;
    }

    public HttpStatusCode? StatusCode { get; }

    /// <summary>Request path without the query string.</summary>
    public string? Path { get; }

    /// <summary>What to check next. Contains no names, tokens or response data, so it is safe to show to dashboard users.</summary>
    public string? Hint { get; }

    /// <summary>GitHub's own error message, truncated and with the token removed. For the application log only, never for the page.</summary>
    public string? GitHubMessage { get; }
}

/// <summary>The hints. Plain text, free of names and secrets.</summary>
public static class GitHubFailureHints
{
    public static string? For(GitHubScope scope, GitHubCall call, HttpStatusCode? status)
    {
        if (status is null) return null;
        if (status == HttpStatusCode.Unauthorized)
            return "The token was rejected: it may be wrong, expired or revoked.";

        var billingOrSeats = call is GitHubCall.SeatSummary or GitHubCall.Seats or GitHubCall.CreditUsage;
        if (!scope.IsEnterprise && billingOrSeats && status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            return "This organization may be billed through a GitHub Enterprise account, which holds the Copilot billing. If so, set GitHub:Enterprise to the enterprise slug (docs/DEPLOY.md, section 1).";
        if (scope.IsEnterprise && status == HttpStatusCode.Forbidden)
            return "Check the token: the enterprise endpoints need a classic personal access token created by an enterprise owner or billing manager, with the scopes manage_billing:copilot and read:enterprise, authorised for SAML single sign-on if it is enforced (docs/DEPLOY.md, section 1).";
        if (scope.IsEnterprise && status == HttpStatusCode.NotFound)
            return "Check the enterprise slug, and whether this endpoint path is right (docs/API-ASSUMPTIONS.md, enterprise scope).";
        if (status == HttpStatusCode.Forbidden)
            return "The token lacks a required scope or permission, or a rate limit was hit.";
        if (status == HttpStatusCode.NotFound)
            return "The path is wrong, the feature or policy is not enabled, or the token cannot see it.";
        return null;
    }
}
