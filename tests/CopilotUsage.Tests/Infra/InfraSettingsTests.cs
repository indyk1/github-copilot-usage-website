using System.Reflection;
using System.Text.RegularExpressions;
using CopilotUsage.Core;
using CopilotUsage.GitHub;
using CopilotUsage.SyncFunction;
using CopilotUsage.Web.Config;

namespace CopilotUsage.Tests.Infra;

/// <summary>
/// A misspelt configuration key does not fail: the app silently uses the default. These tests read the App Settings that
/// infra/web.tf and infra/function.tf write and check every key against the options classes the code binds (and against the keys the
/// startup validator demands), so the Terraform and the code cannot drift apart unnoticed.
/// </summary>
public class InfraSettingsTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CopilotUsage.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("CopilotUsage.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    /// <summary>Keys of the form "Section__Name" (or a bare name) assigned inside the settings map of a .tf file.</summary>
    private static List<string> KeysIn(string tf, string startMarker)
    {
        var start = tf.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{startMarker}' not found in the Terraform file.");
        var tail = tf[start..];
        // up to the first closing of the map: the next line that is only "}" or ")" at the map's own indentation is not needed,
        // because the quoted-key pattern below only matches assignments, and the files keep other quoted assignments out of these maps.
        return Regex.Matches(tail, "^\\s*\"([A-Za-z][A-Za-z0-9_]*)\"\\s*=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToList();
    }

    private static readonly Dictionary<string, Type> Sections = new()
    {
        ["AzureAd"] = typeof(AzureAdOptions),
        ["Sync"] = typeof(SyncOptions),
        ["GitHub"] = typeof(GitHubOptions),
        ["Copilot"] = typeof(CopilotOptions),
        ["Demo"] = typeof(DemoOptions),
        ["SyncApi"] = typeof(SyncApiOptions),
    };

    private static bool BindsToProperty(string key)
    {
        var parts = key.Split("__");
        if (parts.Length != 2 || !Sections.TryGetValue(parts[0], out var type)) return false;
        // Configuration binding is case-insensitive.
        return type.GetProperty(parts[1], BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.CanWrite == true;
    }

    // Keys the platform or code reads without an options class.
    private static readonly string[] OtherKnownWebKeys = ["ConnectionStrings__Default", "DataProtection__KeysPath", "WEBSITES_ENABLE_APP_SERVICE_STORAGE"];

    [Fact]
    public void Every_web_app_setting_in_terraform_binds_to_a_real_option()
    {
        var keys = KeysIn(Read("infra", "web.tf"), "web_app_settings = merge(");
        Assert.NotEmpty(keys);
        foreach (var key in keys.Where(k => !OtherKnownWebKeys.Contains(k)))
            Assert.True(BindsToProperty(key), $"infra/web.tf sets '{key}', which no options class binds (misspelt, or the property was renamed).");

        // The two keys without an options class must still be the ones the code reads.
        var program = Read("src", "CopilotUsage.Web", "Program.cs");
        Assert.Contains("\"DataProtection:KeysPath\"", program);
        Assert.Contains("GetConnectionString(\"Default\")", program);
    }

    [Fact]
    public void Every_key_the_startup_validator_requires_is_set_by_terraform()
    {
        var tf = Read("infra", "web.tf");
        foreach (var required in new[] { "AzureAd__TenantId", "AzureAd__ClientId", "Sync__AllowedCallerObjectId", "GitHub__Org", "GitHub__Token", "ConnectionStrings__Default" })
            Assert.Matches($"\"{required}\"\\s*=", tf);
    }

    [Fact]
    public void Function_app_settings_in_terraform_bind_to_SyncApiOptions_and_the_timer()
    {
        var keys = KeysIn(Read("infra", "function.tf"), "app_settings = {");
        Assert.NotEmpty(keys);
        foreach (var key in keys.Where(k => !k.StartsWith("AzureWebJobsStorage__", StringComparison.Ordinal) && k != "SyncSchedule"))
            Assert.True(key.StartsWith("SyncApi__", StringComparison.Ordinal) && BindsToProperty(key), $"infra/function.tf sets '{key}', which SyncApiOptions does not bind.");

        // The schedule setting the timer trigger reads.
        Assert.Contains("%SyncSchedule%", Read("src", "CopilotUsage.SyncFunction", "SyncTimerFunction.cs"));
        Assert.Contains("SyncSchedule", keys);
        foreach (var required in new[] { "SyncApi__BaseUrl", "SyncApi__Scope", "SyncApi__ManagedIdentityClientId" })
            Assert.Contains(required, keys);
    }

    [Fact]
    public void Terraform_never_carries_the_github_token_value()
    {
        var all = string.Join("\n", Directory.GetFiles(Path.Combine(RepoRoot(), "infra"), "*.tf").Select(File.ReadAllText));
        Assert.DoesNotContain("azurerm_key_vault_secret", all);
        Assert.DoesNotMatch("variable\\s+\"[a-z_]*token[a-z_]*\"", all);
        Assert.Contains("@Microsoft.KeyVault(VaultName=", all);
    }

    [Fact]
    public void Deleted_infrastructure_files_stay_deleted()
    {
        Assert.False(File.Exists(Path.Combine(RepoRoot(), "infra", "main.bicep")));
        Assert.False(Directory.Exists(Path.Combine(RepoRoot(), ".github", "workflows")));
    }
}
