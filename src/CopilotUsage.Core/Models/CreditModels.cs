namespace CopilotUsage.Core.Models;

/// <summary>
/// One billed AI-credit usage line. <see cref="Day"/> is null when the query was not day-filtered;
/// <see cref="Login"/> is null for org-wide lines.
/// </summary>
public sealed record CreditUsageRecord(
    DateOnly? Day,
    string? Login,
    string Model,
    decimal Credits,
    decimal Amount);

/// <summary>Filters for the billing AI-credit usage endpoint. All are optional.</summary>
public sealed record CreditUsageQuery(int? Year = null, int? Month = null, int? Day = null, string? User = null, string? Model = null);
