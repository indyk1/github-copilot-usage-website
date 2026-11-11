namespace CopilotUsage.Core;

/// <summary>Billing-model settings (config section <c>Copilot</c>).</summary>
public sealed class CopilotOptions
{
    public const string SectionName = "Copilot";

    /// <summary>AI credits included per seat per month (Copilot Business: 1,900).</summary>
    public decimal CreditsPerSeat { get; set; } = 1900m;
}
