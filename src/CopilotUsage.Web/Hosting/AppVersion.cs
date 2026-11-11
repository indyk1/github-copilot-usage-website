using System.Reflection;

namespace CopilotUsage.Web;

/// <summary>
/// The version and, when the build recorded it, the short commit hash. The deploy pipeline passes
/// <c>-p:SourceRevisionId=$(Build.SourceVersion)</c>, which the SDK appends to the informational version as <c>+sha</c>.
/// </summary>
public static class AppVersion
{
    public static string Version { get; }
    public static string? Commit { get; }

    /// <summary>For example "1.0.0 (3fa2c1d)", or just "1.0.0" when no commit was recorded.</summary>
    public static string Full => Commit is null ? Version : $"{Version} ({Commit})";

    static AppVersion() => (Version, Commit) = Parse(
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static (string Version, string? Commit) Parse(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational)) return ("unknown", null);
        var plus = informational.IndexOf('+');
        if (plus < 0) return (informational.Trim(), null);
        var version = informational[..plus].Trim();
        var commit = informational[(plus + 1)..].Trim();
        return (version.Length == 0 ? "unknown" : version, commit.Length == 0 ? null : commit[..Math.Min(7, commit.Length)]);
    }
}
