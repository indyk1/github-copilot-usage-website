using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly.Retry;

namespace CopilotUsage.GitHub;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IGitHubClient"/> with the GitHub headers and standard resilience
    /// (retry with exponential backoff and jitter, honouring Retry-After; timeouts; circuit breaker).
    /// Bind <see cref="GitHubOptions"/> separately.
    /// </summary>
    public static IServiceCollection AddGitHubClient(
        this IServiceCollection services, Action<HttpStandardResilienceOptions>? configureResilience = null)
    {
        void Configure(HttpStandardResilienceOptions o)
        {
            o.Retry.MaxRetryAttempts = 4;
            o.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
            o.Retry.UseJitter = true;
            o.Retry.Delay = TimeSpan.FromSeconds(2);
            o.Retry.ShouldRetryAfterHeader = true; // honour Retry-After on 429 / secondary rate limits
            o.Retry.ShouldHandle = args => ValueTask.FromResult(
                HttpClientResiliencePredicates.IsTransient(args.Outcome) || IsSecondaryRateLimit(args));
            o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(30);
            o.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
            o.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(5);
            configureResilience?.Invoke(o);
        }

        services.AddHttpClient<IGitHubClient, GitHubClient>((sp, http) =>
            {
                var o = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                http.BaseAddress = new Uri(string.IsNullOrWhiteSpace(o.BaseUrl) ? "https://api.github.com/" : o.BaseUrl.TrimEnd('/') + "/");
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", string.IsNullOrWhiteSpace(o.ApiVersion) ? GitHubOptions.DefaultApiVersion : o.ApiVersion);
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CopilotUsageDashboard", "1.0"));
                if (!string.IsNullOrWhiteSpace(o.Token))
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", o.Token);
            })
            .AddStandardResilienceHandler(Configure);

        // Signed report URLs must be fetched WITHOUT the GitHub token.
        services.AddHttpClient(GitHubClient.DownloadClientName, http =>
            {
                http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CopilotUsageDashboard", "1.0"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            .AddStandardResilienceHandler(Configure);

        return services;
    }

    /// <summary>Secondary rate limit: 403 that tells us when to retry.</summary>
    private static bool IsSecondaryRateLimit(RetryPredicateArguments<HttpResponseMessage> args) =>
        args.Outcome.Result is { StatusCode: System.Net.HttpStatusCode.Forbidden } r && r.Headers.RetryAfter is not null;
}
