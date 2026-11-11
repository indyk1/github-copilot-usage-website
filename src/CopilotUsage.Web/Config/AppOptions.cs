namespace CopilotUsage.Web.Config;

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    /// <summary>Hours between scheduled syncs.</summary>
    public double IntervalHours { get; set; } = 4;

    /// <summary>Run a sync as soon as the app starts (real mode only; demo data is seeded before the host starts).</summary>
    public bool RunOnStartup { get; set; } = true;
}

public sealed class DemoOptions
{
    public const string SectionName = "Demo";

    /// <summary>When true GitHub is never called; the database is filled with deterministic demo data.</summary>
    public bool Enabled { get; set; }

    /// <summary>Which demo dataset to show: normal (default), over (pool exhausted with overage) or empty (seats, no usage yet).</summary>
    public string Scenario { get; set; } = "normal";
}
