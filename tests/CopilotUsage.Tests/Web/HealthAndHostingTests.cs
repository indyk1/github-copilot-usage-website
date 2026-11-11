using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotUsage.Core;
using CopilotUsage.Data;
using CopilotUsage.Data.Entities;
using CopilotUsage.Data.Queries;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web;
using CopilotUsage.Web.Config;
using CopilotUsage.Web.Health;
using CopilotUsage.Web.Hosting;
using CopilotUsage.Web.Sync;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.Web;

public class HealthEndpointTests
{
    [Fact]
    public async Task Healthz_reports_database_and_last_sync_without_any_secret()
    {
        using var app = new AppFactory("Production", new()
        {
            ["GitHub:Token"] = "ghp_MUSTNOTAPPEAR", ["GitHub:Org"] = "secret-org-name",
        });
        var res = await app.Client().GetAsync("/healthz");
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Contains("no-store", res.Headers.CacheControl!.ToString());
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("databaseReachable").GetBoolean());
        Assert.Equal("Succeeded", root.GetProperty("lastSyncStatus").GetString());
        Assert.True(root.GetProperty("lastSuccessfulSync").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.True(root.GetProperty("demo").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("version").GetString()));
        Assert.DoesNotContain("MUSTNOTAPPEAR", body);
        Assert.DoesNotContain("secret-org-name", body);
        Assert.DoesNotContain("Data Source", body);
    }

    [Fact]
    public async Task Healthz_is_not_caught_by_the_unknown_user_middleware_even_for_browser_requests()
    {
        using var app = new AppFactory();
        var client = app.Client();

        var health = new HttpRequestMessage(HttpMethod.Get, "/healthz");
        health.Headers.Accept.ParseAdd("text/html");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(health)).StatusCode);

        // The same middleware does still answer an unknown login with a real 404.
        var unknown = new HttpRequestMessage(HttpMethod.Get, "/users/nobody-here");
        unknown.Headers.Accept.ParseAdd("text/html");
        unknown.Headers.Add("X-Forwarded-Proto", "https"); // as App Service sends it; otherwise Production redirects first
        unknown.Headers.Add("Cookie", app.SignedInCookie()); // pages need a signed-in user; /healthz above did not
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(unknown)).StatusCode);
    }
}

public class HealthReporterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static Task<HealthReport> Report(TestHost host) => host.QueryAsync(q =>
        HealthReporter.BuildAsync(q, new SyncOptions { IntervalHours = 4 }, new DemoOptions { Enabled = true }, host.Time, CancellationToken.None));

    [Fact]
    public async Task Never_synced_is_degraded_but_the_database_is_reachable()
    {
        await using var host = new TestHost(Now);
        var r = await Report(host);
        Assert.Equal("degraded", r.Status);
        Assert.True(r.DatabaseReachable);
        Assert.True(r.IsServing);
        Assert.Null(r.LastSuccessfulSync);
    }

    [Fact]
    public async Task A_recent_successful_sync_is_ok_and_an_old_one_is_stale()
    {
        await using var host = new TestHost(Now, demo: true);
        await host.Sync.RunAsync(SyncTrigger.Demo);

        var fresh = await Report(host);
        Assert.Equal("ok", fresh.Status);
        Assert.Equal(Now, fresh.LastSuccessfulSync);

        host.Time.Advance(TimeSpan.FromHours(13)); // more than three 4-hour intervals
        var stale = await Report(host);
        Assert.Equal("degraded", stale.Status);
        Assert.Equal(Now, stale.LastSuccessfulSync);
    }

    [Fact]
    public async Task A_failed_latest_sync_is_degraded_and_keeps_the_last_good_time()
    {
        await using var host = new TestHost(Now, demo: true);
        await host.Sync.RunAsync(SyncTrigger.Demo);
        await host.WithDbAsync(async db =>
        {
            db.SyncRuns.Add(new SyncRun { StartedAt = Now.AddMinutes(5), FinishedAt = Now.AddMinutes(6), Status = SyncStatus.Failed, Trigger = SyncTrigger.Scheduled, Error = "boom" });
            await db.SaveChangesAsync();
            return 0;
        });
        host.Time.Advance(TimeSpan.FromMinutes(10));

        var r = await Report(host);
        Assert.Equal("degraded", r.Status);
        Assert.Equal("Failed", r.LastSyncStatus);
        Assert.Equal(Now, r.LastSuccessfulSync);
        Assert.DoesNotContain("boom", JsonSerializer.Serialize(r)); // error text is never exposed
    }

    [Fact]
    public async Task An_unreachable_database_is_unhealthy_and_not_serving()
    {
        var options = new DbContextOptionsBuilder<CopilotUsageDbContext>().UseSqlite("Data Source=/nonexistent-dir-for-test/x.db").Options;
        await using var db = new CopilotUsageDbContext(options);
        var queries = new UsageQueries(db, TimeProvider.System, Options.Create(new CopilotOptions()));

        var r = await HealthReporter.BuildAsync(queries, new SyncOptions(), new DemoOptions(), TimeProvider.System, CancellationToken.None);

        Assert.Equal("unhealthy", r.Status);
        Assert.False(r.DatabaseReachable);
        Assert.False(r.IsServing);
    }
}

public class SecurityHeaderTests
{
    private static readonly string[] Pages = ["/", "/users", "/healthz", "/no-such-page"];

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        using var app = new AppFactory("Production");
        var client = app.Client();
        foreach (var path in Pages)
        {
            var res = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, path) { Headers = { { "X-Forwarded-Proto", "https" } } });
            Assert.True(res.Headers.Contains("X-Content-Type-Options"), path);
            Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
            Assert.Contains("camera=()", res.Headers.GetValues("Permissions-Policy").Single());
            Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
        }
    }

    [Fact]
    public async Task Csp_allows_only_this_origin_plus_avatars_and_has_no_unsafe_script()
    {
        using var app = new AppFactory("Production");
        var res = await app.Client().GetAsync("/healthz");
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);   // includes the Blazor Server websocket
        Assert.Contains("https://avatars.githubusercontent.com", csp);
        Assert.Contains("https://github.com", csp);   // demo identicons
        var formAction = Regex.Match(csp, @"form-action [^;]*").Value;
        Assert.Equal("form-action 'self' https://login.microsoftonline.com", formAction); // sign-out redirects there; nothing else may receive a form
        Assert.Contains("object-src 'none'", csp);
        var script = Regex.Match(csp, @"script-src [^;]*").Value;
        Assert.DoesNotContain("unsafe", script);
        Assert.DoesNotContain("{nonce}", csp);
    }

    [Fact]
    public async Task The_import_map_script_carries_the_same_fresh_nonce_the_policy_allows()
    {
        using var app = new AppFactory("Production");
        var client = app.Client();
        var nonces = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var res = await client.SendAsync(AppFactory.Request(HttpMethod.Get, "/", cookie: app.SignedInCookie()));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var html = await res.Content.ReadAsStringAsync();
            var csp = res.Headers.GetValues("Content-Security-Policy").Single();
            var inCsp = Regex.Match(csp, @"'nonce-([^']+)'").Groups[1].Value;
            var onScript = Regex.Match(html, @"<script[^>]*type=""importmap""[^>]*>").Value;
            Assert.NotEmpty(inCsp);
            Assert.Contains($"nonce=\"{inCsp}\"", onScript);
            nonces.Add(inCsp);
        }
        Assert.NotEqual(nonces[0], nonces[1]);
    }

    [Fact]
    public void The_policy_can_be_replaced_or_switched_off_by_configuration()
    {
        string? Resolve(string? v) => SecurityHeaders.ResolveCsp(new ConfigurationBuilder()
            .AddInMemoryCollection(v is null ? [] : new Dictionary<string, string?> { [SecurityHeaders.SectionKey] = v }).Build());

        Assert.Equal(SecurityHeaders.DefaultCsp, Resolve(null));
        Assert.Equal("default-src 'none'", Resolve("default-src 'none'"));
        Assert.Null(Resolve(""));
    }
}

public class ProxyAndHttpsTests
{
    [Fact]
    public async Task Production_redirects_plain_http_to_https_but_never_the_health_probe()
    {
        using var app = new AppFactory("Production");
        var client = app.Client();

        var page = await client.GetAsync("/users");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, page.StatusCode);
        Assert.Equal("https://dashboard.example.test/users", page.Headers.Location!.ToString());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Production_behind_the_proxy_trusts_forwarded_proto_and_sends_hsts()
    {
        using var app = new AppFactory("Production");
        var req = AppFactory.Request(HttpMethod.Get, "/", cookie: app.SignedInCookie());
        req.Headers.Add("X-Forwarded-For", "203.0.113.7");
        var res = await app.Client().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("max-age", res.Headers.GetValues("Strict-Transport-Security").Single());
    }

    [Fact]
    public async Task Development_over_plain_http_is_not_redirected_and_has_no_hsts()
    {
        using var app = new AppFactory("Development");
        var res = await app.Client("localhost").GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }
}

public class ProductionConfigTests
{
    private static string ContentRoot()
    {
        using var app = new AppFactory("Development");
        return app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
    }

    private static IConfigurationRoot Load(string env, params (string, string?)[] extra) => new ConfigurationBuilder()
        .SetBasePath(ContentRoot())
        .AddJsonFile("appsettings.json", optional: false)
        .AddJsonFile($"appsettings.{env}.json", optional: true)
        .AddInMemoryCollection(extra.ToDictionary(e => e.Item1, e => e.Item2))
        .Build();

    [Fact]
    public void Production_defaults_use_the_app_service_share_and_no_demo()
    {
        var c = Load("Production");
        Assert.Equal("Data Source=/home/data/copilot-usage.db", c.GetConnectionString("Default"));
        Assert.False(c.GetValue<bool>("Demo:Enabled"));
        Assert.Equal("Warning", c["Logging:LogLevel:System.Net.Http.HttpClient"]);
        Assert.Equal("Warning", c["Logging:LogLevel:Microsoft.EntityFrameworkCore"]);
        Assert.Equal("Information", c["Logging:LogLevel:Default"]);
    }

    [Fact]
    public void Development_keeps_a_relative_database_and_demo_mode()
    {
        var c = Load("Development");
        Assert.Equal("Data Source=data/copilot-usage.db", c.GetConnectionString("Default"));
        Assert.True(c.GetValue<bool>("Demo:Enabled"));
        Assert.False(c.GetValue<bool>("Auth:Enabled"));
    }

    [Fact]
    public void Production_files_never_switch_sign_in_off_or_the_timer_on()
    {
        var c = Load("Production");
        Assert.Null(c["Auth:Enabled"]);
        Assert.False(c.GetValue<bool>("Sync:InAppTimer"));
    }

    [Fact]
    public void Production_refuses_to_start_without_an_org_and_token_but_otherwise_validates()
    {
        Assert.Equal(5, StartupConfigValidator.Validate(Load("Production"), isDevelopment: false).Count); // org, token, tenant, client, caller
        Assert.Empty(StartupConfigValidator.Validate(Load("Production", ("GitHub:Org", "acme"), ("GitHub:Token", "t"),
            ("AzureAd:TenantId", TestIds.Tenant), ("AzureAd:ClientId", TestIds.ClientId), ("Sync:AllowedCallerObjectId", TestIds.FunctionOid)), isDevelopment: false));
    }

    [Fact]
    public void No_settings_file_in_the_repo_holds_a_token()
    {
        foreach (var env in new[] { "Production", "Development" })
            Assert.True(string.IsNullOrEmpty(Load(env)["GitHub:Token"]), env);
    }

    [Fact]
    public void An_unresolved_key_vault_reference_is_rejected_with_an_explanation()
    {
        var errors = StartupConfigValidator.Validate(Load("Production", ("GitHub:Org", "acme"),
            ("AzureAd:TenantId", TestIds.Tenant), ("AzureAd:ClientId", TestIds.ClientId), ("Sync:AllowedCallerObjectId", TestIds.FunctionOid),
            ("GitHub:Token", "@Microsoft.KeyVault(SecretUri=https://example.vault.azure.net/secrets/x/)")), isDevelopment: false);
        Assert.Contains("Key Vault reference", Assert.Single(errors));
    }
}

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3+0123456789abcdef", "1.2.3", "0123456")]
    [InlineData("1.2.3", "1.2.3", null)]
    [InlineData("1.2.3+", "1.2.3", null)]
    [InlineData(null, "unknown", null)]
    public void Parses_version_and_short_commit(string? input, string version, string? commit) =>
        Assert.Equal((version, commit), AppVersion.Parse(input));

    [Fact]
    public void The_running_assembly_has_a_version() => Assert.NotEqual("unknown", AppVersion.Version);
}
