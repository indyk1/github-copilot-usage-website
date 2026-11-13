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

    private GitHubScope Scope => GitHubScope.From(_options);

    private string OrgSlug =>
        string.IsNullOrWhiteSpace(_options.Org)
            ? throw new InvalidOperationException("GitHub:Org is not configured.")
            : _options.Org.Trim();

    public async Task<SeatSummary> GetSeatSummaryAsync(CancellationToken ct = default)
    {
        // Enterprise scope: no summary endpoint is known, so the figures are derived from the seat list.
        if (Scope.IsEnterprise)
            return GitHubMappings.DeriveSeatSummary((await GetSeatListAsync(ct)).Seats, DateTimeOffset.UtcNow);
        return GitHubMappings.ToSeatSummary(await GetAsync<SeatBillingDto>(GitHubEndpoints.SeatSummary(OrgSlug), GitHubCall.SeatSummary, ct));
    }

    public async Task<IReadOnlyList<SeatInfo>> GetSeatsAsync(CancellationToken ct = default) =>
        (await GetSeatListAsync(ct)).Seats;

    public async Task<SeatList> GetSeatListAsync(CancellationToken ct = default)
    {
        var scope = Scope;
        var perPage = Math.Clamp(_options.PageSize, 1, 100);
        var rows = new List<SeatInfo>();
        int? reported = null;
        for (var page = 1; page <= MaxPages; page++)
        {
            var dto = await GetAsync<SeatsPageDto>(GitHubEndpoints.Seats(scope, page, perPage), GitHubCall.Seats, ct);
            reported ??= dto?.TotalSeats;
            var pageRows = dto?.Seats ?? [];
            rows.AddRange(pageRows.Select(GitHubMappings.ToSeat).OfType<SeatInfo>());
            if (pageRows.Count < perPage) break;
        }
        // A seat must not appear twice (unique login key downstream). In enterprise scope a user reachable through several
        // organizations may come back once per organization: merge those rows (organizations are joined).
        var seats = rows.GroupBy(s => s.Login, StringComparer.OrdinalIgnoreCase).Select(g => GitHubMappings.MergeSeatRows(g.ToList())).ToList();
        if (reported is { } r && r != seats.Count)
            _logger.LogWarning("The seat list holds {Distinct} distinct users but GitHub reported total_seats={Reported}; the list is used.", seats.Count, r);
        return new SeatList(seats, reported);
    }

    public async Task<IReadOnlyList<CreditUsageRecord>> GetCreditUsageAsync(CreditUsageQuery query, CancellationToken ct = default)
    {
        if (query.Day is not null && (query.Year is null || query.Month is null))
            throw new ArgumentException("A day filter requires year and month.", nameof(query));
        var dto = await GetAsync<CreditUsageResponseDto>(GitHubEndpoints.CreditUsage(Scope, query), GitHubCall.CreditUsage, ct);
        return GitHubMappings.ToCreditRecords(dto, query);
    }

    public async Task<MetricsReport> GetUserMetricsAsync(DateOnly? day = null, CancellationToken ct = default)
    {
        var path = day is { } d ? GitHubEndpoints.UserMetricsForDay(Scope, d) : GitHubEndpoints.UserMetricsLatest28Day(Scope);
        var links = await GetAsync<MetricsReportLinksDto>(path, GitHubCall.Metrics, ct);

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
            {
                // Never log the URL: it carries a signature.
                _logger.LogError("GitHub report download failed with HTTP {Status} (signed report URL, not logged).", (int)response.StatusCode);
                throw new GitHubApiException($"Report download failed with {(int)response.StatusCode} {response.StatusCode}.", response.StatusCode, "(signed report url)");
            }

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

    private async Task<T?> GetAsync<T>(string pathAndQuery, GitHubCall call, CancellationToken ct) where T : class
    {
        var path = StripQuery(pathAndQuery);
        using var response = await _api.GetAsync(pathAndQuery.TrimStart('/'), ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(response, ct);
            var hint = GitHubFailureHints.For(Scope, call, response.StatusCode);
            // The log carries the path (no query string), the status, GitHub's own message and the hint. Never the token.
            _logger.LogError("GitHub call {Call} failed: GET {Path} returned HTTP {Status} {Reason} in {ScopeKind} scope. GitHub said: {GitHubMessage}. Hint: {Hint}",
                call, path, (int)response.StatusCode, response.StatusCode, Scope.Kind, string.IsNullOrEmpty(body) ? "(empty body)" : body, hint ?? "(none)");
            throw new GitHubApiException(
                $"GET {path} failed: {(int)response.StatusCode} {response.StatusCode}.{(hint is null ? "" : " " + hint)}{(string.IsNullOrEmpty(body) ? "" : " GitHub said: " + body)}",
                response.StatusCode, path, hint: hint, gitHubMessage: body);
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<T>(GitHubJson.Options, ct);
        }
        catch (JsonException ex)
        {
            // Field-level mismatches are tolerated by the converters; this is a body that is not JSON of the expected kind.
            _logger.LogError("GitHub call {Call}: GET {Path} returned HTTP {Status} with a body that is not the expected JSON object.", call, path, (int)response.StatusCode);
            throw new GitHubApiException($"GET {path} returned a body that is not the expected JSON object.", response.StatusCode, path, ex);
        }
    }

    /// <summary>GitHub's error message (its JSON "message" if present, else the raw text), one line, at most 300 characters, token removed.</summary>
    private string SafeRead(string raw)
    {
        var text = raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                text = m.GetString() ?? raw;
        }
        catch (JsonException) { }
        var token = _options.Token;
        if (!string.IsNullOrWhiteSpace(token)) text = text.Replace(token.Trim(), "***", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= 300 ? text : text[..300] + "...";
    }

    private async Task<string> SafeReadAsync(HttpResponseMessage r, CancellationToken ct)
    {
        try
        {
            return SafeRead(await r.Content.ReadAsStringAsync(ct));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return "";
        }
    }

    private static string StripQuery(string p) => p.Split('?')[0];
}
