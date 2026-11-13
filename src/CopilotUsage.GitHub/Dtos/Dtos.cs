using System.Text.Json.Serialization;

namespace CopilotUsage.GitHub.Dtos;

// Wire shapes. Everything is nullable: a missing or mistyped field becomes null (see Json/Lenient*.cs),
// unknown fields are ignored. Field names are the assumptions listed in docs/API-ASSUMPTIONS.md.

public sealed class SeatBillingDto
{
    [JsonPropertyName("seat_breakdown")] public SeatBreakdownDto? SeatBreakdown { get; set; }
    [JsonPropertyName("plan_type")] public string? PlanType { get; set; }
}

public sealed class SeatBreakdownDto
{
    [JsonPropertyName("total")] public int? Total { get; set; }
    [JsonPropertyName("added_this_cycle")] public int? AddedThisCycle { get; set; }
    [JsonPropertyName("pending_invitation")] public int? PendingInvitation { get; set; }
    [JsonPropertyName("pending_cancellation")] public int? PendingCancellation { get; set; }
    [JsonPropertyName("active_this_cycle")] public int? ActiveThisCycle { get; set; }
    [JsonPropertyName("inactive_this_cycle")] public int? InactiveThisCycle { get; set; }
}

public sealed class SeatsPageDto
{
    [JsonPropertyName("total_seats")] public int? TotalSeats { get; set; }
    [JsonPropertyName("seats")] public List<SeatDto>? Seats { get; set; }
}

public sealed class SeatDto
{
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("last_activity_at")] public string? LastActivityAt { get; set; }
    [JsonPropertyName("last_activity_editor")] public string? LastActivityEditor { get; set; }
    [JsonPropertyName("plan_type")] public string? PlanType { get; set; }
    [JsonPropertyName("assignee")] public AssigneeDto? Assignee { get; set; }
    [JsonPropertyName("assigning_team")] public AssigningTeamDto? AssigningTeam { get; set; }
    /// <summary>The organization that assigned the seat, when the API carries it (enterprise scope; unverified).</summary>
    [JsonPropertyName("organization")] public OrganizationDto? Organization { get; set; }
}

public sealed class OrganizationDto
{
    [JsonPropertyName("login")] public string? Login { get; set; }
}

public sealed class AssigneeDto
{
    [JsonPropertyName("login")] public string? Login { get; set; }
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("avatar_url")] public string? AvatarUrl { get; set; }
}

public sealed class AssigningTeamDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
}

/// <summary>Modelled on the older <c>premium_request/usage</c> report.</summary>
public sealed class CreditUsageResponseDto
{
    [JsonPropertyName("timePeriod")] public TimePeriodDto? TimePeriod { get; set; }
    [JsonPropertyName("organization")] public string? Organization { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
    [JsonPropertyName("usageItems")] public List<CreditUsageItemDto>? UsageItems { get; set; }
}

public sealed class TimePeriodDto
{
    [JsonPropertyName("year")] public int? Year { get; set; }
    [JsonPropertyName("month")] public int? Month { get; set; }
    [JsonPropertyName("day")] public int? Day { get; set; }
}

public sealed class CreditUsageItemDto
{
    [JsonPropertyName("product")] public string? Product { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("unitType")] public string? UnitType { get; set; }
    [JsonPropertyName("pricePerUnit")] public decimal? PricePerUnit { get; set; }
    [JsonPropertyName("grossQuantity")] public decimal? GrossQuantity { get; set; }
    [JsonPropertyName("grossAmount")] public decimal? GrossAmount { get; set; }
    [JsonPropertyName("discountQuantity")] public decimal? DiscountQuantity { get; set; }
    [JsonPropertyName("discountAmount")] public decimal? DiscountAmount { get; set; }
    [JsonPropertyName("netQuantity")] public decimal? NetQuantity { get; set; }
    [JsonPropertyName("netAmount")] public decimal? NetAmount { get; set; }
    /// <summary>Optional per-line date (yyyy-MM-dd); the report is normally filtered by day instead.</summary>
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("user")] public string? User { get; set; }
}

/// <summary>Response of a usage-metrics report call: signed URLs to NDJSON files.</summary>
public sealed class MetricsReportLinksDto
{
    [JsonPropertyName("download_links")] public List<string>? DownloadLinks { get; set; }
    [JsonPropertyName("report_start_day")] public string? ReportStartDay { get; set; }
    [JsonPropertyName("report_end_day")] public string? ReportEndDay { get; set; }
    [JsonPropertyName("report_day")] public string? ReportDay { get; set; }
}

/// <summary>One NDJSON line: one user on one day.</summary>
public sealed class UserMetricsLineDto
{
    [JsonPropertyName("user_login")] public string? UserLogin { get; set; }
    [JsonPropertyName("user_id")] public long? UserId { get; set; }
    [JsonPropertyName("day")] public string? Day { get; set; }
    [JsonPropertyName("ai_credits_used")] public decimal? AiCreditsUsed { get; set; }
    [JsonPropertyName("user_initiated_interaction_count")] public long? UserInitiatedInteractionCount { get; set; }
    [JsonPropertyName("code_generation_activity_count")] public long? CodeGenerationActivityCount { get; set; }
    [JsonPropertyName("code_acceptance_activity_count")] public long? CodeAcceptanceActivityCount { get; set; }
    [JsonPropertyName("loc_suggested_to_add_sum")] public long? LocSuggestedToAddSum { get; set; }
    [JsonPropertyName("loc_suggested_to_delete_sum")] public long? LocSuggestedToDeleteSum { get; set; }
    [JsonPropertyName("loc_added_sum")] public long? LocAddedSum { get; set; }
    [JsonPropertyName("loc_deleted_sum")] public long? LocDeletedSum { get; set; }
    [JsonPropertyName("used_chat")] public bool? UsedChat { get; set; }
    [JsonPropertyName("used_agent")] public bool? UsedAgent { get; set; }
    [JsonPropertyName("totals_by_ide")] public List<MetricsTotalsDto>? TotalsByIde { get; set; }
    [JsonPropertyName("totals_by_feature")] public List<MetricsTotalsDto>? TotalsByFeature { get; set; }
    [JsonPropertyName("totals_by_language_feature")] public List<MetricsTotalsDto>? TotalsByLanguageFeature { get; set; }
    [JsonPropertyName("totals_by_language_model")] public List<MetricsTotalsDto>? TotalsByLanguageModel { get; set; }
    [JsonPropertyName("totals_by_model_feature")] public List<MetricsTotalsDto>? TotalsByModelFeature { get; set; }
    [JsonPropertyName("totals_by_client")] public List<MetricsTotalsDto>? TotalsByClient { get; set; }
}

/// <summary>Shared row shape for every totals_by_* array; only the relevant key field is set.</summary>
public sealed class MetricsTotalsDto
{
    [JsonPropertyName("ide")] public string? Ide { get; set; }
    [JsonPropertyName("feature")] public string? Feature { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("client")] public string? Client { get; set; }
    [JsonPropertyName("user_initiated_interaction_count")] public long? UserInitiatedInteractionCount { get; set; }
    [JsonPropertyName("code_generation_activity_count")] public long? CodeGenerationActivityCount { get; set; }
    [JsonPropertyName("code_acceptance_activity_count")] public long? CodeAcceptanceActivityCount { get; set; }
    [JsonPropertyName("loc_added_sum")] public long? LocAddedSum { get; set; }
    [JsonPropertyName("loc_deleted_sum")] public long? LocDeletedSum { get; set; }
    [JsonPropertyName("requests")] public long? Requests { get; set; }
    [JsonPropertyName("tokens")] public long? Tokens { get; set; }
    [JsonPropertyName("input_tokens")] public long? InputTokens { get; set; }
    [JsonPropertyName("output_tokens")] public long? OutputTokens { get; set; }
    [JsonPropertyName("last_known_ide_version")] public VersionDto? LastKnownIdeVersion { get; set; }
    [JsonPropertyName("last_known_plugin_version")] public VersionDto? LastKnownPluginVersion { get; set; }
}

public sealed class VersionDto
{
    [JsonPropertyName("ide_version")] public string? IdeVersion { get; set; }
    [JsonPropertyName("plugin")] public string? Plugin { get; set; }
    [JsonPropertyName("plugin_version")] public string? PluginVersion { get; set; }
    [JsonPropertyName("sampled_at")] public string? SampledAt { get; set; }
}
