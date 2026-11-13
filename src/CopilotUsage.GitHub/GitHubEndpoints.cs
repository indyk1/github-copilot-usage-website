using CopilotUsage.Core.Models;

namespace CopilotUsage.GitHub;

/// <summary>
/// EVERY GitHub REST path used by the app lives here, for both scopes. Most are unverified assumptions (PLAN.md 3.5,
/// docs/API-ASSUMPTIONS.md): correcting one is a one-line change in this file.
/// </summary>
public static class GitHubEndpoints
{
    /// <summary>Organization scope only (documented). There is no known enterprise equivalent: the summary is derived from the seat list.</summary>
    public static string SeatSummary(string org) => $"/orgs/{Esc(org)}/copilot/billing";

    // Documented (Copilot user management API), both scopes.
    public static string Seats(GitHubScope scope, int page, int perPage) =>
        $"{CopilotBase(scope)}/billing/seats?per_page={perPage}&page={page}";

    // Organization path from plan 3.2 (note "organizations", not "orgs"); the enterprise path mirrors the documented
    // enterprise premium_request/usage path. Schema unverified in both scopes.
    public static string CreditUsage(GitHubScope scope, CreditUsageQuery query)
    {
        var parts = new List<string>();
        if (query.Year is { } y) parts.Add($"year={y}");
        if (query.Month is { } m) parts.Add($"month={m}");
        if (query.Day is { } d) parts.Add($"day={d}");
        if (!string.IsNullOrWhiteSpace(query.User)) parts.Add($"user={Esc(query.User)}");
        if (!string.IsNullOrWhiteSpace(query.Model)) parts.Add($"model={Esc(query.Model)}");
        var qs = parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        var root = scope.IsEnterprise ? $"/enterprises/{Esc(Slug(scope))}" : $"/organizations/{Esc(Slug(scope))}";
        return $"{root}/settings/billing/ai_credit/usage{qs}";
    }

    // Plan 3.3. Paths unverified (plan 3.5 item 1); the enterprise ones mirror the organization ones. Each returns signed URLs to NDJSON files.
    public static string UserMetricsLatest28Day(GitHubScope scope) =>
        $"{CopilotBase(scope)}/metrics/reports/users-28-day/latest";

    public static string UserMetricsForDay(GitHubScope scope, DateOnly day) =>
        $"{CopilotBase(scope)}/metrics/reports/users-1-day?day={day:yyyy-MM-dd}";

    private static string CopilotBase(GitHubScope scope) =>
        scope.IsEnterprise ? $"/enterprises/{Esc(Slug(scope))}/copilot" : $"/orgs/{Esc(Slug(scope))}/copilot";

    private static string Slug(GitHubScope scope) =>
        string.IsNullOrWhiteSpace(scope.Slug)
            ? throw new InvalidOperationException(scope.IsEnterprise ? "GitHub:Enterprise is not configured." : "GitHub:Org is not configured.")
            : scope.Slug;

    private static string Esc(string s) => Uri.EscapeDataString(s);
}
