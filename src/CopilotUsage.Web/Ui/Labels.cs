using System.Globalization;

namespace CopilotUsage.Web.Ui;

/// <summary>
/// Readable names for the raw identifiers GitHub's metrics use ("chat_panel_agent_mode"). Known keys get a hand-written
/// label; unknown keys fall back to their words in sentence case, so a new identifier still reads sensibly.
/// </summary>
public static class Labels
{
    private static readonly Dictionary<string, string> Features = new(StringComparer.OrdinalIgnoreCase)
    {
        ["code_completion"] = "Code completions",
        ["code_completions"] = "Code completions",
        ["next_edit_suggestion"] = "Next edit suggestions",
        ["next_edit_suggestions"] = "Next edit suggestions",
        ["chat_panel_ask_mode"] = "Chat, ask mode",
        ["chat_panel_agent_mode"] = "Chat, agent mode",
        ["chat_panel_edit_mode"] = "Chat, edit mode",
        ["chat_panel_plan_mode"] = "Chat, plan mode",
        ["chat_panel_custom_mode"] = "Chat, custom mode",
        ["chat_inline"] = "Inline chat",
        ["agent_edit"] = "Agent edits",
        ["copilot_cli"] = "Copilot CLI",
        ["code_review"] = "Code review",
        ["cloud_agent"] = "Cloud agent",
        ["coding_agent"] = "Cloud agent",
        ["pull_request_summary"] = "Pull request summaries",
    };

    private static readonly Dictionary<string, string> Ides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vscode"] = "VS Code",
        ["vscode-insiders"] = "VS Code Insiders",
        ["visualstudio"] = "Visual Studio",
        ["visual-studio"] = "Visual Studio",
        ["intellij"] = "IntelliJ IDEA",
        ["jetbrains"] = "JetBrains",
        ["neovim"] = "Neovim",
        ["vim"] = "Vim",
        ["xcode"] = "Xcode",
        ["eclipse"] = "Eclipse",
    };

    private static readonly Dictionary<string, string> Clients = new(StringComparer.OrdinalIgnoreCase)
    {
        ["vscode"] = "VS Code",
        ["jetbrains"] = "JetBrains IDEs",
        ["visual-studio"] = "Visual Studio",
        ["visualstudio"] = "Visual Studio",
        ["neovim"] = "Neovim",
        ["copilot-cli"] = "Copilot CLI",
        ["copilot_cli"] = "Copilot CLI",
        ["claude-code"] = "Claude Code",
        ["codex"] = "Codex",
        ["opencode"] = "OpenCode",
    };

    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["csharp"] = "C#",
        ["cpp"] = "C++",
        ["fsharp"] = "F#",
        ["javascript"] = "JavaScript",
        ["typescript"] = "TypeScript",
        ["javascriptreact"] = "JavaScript (React)",
        ["typescriptreact"] = "TypeScript (React)",
        ["python"] = "Python",
        ["java"] = "Java",
        ["go"] = "Go",
        ["rust"] = "Rust",
        ["razor"] = "Razor",
        ["sql"] = "SQL",
        ["yaml"] = "YAML",
        ["json"] = "JSON",
        ["html"] = "HTML",
        ["css"] = "CSS",
        ["markdown"] = "Markdown",
        ["shellscript"] = "Shell",
        ["powershell"] = "PowerShell",
    };

    public static string Feature(string? key) => Lookup(Features, key);
    public static string Ide(string? key) => Lookup(Ides, key);
    public static string Client(string? key) => Lookup(Clients, key);
    public static string Language(string? key) => Lookup(Languages, key);

    private static string Lookup(Dictionary<string, string> known, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "Unknown";
        var k = key.Trim();
        return known.TryGetValue(k, out var label) ? label : Humanize(k);
    }

    /// <summary>"some_new-key" becomes "Some new key". Keeps tokens that already contain capitals or digits-with-dots as they are.</summary>
    public static string Humanize(string key)
    {
        var words = key.Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return key;
        var text = string.Join(' ', words);
        return char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
    }
}
