using CopilotUsage.Core.Models;

namespace CopilotUsage.GitHub;

/// <summary>
/// EVERY GitHub REST path used by the app lives here. These are unverified assumptions (PLAN.md 3.5,
/// docs/API-ASSUMPTIONS.md): correcting one is a one-line change in this file.
/// </summary>
public static class GitHubEndpoints
{
    // Documented (Copilot user management API).
    public static string SeatSummary(string org) => $"/orgs/{Esc(org)}/copilot/billing";

    public static string Seats(string org, int page, int perPage) =>
        $"/orgs/{Esc(org)}/copilot/billing/seats?per_page={perPage}&page={page}";

    // Plan 3.2. Note the "organizations" (not "orgs") prefix used by the billing API. Schema unverified.
    public static string CreditUsage(string org, CreditUsageQuery query)
    {
        var parts = new List<string>();
        if (query.Year is { } y) parts.Add($"year={y}");
        if (query.Month is { } m) parts.Add($"month={m}");
        if (query.Day is { } d) parts.Add($"day={d}");
        if (!string.IsNullOrWhiteSpace(query.User)) parts.Add($"user={Esc(query.User)}");
        if (!string.IsNullOrWhiteSpace(query.Model)) parts.Add($"model={Esc(query.Model)}");
        var qs = parts.Count == 0 ? "" : "?" + string.Join('&', parts);
        return $"/organizations/{Esc(org)}/settings/billing/ai_credit/usage{qs}";
    }

    // Plan 3.3. Paths unverified (plan 3.5 item 1). Each returns signed URLs to NDJSON files.
    public static string UserMetricsLatest28Day(string org) =>
        $"/orgs/{Esc(org)}/copilot/metrics/reports/users-28-day/latest";

    public static string UserMetricsForDay(string org, DateOnly day) =>
        $"/orgs/{Esc(org)}/copilot/metrics/reports/users-1-day?day={day:yyyy-MM-dd}";

    private static string Esc(string s) => Uri.EscapeDataString(s);
}
