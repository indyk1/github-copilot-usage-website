namespace CopilotUsage.Core.Models;

/// <summary>Seat counts from the Copilot billing summary.</summary>
public sealed record SeatSummary(
    string? PlanType,
    int TotalSeats,
    int AddedThisCycle,
    int PendingInvitation,
    int PendingCancellation,
    int ActiveThisCycle,
    int InactiveThisCycle);

/// <summary>One assigned Copilot seat.</summary>
public sealed record SeatInfo(
    string Login,
    string? AvatarUrl,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? UpdatedAt,
    string? PlanType,
    string? AssigningTeam,
    DateTimeOffset? LastActivityAt,
    string? LastActivityEditor);
