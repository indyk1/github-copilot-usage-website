using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Auth;
using CopilotUsage.Web.Config;
using CopilotUsage.Web.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.Web;

public class DataProtectionKeyTests
{
    [Fact]
    public async Task A_cookie_issued_by_one_instance_is_accepted_by_a_second_instance_sharing_the_keys_directory()
    {
        var keys = Path.Combine(Path.GetTempPath(), "copilot-keys-" + Guid.NewGuid().ToString("N"));
        try
        {
            string cookie;
            using (var first = new AppFactory("Production", new() { ["DataProtection:KeysPath"] = keys }))
            {
                _ = first.Client();
                cookie = first.SignedInCookie("Grace Hopper");
            }   // the first instance is gone, as when the Free plan unloads the app
            Assert.NotEmpty(Directory.GetFiles(keys, "*.xml"));

            using var second = new AppFactory("Production", new() { ["DataProtection:KeysPath"] = keys });
            var res = await second.Client().SendAsync(AppFactory.Request(HttpMethod.Get, "/", cookie: cookie));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Contains("Grace Hopper", await res.Content.ReadAsStringAsync());
        }
        finally { try { Directory.Delete(keys, true); } catch (IOException) { } }
    }

    [Fact]
    public async Task Without_a_shared_keys_directory_a_cookie_from_another_instance_is_not_accepted()
    {
        using var first = new AppFactory("Production");
        _ = first.Client();
        var cookie = first.SignedInCookie();
        using var second = new AppFactory("Production");
        var res = await second.Client().SendAsync(AppFactory.Request(HttpMethod.Get, "/", cookie: cookie));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
    }

    [Fact]
    public void An_unwritable_keys_directory_stops_the_app_from_starting()
    {
        var file = Path.GetTempFileName();   // a file where a directory is needed
        try
        {
            using var app = new AppFactory("Production", new() { ["DataProtection:KeysPath"] = Path.Combine(file, "keys") });
            Assert.Throws<InvalidOperationException>(() => app.CreateClient());
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Production_keeps_the_keys_next_to_the_database_and_the_application_name_is_stable()
    {
        var prod = new ConfigurationBuilderHelper().Production();
        Assert.Equal("/home/data/dataprotection-keys", prod);
        using var app = new AppFactory("Production");
        var opts = app.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>>().Value;
        Assert.Equal("CopilotUsageDashboard", opts.ApplicationDiscriminator);
    }

    private sealed class ConfigurationBuilderHelper
    {
        public string? Production()
        {
            var root = Path.GetDirectoryName(typeof(CopilotUsage.Web.AppVersion).Assembly.Location)!;
            return new Microsoft.Extensions.Configuration.ConfigurationBuilder().SetBasePath(root)
                .AddJsonFile("appsettings.json").AddJsonFile("appsettings.Production.json").Build()["DataProtection:KeysPath"];
        }
    }
}

public sealed class CaptureProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => new L(categoryName, Lines);
    public void Dispose() { }
    private sealed class L(string cat, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> f) => lines.Enqueue($"{cat}: {f(state, ex)}");
    }
}

/// <summary>An unsigned or wrongly signed token with every correct claim must die at signature validation, before any claim is read.</summary>
public class TokenRejectionTests
{
    private static string Unsigned()
    {
        string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = B64("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray());
        var payload = B64(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["iss"] = TestIds.V2Issuer(), ["aud"] = TestIds.ClientId, ["oid"] = TestIds.FunctionOid, ["sub"] = TestIds.FunctionOid,
            ["tid"] = TestIds.Tenant, ["azp"] = TestIds.FunctionClientId, ["idtyp"] = "app", ["roles"] = new[] { "Sync.Run" },
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
        }));
        return $"{header}.{payload}.";
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("wrong-key")]
    [InlineData("tampered")]
    public async Task Rejected_at_validation_as_401_with_no_claim_trusted_and_the_log_says_why(string kind)
    {
        var log = new CaptureProvider();
        var fake = new FakeSyncService();
        using var app = new AppFactory("Production", null, s =>
        {
            s.AddLogging(b => b.AddProvider(log));
            s.Replace(ServiceDescriptor.Singleton<ISyncService>(fake));
        });
        _ = app.Client();
        fake.Reset();
        while (log.Lines.TryDequeue(out _)) { }

        var token = kind switch
        {
            "unsigned" => Unsigned(),
            "wrong-key" => TestTokens.Create(s => s.Key = TestTokens.UntrustedKey),
            _ => TamperPayload(TestTokens.Create(s => s.Claims["oid"] = TestIds.SomeoneElseOid)),
        };
        var res = await app.Client().SendAsync(AppFactory.Request(HttpMethod.Post, "/api/sync", bearer: token));

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);   // 401, not 403: authentication failed, the policy never saw a user
        Assert.Equal(0, fake.Calls);
        var lines = log.Lines.ToList();
        Assert.Contains(lines, l => l.Contains("Sync token rejected during validation"));
        Assert.DoesNotContain(lines, l => l.Contains("has no oid") || l.Contains("lacks the Sync.Run role") || l.Contains("not the allowed one"));
        Assert.Contains(lines, l => l.Contains("no authenticated caller"));
    }

    // Swap in a payload claiming the right caller while keeping the old signature.
    private static string TamperPayload(string signed)
    {
        var parts = signed.Split('.');
        var good = TestTokens.Create();
        parts[1] = good.Split('.')[1];
        return string.Join('.', parts);
    }

    [Fact]
    public async Task The_policy_handler_reads_no_claim_of_an_unauthenticated_principal()
    {
        var handler = new SyncCallerHandler(Options.Create(new SyncOptions { AllowedCallerObjectId = TestIds.FunctionOid }),
            Options.Create(new AzureAdOptions { TenantId = TestIds.Tenant }), Microsoft.Extensions.Logging.Abstractions.NullLogger<SyncCallerHandler>.Instance);
        // Claims present but no authentication type: IsAuthenticated is false.
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("oid", TestIds.FunctionOid), new("sub", TestIds.FunctionOid), new("tid", TestIds.Tenant), new("roles", "Sync.Run")]));
        var req = new SyncCallerRequirement();
        var ctx = new Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext([req], user, null);
        await handler.HandleAsync(ctx);
        Assert.False(ctx.HasSucceeded);
    }
}
