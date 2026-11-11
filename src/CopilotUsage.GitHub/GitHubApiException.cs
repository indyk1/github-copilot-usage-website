using System.Net;

namespace CopilotUsage.GitHub;

/// <summary>A GitHub call failed after retries, or returned something that is not JSON at all.</summary>
public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message, HttpStatusCode? statusCode = null, string? path = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Path = path;
    }

    public HttpStatusCode? StatusCode { get; }
    public string? Path { get; }
}
