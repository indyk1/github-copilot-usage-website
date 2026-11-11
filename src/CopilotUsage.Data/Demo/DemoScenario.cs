namespace CopilotUsage.Data.Demo;

/// <summary>Which demo dataset to seed, so every state of the UI can be reviewed without real data.</summary>
public enum DemoScenario
{
    /// <summary>About half the pool used, a few users over their fair share.</summary>
    Normal,

    /// <summary>Pool exhausted with overage billed.</summary>
    Over,

    /// <summary>Seats exist but nothing has been used yet this month.</summary>
    Empty,
}

public static class DemoScenarios
{
    public const string Allowed = "normal, over, empty";

    /// <summary>Case-insensitive parse; null or blank means <see cref="DemoScenario.Normal"/>. Returns false for anything else.</summary>
    public static bool TryParse(string? value, out DemoScenario scenario)
    {
        if (string.IsNullOrWhiteSpace(value)) { scenario = DemoScenario.Normal; return true; }
        return Enum.TryParse(value.Trim(), ignoreCase: true, out scenario) && Enum.IsDefined(scenario);
    }
}
