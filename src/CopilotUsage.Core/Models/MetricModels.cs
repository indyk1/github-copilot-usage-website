namespace CopilotUsage.Core.Models;

/// <summary>The dimension a metrics breakdown row belongs to.</summary>
public enum MetricDimension
{
    Feature,
    Ide,
    Client,
    Language,
    Model,
}

/// <summary>Activity counts for one key (e.g. feature "chat_panel_agent_mode") on one day.</summary>
public sealed record MetricBreakdown(
    MetricDimension Dimension,
    string Key,
    long Interactions,
    long Generations,
    long Acceptances,
    long LocAdded,
    long LocDeleted,
    long Requests,
    long Tokens,
    string? Version);

/// <summary>One user's metrics for one UTC day.</summary>
public sealed record UserDailyMetrics(
    string Login,
    DateOnly Day,
    decimal AiCredits,
    long Prompts,
    long SuggestionsGenerated,
    long SuggestionsAccepted,
    long LocSuggestedToAdd,
    long LocSuggestedToDelete,
    long LocAdded,
    long LocDeleted,
    bool UsedChat,
    bool UsedAgent,
    IReadOnlyList<MetricBreakdown> Breakdowns);
