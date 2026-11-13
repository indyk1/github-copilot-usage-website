namespace CopilotUsage.Core.Models;

/// <summary>
/// Seat counts from the Copilot billing summary (organization scope) or derived from the seat list (enterprise scope).
/// A null count means the source does not provide it; it is never invented.
/// </summary>
public sealed record SeatSummary(
    string? PlanType,
    int TotalSeats,
    int AddedThisCycle,
    int? PendingInvitation,
    int? PendingCancellation,
    int ActiveThisCycle,
    int InactiveThisCycle);

/// <summary>One assigned Copilot seat. <c>Organization</c> is the organization(s) that assigned it, comma separated, when the API says.</summary>
public sealed record SeatInfo(
    string Login,
    string? AvatarUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? PlanType,
    string? AssigningTeam,
    DateTimeOffset? LastActivityAt,
    string? LastActivityEditor,
    string? Organization = null);
