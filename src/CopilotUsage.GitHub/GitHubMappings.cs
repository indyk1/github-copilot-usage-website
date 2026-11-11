using System.Globalization;
using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;
using CopilotUsage.GitHub.Dtos;

namespace CopilotUsage.GitHub;

/// <summary>
/// EVERY DTO-to-domain mapping lives here. If GitHub's real field names or semantics differ from the
/// assumptions in docs/API-ASSUMPTIONS.md, this file (and Dtos/Dtos.cs) is what to change.
/// Mapping never throws: bad values become null, zero or "unknown".
/// </summary>
public static class GitHubMappings
{
    public const string UnknownModel = "unknown";

    public static SeatSummary ToSeatSummary(SeatBillingDto? dto)
    {
        var b = dto?.SeatBreakdown;
        return new SeatSummary(
            dto?.PlanType,
            b?.Total ?? 0,
            b?.AddedThisCycle ?? 0,
            b?.PendingInvitation ?? 0,
            b?.PendingCancellation ?? 0,
            b?.ActiveThisCycle ?? 0,
            b?.InactiveThisCycle ?? 0);
    }

    /// <summary>Returns null for a seat without an assignee login (cannot be keyed).</summary>
    public static SeatInfo? ToSeat(SeatDto? dto)
    {
        var login = dto?.Assignee?.Login;
        if (dto is null || string.IsNullOrWhiteSpace(login)) return null;
        return new SeatInfo(
            login.Trim(),
            dto.Assignee?.AvatarUrl,
            ParseTimestamp(dto.CreatedAt),
            ParseTimestamp(dto.UpdatedAt),
            dto.PlanType,
            dto.AssigningTeam?.Name ?? dto.AssigningTeam?.Slug,
            ParseTimestamp(dto.LastActivityAt),
            dto.LastActivityEditor);
    }

    /// <summary>
    /// Assumed semantics: grossQuantity = AI credits consumed (before the included pool is applied),
    /// grossAmount = dollars at list price ($0.01 per credit). Overage is computed by us from the pool.
    /// </summary>
    public static IReadOnlyList<CreditUsageRecord> ToCreditRecords(CreditUsageResponseDto? dto, CreditUsageQuery query)
    {
        var result = new List<CreditUsageRecord>();
        if (dto?.UsageItems is null) return result;

        var queryDay = DayFromPeriod(dto.TimePeriod) ?? DayFromQuery(query);
        foreach (var item in dto.UsageItems)
        {
            var credits = item.GrossQuantity
                ?? (item.GrossAmount is { } a ? a / PoolCalculator.DollarsPerCredit : (decimal?)null)
                ?? 0m;
            var amount = item.GrossAmount ?? credits * PoolCalculator.DollarsPerCredit;
            var day = ParseDay(item.Date) ?? queryDay;
            var login = FirstNonEmpty(item.User, dto.User, query.User);
            var model = FirstNonEmpty(item.Model, item.Sku) ?? UnknownModel;
            result.Add(new CreditUsageRecord(day, login, model, credits, amount));
        }
        return result;
    }

    public static UserDailyMetrics? ToUserMetrics(UserMetricsLineDto? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.UserLogin) || ParseDay(dto.Day) is not { } day) return null;

        var breakdowns = new List<MetricBreakdown>();
        breakdowns.AddRange(Aggregate(MetricDimension.Feature, dto.TotalsByFeature, t => t.Feature));
        breakdowns.AddRange(Aggregate(MetricDimension.Ide, dto.TotalsByIde, t => t.Ide));
        breakdowns.AddRange(Aggregate(MetricDimension.Client, dto.TotalsByClient, t => t.Client));
        breakdowns.AddRange(Aggregate(MetricDimension.Language, dto.TotalsByLanguageFeature ?? dto.TotalsByLanguageModel, t => t.Language));
        breakdowns.AddRange(Aggregate(MetricDimension.Model, dto.TotalsByModelFeature ?? dto.TotalsByLanguageModel, t => t.Model));

        return new UserDailyMetrics(
            dto.UserLogin.Trim(),
            day,
            dto.AiCreditsUsed ?? 0m,
            dto.UserInitiatedInteractionCount ?? 0,
            dto.CodeGenerationActivityCount ?? 0,
            dto.CodeAcceptanceActivityCount ?? 0,
            dto.LocSuggestedToAddSum ?? 0,
            dto.LocSuggestedToDeleteSum ?? 0,
            dto.LocAddedSum ?? 0,
            dto.LocDeletedSum ?? 0,
            dto.UsedChat ?? false,
            dto.UsedAgent ?? false,
            breakdowns);
    }

    /// <summary>Sums rows that share a key (e.g. a language that appears once per feature).</summary>
    private static IEnumerable<MetricBreakdown> Aggregate(
        MetricDimension dimension, List<MetricsTotalsDto>? rows, Func<MetricsTotalsDto, string?> key)
    {
        if (rows is null) yield break;
        foreach (var g in rows.Where(r => !string.IsNullOrWhiteSpace(key(r))).GroupBy(r => key(r)!.Trim(), StringComparer.Ordinal))
        {
            yield return new MetricBreakdown(
                dimension,
                g.Key,
                g.Sum(r => r.UserInitiatedInteractionCount ?? 0),
                g.Sum(r => r.CodeGenerationActivityCount ?? 0),
                g.Sum(r => r.CodeAcceptanceActivityCount ?? 0),
                g.Sum(r => r.LocAddedSum ?? 0),
                g.Sum(r => r.LocDeletedSum ?? 0),
                g.Sum(r => r.Requests ?? 0),
                g.Sum(r => r.Tokens ?? ((r.InputTokens ?? 0) + (r.OutputTokens ?? 0))),
                g.Select(r => r.LastKnownIdeVersion?.IdeVersion ?? r.LastKnownPluginVersion?.PluginVersion)
                    .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)));
        }
    }

    public static DateTimeOffset? ParseTimestamp(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v)
            ? v
            : null;

    /// <summary>Accepts "yyyy-MM-dd" and full ISO timestamps (date part in UTC is used).</summary>
    public static DateOnly? ParseDay(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (DateOnly.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return ParseTimestamp(s) is { } ts ? DateOnly.FromDateTime(ts.UtcDateTime) : null;
    }

    private static DateOnly? DayFromPeriod(TimePeriodDto? p) =>
        p is { Year: { } y, Month: { } m, Day: { } d } && IsValid(y, m, d) ? new DateOnly(y, m, d) : null;

    private static DateOnly? DayFromQuery(CreditUsageQuery q) =>
        q is { Year: { } y, Month: { } m, Day: { } d } && IsValid(y, m, d) ? new DateOnly(y, m, d) : null;

    private static bool IsValid(int y, int m, int d) =>
        y is >= 1 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
