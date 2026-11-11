using CopilotUsage.Data.Queries;

namespace CopilotUsage.Web.Ui;

/// <summary>
/// How an <see cref="ActivityItem"/> is counted on the drill-down. GitHub reports different counters for different
/// dimensions (prompts for chat features, suggestions for completions, requests and tokens for clients), so each row
/// picks the most meaningful one and names its unit. These are activity counts, never costs.
/// </summary>
public static class ActivityView
{
    public static long Count(ActivityItem a) =>
        a.Interactions > 0 ? a.Interactions
        : a.Requests > 0 ? a.Requests
        : a.Generations > 0 ? a.Generations
        : a.LocAdded;

    public static string Unit(ActivityItem a, long count) => (a.Interactions > 0 ? "prompt" : a.Requests > 0 ? "request" : a.Generations > 0 ? "suggestion" : "line") + (count == 1 ? "" : "s");

    /// <summary>"1,240 prompts", "1 request".</summary>
    public static string Describe(ActivityItem a)
    {
        var n = Count(a);
        return $"{Fmt.Number(n)} {Unit(a, n)}";
    }

    /// <summary>Secondary text: the version where there is one, and tokens where there are some ("v1.109.2 · 4.2M tokens").</summary>
    public static string? Detail(ActivityItem a)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(a.Version)) parts.Add("v" + a.Version!.TrimStart('v', 'V'));
        if (a.Tokens > 0) parts.Add(Fmt.Compact(a.Tokens) + " tokens");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>Items with something to show, biggest first; anything past <paramref name="max"/> is folded into one "Other" row.</summary>
    public static IReadOnlyList<ActivityRow> Rows(IEnumerable<ActivityItem> items, Func<string?, string> label, int max = 6)
    {
        var ranked = items.Select(i => (Item: i, Count: Count(i))).Where(x => x.Count > 0)
            .OrderByDescending(x => x.Count).ThenBy(x => x.Item.Key, StringComparer.Ordinal).ToList();
        var rows = ranked.Take(max).Select(x => new ActivityRow(label(x.Item.Key), Describe(x.Item), Detail(x.Item), x.Count)).ToList();
        var rest = ranked.Skip(max).ToList();
        if (rest.Count > 0)
            rows.Add(new ActivityRow($"Other ({rest.Count})", Fmt.Number(rest.Sum(x => x.Count)) + " in total", null, rest.Sum(x => x.Count)));
        return rows;
    }
}

public sealed record ActivityRow(string Label, string Text, string? Detail, long Count);
