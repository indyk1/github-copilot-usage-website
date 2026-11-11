using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotUsage.Core.Models;
using CopilotUsage.GitHub.Dtos;
using CopilotUsage.GitHub.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.GitHub;

/// <summary>
/// Typed client. Paths come from <see cref="GitHubEndpoints"/>, mapping from <see cref="GitHubMappings"/>.
/// Retry and rate-limit backoff are applied by the resilience handler (see ServiceCollectionExtensions).
/// </summary>
public sealed class GitHubClient : IGitHubClient
{
    /// <summary>Name of the un-authenticated client used for signed report URLs.</summary>
    public const string DownloadClientName = "github-report-download";

    private const int MaxPages = 200;

    private readonly HttpClient _api;
    private readonly IHttpClientFactory _factory;
    private readonly GitHubOptions _options;
    private readonly ILogger<GitHubClient> _logger;

    public GitHubClient(HttpClient api, IHttpClientFactory factory, IOptions<GitHubOptions> options, ILogger<GitHubClient> logger)
    {
        _api = api;
        _factory = factory;
        _options = options.Value;
        _logger = logger;
    }

    private string Org =>
        string.IsNullOrWhiteSpace(_options.Org)
            ? throw new InvalidOperationException("GitHub:Org is not configured.")
            : _options.Org.Trim();

    public async Task<SeatSummary> GetSeatSummaryAsync(CancellationToken ct = default) =>
        GitHubMappings.ToSeatSummary(await GetAsync<SeatBillingDto>(GitHubEndpoints.SeatSummary(Org), ct));

    public async Task<IReadOnlyList<SeatInfo>> GetSeatsAsync(CancellationToken ct = default)
    {
        var perPage = Math.Clamp(_options.PageSize, 1, 100);
        var seats = new List<SeatInfo>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var dto = await GetAsync<SeatsPageDto>(GitHubEndpoints.Seats(Org, page, perPage), ct);
            var rows = dto?.Seats ?? [];
            seats.AddRange(rows.Select(GitHubMappings.ToSeat).OfType<SeatInfo>());
            if (rows.Count < perPage) break;
        }
        // Defensive: a seat must not appear twice (would break the unique login key downstream).
        return seats.GroupBy(s => s.Login, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
    }

    public async Task<IReadOnlyList<CreditUsageRecord>> GetCreditUsageAsync(CreditUsageQuery query, CancellationToken ct = default)
    {
        if (query.Day is not null && (query.Year is null || query.Month is null))
            throw new ArgumentException("A day filter requires year and month.", nameof(query));
        var dto = await GetAsync<CreditUsageResponseDto>(GitHubEndpoints.CreditUsage(Org, query), ct);
        return GitHubMappings.ToCreditRecords(dto, query);
    }

    public async Task<MetricsReport> GetUserMetricsAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        var path = day is { } d ? GitHubEndpoints.UserMetricsForDay(Org, d) : GitHubEndpoints.UserMetricsLatest28Day(Org);
        var links = await GetAsync<MetricsReportLinksDto>(path, ct);

        var users = new List<UserDailyMetrics>();
        var malformed = 0;
        foreach (var url in links?.DownloadLinks ?? [])
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                _logger.LogWarning("Skipping report link that is not an absolute https URL.");
                continue;
            }

            using var download = _factory.CreateClient(DownloadClientName);
            using var response = await download.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new GitHubApiException($"Report download failed with {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode, "(signed report url)");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await foreach (var line in NdjsonReader.ReadAsync<UserMetricsLineDto>(stream, (n, msg) => malformed++, ct))
            {
                if (GitHubMappings.ToUserMetrics(line) is { } m) users.Add(m);
                else malformed++;
            }
        }

        if (malformed > 0) _logger.LogWarning("Skipped {Count} malformed or incomplete metrics lines.", malformed);

        var start = GitHubMappings.ParseDay(links?.ReportStartDay ?? links?.ReportDay) ?? (users.Count > 0 ? users.Min(u => u.Day) : null);
        var end = GitHubMappings.ParseDay(links?.ReportEndDay ?? links?.ReportDay) ?? (users.Count > 0 ? users.Max(u => u.Day) : null);
        return new MetricsReport(start, end, users, malformed);
    }

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken ct) where T : class
    {
        using var response = await _api.GetAsync(pathAndQuery.TrimStart('/'), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct);
            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => " (token rejected)",
                HttpStatusCode.Forbidden => " (token lacks the required scope/permission, or rate limited)",
                HttpStatusCode.NotFound => " (path wrong, feature/policy not enabled, or no access to the org)",
                _ => "",
            };
            throw new GitHubApiException(
                $"GET {StripQuery(pathAndQuery)} failed: {(int)response.StatusCode} {response.StatusCode}{hint}. {body}".TrimEnd(),
                response.StatusCode, pathAndQuery);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(GitHubJson.Options, ct);
        }
        catch (JsonException ex)
        {
            // Field-level mismatches are tolerated by the converters; this is a body that is not JSON of the expected kind.
            throw new GitHubApiException($"GET {StripQuery(pathAndQuery)} returned a body that is not the expected JSON object.", response.StatusCode, pathAndQuery, ex);
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            var s = await r.Content.ReadAsStringAsync(ct);
            return s.Length <= 300 ? s : s[..300];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "";
        }
    }

    private static string StripQuery(string p) => p.Split('?')[0];
}
