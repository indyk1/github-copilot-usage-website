namespace CopilotUsage.Web.Ui;

/// <summary>Avatar fallback: initials and a stable colour derived from the login.</summary>
public static class Initials
{
    /// <summary>"maya-chen" gives "MC"; "octocat" gives "OC"; empty gives "?".</summary>
    public static string From(string? login)
    {
        if (string.IsNullOrWhiteSpace(login)) return "?";
        var parts = login.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "?";
        var text = parts.Length >= 2
            ? string.Concat(parts[0][0], parts[1][0])
            : parts[0].Length >= 2 ? parts[0][..2] : parts[0];
        return text.ToUpperInvariant();
    }

    /// <summary>A hue in 0..359 that is the same for the same login on every run (FNV-1a, not string.GetHashCode).</summary>
    public static int Hue(string? login)
    {
        uint h = 2166136261;
        foreach (var ch in (login ?? "").ToLowerInvariant())
        {
            h ^= ch;
            h *= 16777619;
        }
        return (int)(h % 360);
    }
}
