using CopilotUsage.Core.Models;

namespace CopilotUsage.Data.Entities;

/// <summary>An assigned Copilot seat. Rows are kept after a seat disappears (IsActive = false).</summary>
public class Seat
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string? AvatarUrl { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? PlanType { get; set; }
    public string? AssigningTeam { get; set; }
    public DateTimeOffset? LastActivityAt { get; set; }
    public string? LastActivityEditor { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSyncedAt { get; set; }
}

/// <summary>Seat counts as of the last sync of a UTC day (one row per day).</summary>
public class SeatSnapshot
{
    public int Id { get; set; }
    public DateOnly Day { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public string? PlanType { get; set; }
    public int TotalSeats { get; set; }
    public int AddedThisCycle { get; set; }
    public int PendingInvitation { get; set; }
    public int PendingCancellation { get; set; }
    public int ActiveThisCycle { get; set; }
    public int InactiveThisCycle { get; set; }
}

/// <summary>
/// Billed credits for one day and model. <see cref="UserLogin"/> is <see cref="OrgWide"/> (empty string)
/// for org-wide rows, which is what the sync writes (billing is queried per day for the whole org).
/// </summary>
public class CreditUsageDaily
{
    public const string OrgWide = "";

    public int Id { get; set; }
    public DateOnly Day { get; set; }
    public string UserLogin { get; set; } = OrgWide;
    public string Model { get; set; } = "";
    public decimal Credits { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>
/// Billed credits per user per model for a whole month (billing is queried per user for the month,
/// not per user per day). This table is an addition to the tables listed in PLAN.md section 5.
/// </summary>
public class UserModelMonthly
{
    public int Id { get; set; }
    public DateOnly MonthStart { get; set; }
    public string UserLogin { get; set; } = "";
    public string Model { get; set; } = "";
    public decimal Credits { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>Per-user daily totals from the usage metrics report. AiCredits is an analysis signal, not a billed figure.</summary>
public class UserMetricDaily
{
    public int Id { get; set; }
    public DateOnly Day { get; set; }
    public string UserLogin { get; set; } = "";
    public decimal AiCredits { get; set; }
    public long Prompts { get; set; }
    public long SuggestionsGenerated { get; set; }
    public long SuggestionsAccepted { get; set; }
    public long LocSuggestedToAdd { get; set; }
    public long LocSuggestedToDelete { get; set; }
    public long LocAdded { get; set; }
    public long LocDeleted { get; set; }
    public bool UsedChat { get; set; }
    public bool UsedAgent { get; set; }
}

public class UserMetricBreakdown
{
    public int Id { get; set; }
    public DateOnly Day { get; set; }
    public string UserLogin { get; set; } = "";
    public MetricDimension Dimension { get; set; }
    public string Key { get; set; } = "";
    public long Interactions { get; set; }
    public long Generations { get; set; }
    public long Acceptances { get; set; }
    public long LocAdded { get; set; }
    public long LocDeleted { get; set; }
    public long Requests { get; set; }
    public long Tokens { get; set; }
    public string? Version { get; set; }
}

public enum SyncTrigger { Startup, Scheduled, Manual, Demo, Api }

public enum SyncStatus { Running, Succeeded, PartialFailure, Failed }

public class SyncRun
{
    public int Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public SyncTrigger Trigger { get; set; }
    public SyncStatus Status { get; set; }
    public bool IsDemo { get; set; }

    /// <summary>JSON array of step results: [{"step","status","count","error"}].</summary>
    public string? StepsJson { get; set; }

    /// <summary>First error message, if any, for display.</summary>
    public string? Error { get; set; }
}
