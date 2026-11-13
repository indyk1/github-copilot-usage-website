namespace CopilotUsage.Core.Models;

/// <summary>Organization text on a seat is a comma separated list (slugs cannot contain commas).</summary>
public static class SeatOrganizations
{
    public static IEnumerable<string> Split(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when the given seat texts name more than one distinct organization between them.</summary>
    public static bool HasMany(IEnumerable<string?> texts) =>
        texts.SelectMany(Split).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
}
