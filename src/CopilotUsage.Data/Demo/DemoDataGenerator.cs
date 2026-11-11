using CopilotUsage.Core.Calculations;
using CopilotUsage.Core.Models;

namespace CopilotUsage.Data.Demo;

public sealed record DemoUserMonthLine(BillingMonth Month, string Login, string Model, decimal Credits);

public sealed record DemoDataSet(
    SeatSummary Summary,
    IReadOnlyList<SeatInfo> Seats,
    IReadOnlyList<BillingMonth> Months,
    /// <summary>Org-wide billed lines (Login null), one per day and model.</summary>
    IReadOnlyList<CreditUsageRecord> OrgDaily,
    /// <summary>Per-user billed credits for a whole month, one line per month, user and model.</summary>
    IReadOnlyList<DemoUserMonthLine> UserMonthly,
    IReadOnlyList<UserDailyMetrics> Metrics);

/// <summary>
/// Builds a deterministic, plausible dataset for the current and previous billing month: 25 users
/// (4 heavy users over their fair share, 10 medium, 9 light, 2 inactive), 6 models, and per-day activity
/// by feature, IDE, client, language and model. Same clock date in, same data out.
/// </summary>
public static class DemoDataGenerator
{
    private enum Kind { Heavy, Medium, Light, Inactive }

    private sealed record Persona(string Login, Kind Kind, double AgentAffinity, double CliAffinity, double ThirdPartyAffinity, string Ide, IReadOnlyList<string> Languages, string? Team);

    private static readonly string[] Models =
        ["GPT-5.2", "Claude Sonnet 4.5", "Claude Opus 4.5", "Gemini 3 Pro", "GPT-5 mini", "Claude Haiku 4.5"];

    private static readonly double[] ModelPopularity = [1.0, 1.2, 0.5, 0.4, 0.6, 0.5];

    private static readonly string[] AllLanguages =
        ["csharp", "typescript", "python", "java", "go", "razor", "sql", "yaml", "markdown"];

    private static readonly Dictionary<string, string> IdeVersions = new()
    {
        ["vscode"] = "1.109.2",
        ["intellij"] = "2026.2.1",
        ["visualstudio"] = "18.3.0",
        ["neovim"] = "0.12.1",
    };

    private static readonly string[] Teams = ["platform", "payments", "web-frontend", "data-eng"];

    private static readonly (string Login, Kind Kind)[] Roster =
    [
        ("maya-chen", Kind.Heavy), ("dmitri-volkov", Kind.Heavy), ("priya-nair", Kind.Heavy), ("tomas-lindqvist", Kind.Heavy),
        ("sofia-alvarez", Kind.Medium), ("jonas-weber", Kind.Medium), ("amara-okafor", Kind.Medium), ("liam-obrien", Kind.Medium),
        ("hana-sato", Kind.Medium), ("caleb-morgan", Kind.Medium), ("elena-rossi", Kind.Medium), ("omar-haddad", Kind.Medium),
        ("grace-kim", Kind.Medium), ("lucas-silva", Kind.Medium),
        ("noah-fischer", Kind.Light), ("zoe-bennett", Kind.Light), ("ivan-petrov", Kind.Light), ("aisha-khan", Kind.Light),
        ("marco-bianchi", Kind.Light), ("chloe-dubois", Kind.Light), ("ravi-menon", Kind.Light), ("emma-larsen", Kind.Light),
        ("kofi-mensah", Kind.Light),
        ("olivia-grant", Kind.Inactive), ("pavel-novak", Kind.Inactive),
    ];

    public static DemoDataSet Generate(DateTimeOffset now, decimal creditsPerSeat)
    {
        var nowUtc = now.ToUniversalTime();
        var today = DateOnly.FromDateTime(nowUtc.UtcDateTime);
        var current = BillingMonth.From(today);
        var months = new[] { current.Previous, current };

        var personas = Roster.Select((r, i) => BuildPersona(r, i)).ToList();
        var orgDaily = new Dictionary<(DateOnly Day, string Model), decimal>();
        var userMonthly = new Dictionary<(DateOnly Month, string Login, string Model), decimal>();
        var metrics = new List<UserDailyMetrics>();
        var lastActive = new Dictionary<string, DateOnly>(StringComparer.Ordinal);

        foreach (var month in months)
        {
            var isCurrent = month == current;
            var lastDay = isCurrent ? today : month.LastDay;
            foreach (var p in personas.Where(p => p.Kind != Kind.Inactive))
            {
                var target = TargetCredits(p, month, isCurrent, today, creditsPerSeat);
                var perDay = DailyCredits(p, month, lastDay, today, target);
                var modelWeights = UserModelWeights(p);

                foreach (var (day, dayCredits) in perDay)
                {
                    if (dayCredits <= 0m) continue;
                    var rng = new DemoRandom(p.Login, "daymix", day.DayNumber);
                    var mix = modelWeights.Select(w => w * rng.Between(0.7, 1.3)).ToArray();
                    var split = SplitDecimal(dayCredits, mix);
                    for (var i = 0; i < Models.Length; i++)
                    {
                        if (split[i] <= 0m) continue;
                        orgDaily[(day, Models[i])] = orgDaily.GetValueOrDefault((day, Models[i])) + split[i];
                        (DateOnly Month, string Login, string Model) k = (month.Start, p.Login, Models[i]);
                        userMonthly[k] = userMonthly.GetValueOrDefault(k) + split[i];
                    }
                    metrics.Add(BuildMetrics(p, day, dayCredits, split, mix));
                    lastActive[p.Login] = day;
                }
            }
        }

        var seats = personas.Select(p => BuildSeat(p, current, today, nowUtc, lastActive)).ToList();
        var activeThisCycle = personas.Count(p => p.Kind != Kind.Inactive && lastActive.TryGetValue(p.Login, out var d) && current.Contains(d));
        var summary = new SeatSummary(
            "business", seats.Count,
            seats.Count(s => s.CreatedAt is { } c && c >= current.StartUtc),
            0, 0, activeThisCycle, seats.Count - activeThisCycle);

        return new DemoDataSet(
            summary, seats, months,
            orgDaily.Select(kv => new CreditUsageRecord(kv.Key.Day, null, kv.Key.Model, kv.Value, kv.Value * PoolCalculator.DollarsPerCredit))
                .OrderBy(r => r.Day).ThenBy(r => r.Model, StringComparer.Ordinal).ToList(),
            userMonthly.Select(kv => new DemoUserMonthLine(BillingMonth.From(kv.Key.Month), kv.Key.Login, kv.Key.Model, kv.Value))
                .OrderBy(r => r.Month.Start).ThenBy(r => r.Login, StringComparer.Ordinal).ThenBy(r => r.Model, StringComparer.Ordinal).ToList(),
            metrics);
    }

    // ---- personas ----

    private static Persona BuildPersona((string Login, Kind Kind) r, int index)
    {
        var rng = new DemoRandom(r.Login, "persona");
        var langCount = rng.Between(2, 4);
        var langs = AllLanguages.OrderBy(_ => rng.NextDouble()).Take(langCount).ToList();
        var ide = rng.NextDouble() switch { < 0.60 => "vscode", < 0.80 => "intellij", < 0.95 => "visualstudio", _ => "neovim" };
        var agent = r.Kind switch { Kind.Heavy => rng.Between(0.6, 0.9), Kind.Medium => rng.Between(0.15, 0.55), _ => rng.Between(0.0, 0.25) };
        var cli = r.Kind == Kind.Heavy ? rng.Between(0.1, 0.4) : rng.Between(0.0, 0.15);
        var third = rng.NextDouble() < 0.3 ? rng.Between(0.1, 0.4) : 0.0;
        return new Persona(r.Login, r.Kind, agent, cli, third, ide, langs, Teams[index % Teams.Length]);
    }

    private static SeatInfo BuildSeat(Persona p, BillingMonth current, DateOnly today, DateTimeOffset now, Dictionary<string, DateOnly> lastActive)
    {
        var rng = new DemoRandom(p.Login, "seat");
        var created = now.AddDays(-rng.Between(60, 420));
        if (p.Login == "lucas-silva") created = current.StartUtc.AddHours(1); // the one seat added this cycle
        created = new DateTimeOffset(created.UtcDateTime.Date, TimeSpan.Zero).AddHours(rng.Between(8, 17));

        DateTimeOffset? lastSeen = p.Kind switch
        {
            Kind.Inactive when p.Login == "olivia-grant" => now.AddDays(-52),
            Kind.Inactive => null,
            _ => lastActive.TryGetValue(p.Login, out var d)
                ? new DateTimeOffset(d.ToDateTime(new TimeOnly(rng.Between(9, 17), rng.Between(0, 59))), TimeSpan.Zero)
                : now.AddDays(-rng.Between(40, 70)),
        };
        if (lastSeen > now) lastSeen = now;

        return new SeatInfo(
            p.Login,
            $"https://github.com/identicons/{p.Login}.png",
            created, created, "business", p.Team, lastSeen,
            lastSeen is null ? null : EditorString(p.Ide));
    }

    private static string EditorString(string ide) => ide switch
    {
        "vscode" => $"vscode/{IdeVersions["vscode"]}/copilot-chat/0.36.1",
        "intellij" => $"JetBrains-IC/{IdeVersions["intellij"]}",
        "visualstudio" => $"VisualStudio/{IdeVersions["visualstudio"]}",
        _ => $"Neovim/{IdeVersions["neovim"]}",
    };

    // ---- credits ----

    /// <summary>
    /// Month-to-date (current month) or full-month (previous month) credits for a user, as a share of the
    /// 1,900 fair share. The current month is damped for the first few days so day 1 does not look absurd.
    /// </summary>
    private static decimal TargetCredits(Persona p, BillingMonth month, bool isCurrent, DateOnly today, decimal fairShare)
    {
        var rng = new DemoRandom(p.Login, "target", month.Start.DayNumber);
        var (lo, hi) = (p.Kind, isCurrent) switch
        {
            (Kind.Heavy, true) => (1.10, 1.70),
            (Kind.Heavy, false) => (1.40, 2.30),
            (Kind.Medium, true) => (0.35, 0.90),
            (Kind.Medium, false) => (0.40, 1.00),
            (Kind.Light, true) => (0.04, 0.25),
            _ => (0.05, 0.30),
        };
        var fraction = rng.Between(lo, hi);
        if (isCurrent) fraction *= Math.Min(1.0, 0.4 + 0.06 * today.Day);
        return Math.Round(fairShare * (decimal)fraction, 2);
    }

    private static List<(DateOnly Day, decimal Credits)> DailyCredits(Persona p, BillingMonth month, DateOnly lastDay, DateOnly today, decimal target)
    {
        var activeProbability = p.Kind switch { Kind.Heavy => 0.95, Kind.Medium => 0.80, _ => 0.45 };
        var days = new List<DateOnly>();
        for (var d = month.Start; d <= lastDay; d = d.AddDays(1)) days.Add(d);

        var weights = new double[days.Count];
        for (var i = 0; i < days.Count; i++)
        {
            var rng = new DemoRandom(p.Login, "day", days[i].DayNumber);
            var weekend = days[i].DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var probability = weekend ? activeProbability * 0.15 : activeProbability;
            weights[i] = rng.NextDouble() < probability ? rng.Between(0.4, 1.8) : 0.0;
            if (days[i] == today) weights[i] *= 0.5; // today is partial
        }
        if (weights.All(w => w == 0.0) && weights.Length > 0) weights[Array.FindLastIndex(weights, _ => true)] = 1.0;

        var split = SplitDecimal(target, weights);
        return days.Select((d, i) => (d, split[i])).ToList();
    }

    private static double[] UserModelWeights(Persona p)
    {
        var rng = new DemoRandom(p.Login, "models");
        var w = new double[Models.Length];
        for (var i = 0; i < w.Length; i++)
        {
            var v = ModelPopularity[i] * (0.3 + rng.NextDouble());
            if (p.Kind == Kind.Heavy && i == 2) v *= 2.5;           // Opus-heavy power users
            if (p.Kind == Kind.Light && (i == 4 || i == 5)) v *= 2.5; // light users lean on cheap models
            w[i] = v;
        }
        return w;
    }

    // ---- metrics ----

    private static UserDailyMetrics BuildMetrics(Persona p, DateOnly day, decimal billedCredits, decimal[] modelCredits, double[] modelMix)
    {
        var rng = new DemoRandom(p.Login, "metrics", day.DayNumber);
        var aiCredits = Math.Round(billedCredits * (decimal)rng.Between(0.92, 1.08), 2);
        var prompts = Math.Max(1L, (long)Math.Round((double)billedCredits / rng.Between(3.5, 6.5)));

        var scale = p.Kind switch { Kind.Heavy => 1.0, Kind.Medium => 0.7, _ => 0.4 };
        var generated = (long)Math.Round((60 + 260 * rng.NextDouble()) * scale);
        var acceptRate = 0.22 + 0.16 * (new DemoRandom(p.Login, "acc").NextDouble()) + rng.Between(-0.03, 0.03);
        var accepted = (long)Math.Round(generated * acceptRate);
        var locSuggested = (long)Math.Round(generated * rng.Between(2.8, 3.8));
        var locAdded = (long)Math.Round(accepted * rng.Between(2.6, 3.4) + prompts * p.AgentAffinity * rng.Between(8, 30));
        var locDeleted = (long)Math.Round(locAdded * rng.Between(0.25, 0.45));

        var usedAgent = rng.NextDouble() < p.AgentAffinity;
        var breakdowns = new List<MetricBreakdown>();

        // Feature mix. Completions are activity only (unlimited, no credits); the rest split the prompts.
        var featureKeys = new[] { "chat_panel_ask_mode", "chat_panel_agent_mode", "chat_panel_edit_mode", "chat_inline", "copilot_cli", "code_review" };
        var featureWeights = new[]
        {
            0.40 * (1.1 - p.AgentAffinity), usedAgent ? 0.5 + p.AgentAffinity : 0.0, 0.08, 0.12,
            p.CliAffinity, 0.04,
        };
        var featurePrompts = SplitLong(prompts, featureWeights);
        breakdowns.Add(new MetricBreakdown(MetricDimension.Feature, "code_completion", 0, generated, accepted, locAdded / 2, locDeleted / 4, 0, 0, null));
        for (var i = 0; i < featureKeys.Length; i++)
        {
            if (featurePrompts[i] == 0) continue;
            var gen = (long)Math.Round(featurePrompts[i] * 0.6);
            breakdowns.Add(new MetricBreakdown(MetricDimension.Feature, featureKeys[i], featurePrompts[i], gen, gen / 2,
                featurePrompts[i] * rng.Between(3, 14) / 2, featurePrompts[i] * rng.Between(1, 5) / 2, 0, 0, null));
        }

        // IDE: the primary IDE takes most of the day, sometimes a second one.
        var ides = new List<string> { p.Ide };
        if (rng.NextDouble() < 0.25) ides.Add(p.Ide == "vscode" ? "intellij" : "vscode");
        var ideWeights = ides.Select((_, i) => i == 0 ? 0.8 : 0.2).ToArray();
        var idePrompts = SplitLong(prompts, ideWeights);
        var ideGen = SplitLong(generated, ideWeights);
        var ideAcc = SplitLong(accepted, ideWeights);
        for (var i = 0; i < ides.Count; i++)
            breakdowns.Add(new MetricBreakdown(MetricDimension.Ide, ides[i], idePrompts[i], ideGen[i], ideAcc[i],
                locAdded * (i == 0 ? 8 : 2) / 10, locDeleted * (i == 0 ? 8 : 2) / 10, 0, 0, IdeVersions[ides[i]]));

        // Client: the editor's own client, plus CLI and third-party tools where the persona uses them.
        var ownClient = p.Ide is "intellij" ? "jetbrains" : p.Ide is "visualstudio" ? "visual-studio" : p.Ide == "neovim" ? "neovim" : "vscode";
        var clientList = new List<(string Key, double Weight)> { (ownClient, 1.0) };
        if (p.CliAffinity > 0.02) clientList.Add(("copilot-cli", p.CliAffinity));
        if (p.ThirdPartyAffinity > 0)
            clientList.Add((new DemoRandom(p.Login, "third").Pick(new[] { "claude-code", "codex", "opencode" }), p.ThirdPartyAffinity));
        var requests = (long)Math.Round(prompts * rng.Between(3.0, 8.0));
        var reqSplit = SplitLong(requests, clientList.Select(c => c.Weight).ToArray());
        for (var i = 0; i < clientList.Count; i++)
            breakdowns.Add(new MetricBreakdown(MetricDimension.Client, clientList[i].Key, 0, 0, 0, 0, 0,
                reqSplit[i], reqSplit[i] * (long)rng.Between(5000, 13000), null));

        // Languages: split across the persona's languages with a dominant one.
        var langWeights = p.Languages.Select((_, i) => 1.0 / (i + 1)).ToArray();
        var langPrompts = SplitLong(prompts, langWeights);
        var langGen = SplitLong(generated, langWeights);
        var langAcc = SplitLong(accepted, langWeights);
        var langLoc = SplitLong(locAdded, langWeights);
        var langDel = SplitLong(locDeleted, langWeights);
        for (var i = 0; i < p.Languages.Count; i++)
            breakdowns.Add(new MetricBreakdown(MetricDimension.Language, p.Languages[i], langPrompts[i], langGen[i], langAcc[i], langLoc[i], langDel[i], 0, 0, null));

        // Model activity (chat only, not completions) follows the day's credit mix.
        var modelPrompts = SplitLong(prompts, modelMix);
        for (var i = 0; i < Models.Length; i++)
            if (modelPrompts[i] > 0 && modelCredits[i] > 0m)
                breakdowns.Add(new MetricBreakdown(MetricDimension.Model, Models[i], modelPrompts[i], 0, 0, 0, 0, 0, 0, null));

        return new UserDailyMetrics(
            p.Login, day, aiCredits, prompts, generated, accepted, locSuggested, locSuggested / 5,
            locAdded, locDeleted, true, usedAgent, breakdowns);
    }

    // ---- exact splitting helpers (largest remainder) ----

    private static decimal[] SplitDecimal(decimal total, double[] weights)
    {
        var result = new decimal[weights.Length];
        var sum = weights.Sum();
        if (total <= 0m || sum <= 0.0) return result;
        var cents = (long)Math.Round(total * 100m);
        var parts = SplitLong(cents, weights);
        for (var i = 0; i < parts.Length; i++) result[i] = parts[i] / 100m;
        return result;
    }

    private static long[] SplitLong(long total, double[] weights)
    {
        var result = new long[weights.Length];
        var sum = weights.Sum();
        if (total <= 0 || sum <= 0.0) return result;

        var exact = weights.Select(w => total * w / sum).ToArray();
        for (var i = 0; i < result.Length; i++) result[i] = (long)Math.Floor(exact[i]);
        var remainder = total - result.Sum();
        foreach (var i in Enumerable.Range(0, result.Length).OrderByDescending(i => exact[i] - result[i]).ThenBy(i => i).Take((int)remainder))
            result[i]++;
        return result;
    }
}
