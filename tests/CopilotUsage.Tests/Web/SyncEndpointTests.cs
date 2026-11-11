using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopilotUsage.Data.Entities;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CopilotUsage.Tests.Web;

/// <summary>Stands in for the real sync: counts calls and can be held open to simulate a long-running one.</summary>
public sealed class FakeSyncService : ISyncService
{
    private int _calls;
    private volatile bool _running;
    public TaskCompletionSource? Hold { get; set; }
    public int Calls => Volatile.Read(ref _calls);
    public void Reset() => Volatile.Write(ref _calls, 0);
    public bool IsRunning => _running;

    public async Task<SyncResult> RunAsync(SyncTrigger trigger, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        _running = true;
        try
        {
            if (Hold is not null) await Hold.Task.WaitAsync(ct);
            return new SyncResult(42, SyncStatus.Succeeded, false,
                [new SyncStepResult("Seats", SyncStatus.Succeeded, 25, 0, null), new SyncStepResult("UserMetrics", SyncStatus.PartialFailure, 10, 1, "secret-org: boom")]);
        }
        finally { _running = false; }
    }
}

/// <summary>POST /api/sync: who may call it, what it does, and that it says nothing about why it refuses.</summary>
public class SyncEndpointTests
{
    private sealed record Harness(AppFactory App, FakeSyncService Sync) : IDisposable
    {
        public void Dispose() => App.Dispose();
    }

    private static Harness Start(Dictionary<string, string?>? settings = null)
    {
        var fake = new FakeSyncService();
        var app = new AppFactory("Production", settings, s => s.Replace(ServiceDescriptor.Singleton<ISyncService>(fake)));
        _ = app.Client();   // start the host: in demo mode it calls the sync service once at startup, which is not what the tests count
        fake.Reset();
        return new Harness(app, fake);
    }

    private static Task<HttpResponseMessage> Post(Harness h, string? bearer = null, string? cookie = null) =>
        h.App.Client().SendAsync(AppFactory.Request(HttpMethod.Post, "/api/sync", cookie, bearer));

    private static async Task AssertRefused(Harness h, HttpResponseMessage res, HttpStatusCode expected)
    {
        Assert.Equal(expected, res.StatusCode);
        Assert.Equal("", await res.Content.ReadAsStringAsync());          // no detail in the body
        if (expected == HttpStatusCode.Unauthorized)
            Assert.Equal("Bearer", string.Join(",", res.Headers.WwwAuthenticate)); // no error=... description either
        Assert.Equal(0, h.Sync.Calls);
    }

    [Fact]
    public async Task No_token_is_401()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h), HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    [InlineData("")]
    public async Task A_malformed_token_is_401(string token)
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, token.Length == 0 ? "x" : token), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_signed_with_the_wrong_key_is_401()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Key = TestTokens.UntrustedKey)), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_401()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Audience = "api://someone-else")), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_token_from_another_tenant_is_401()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Issuer = TestIds.V2Issuer(TestIds.OtherTenant))), HttpStatusCode.Unauthorized);
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Issuer = "https://evil.example/" + TestIds.Tenant + "/v2.0")), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_expired_or_not_yet_valid_token_is_401()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => { s.NotBefore = DateTime.UtcNow.AddHours(-3); s.Expires = DateTime.UtcNow.AddHours(-2); })), HttpStatusCode.Unauthorized);
        await AssertRefused(h, await Post(h, TestTokens.Create(s => { s.NotBefore = DateTime.UtcNow.AddHours(1); s.Expires = DateTime.UtcNow.AddHours(2); })), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_unsigned_token_is_401()
    {
        using var h = Start();
        var header = Convert.ToBase64String("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray()).TrimEnd('=');
        var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = TestIds.V2Issuer(), aud = TestIds.ClientId, oid = TestIds.FunctionOid, tid = TestIds.Tenant,
            roles = new[] { "Sync.Run" }, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        })).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await AssertRefused(h, await Post(h, $"{header}.{payload}."), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_valid_token_without_the_role_is_403()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims.Remove("roles"))), HttpStatusCode.Forbidden);
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims["roles"] = new[] { "Other.Role" })), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_role_with_the_wrong_caller_is_403()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => { s.Claims["oid"] = TestIds.SomeoneElseOid; s.Claims["sub"] = TestIds.SomeoneElseOid; })), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_delegated_user_token_is_403_even_when_it_carries_the_role_and_the_right_oid()
    {
        using var h = Start();
        // scope claim (delegated), a pairwise subject and idtyp absent: what a signed-in person's token looks like
        var delegated = TestTokens.Create(s => { s.Claims.Remove("idtyp"); s.Claims["scp"] = "access_as_user"; s.Claims["sub"] = "pairwise-user-subject"; });
        await AssertRefused(h, await Post(h, delegated), HttpStatusCode.Forbidden);
        // either marker alone is enough
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims["scp"] = "x")), HttpStatusCode.Forbidden);
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims["sub"] = "pairwise-user-subject")), HttpStatusCode.Forbidden);
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims["idtyp"] = "user")), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_token_for_the_right_object_but_another_tenant_claim_is_403()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims["tid"] = TestIds.OtherTenant)), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task When_a_client_id_is_configured_it_must_match_too()
    {
        using var h = Start(new() { ["Sync:AllowedCallerClientId"] = TestIds.FunctionClientId });
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims["azp"] = "00000000-0000-0000-0000-000000000001")), HttpStatusCode.Forbidden);
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims.Remove("azp"))), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.OK, (await Post(h, TestTokens.Create())).StatusCode);
    }

    [Fact]
    public async Task A_signed_in_dashboard_user_cannot_call_it()
    {
        using var h = Start();
        var res = await Post(h, cookie: h.App.SignedInCookie());
        await AssertRefused(h, res, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_user_who_has_a_cookie_and_a_role_less_token_still_gets_nothing()
    {
        using var h = Start();
        await AssertRefused(h, await Post(h, TestTokens.Create(s => s.Claims.Remove("roles")), h.App.SignedInCookie()), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_fully_valid_token_runs_the_sync_once_and_returns_a_small_summary_without_error_text()
    {
        using var h = Start();
        var res = await Post(h, TestTokens.Create());
        var body = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, h.Sync.Calls);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("Succeeded", root.GetProperty("status").GetString());
        Assert.Equal(42, root.GetProperty("runId").GetInt32());
        Assert.True(root.GetProperty("durationMs").GetInt32() >= 0);
        var steps = root.GetProperty("steps").EnumerateArray().ToList();
        Assert.Equal(2, steps.Count);
        Assert.Equal("Seats", steps[0].GetProperty("step").GetString());
        Assert.Equal(25, steps[0].GetProperty("rows").GetInt32());
        Assert.Equal("PartialFailure", steps[1].GetProperty("status").GetString());
        Assert.DoesNotContain("secret-org", body);
        Assert.DoesNotContain("boom", body);
    }

    [Fact]
    public async Task A_v1_format_token_as_issued_to_an_app_registration_on_the_default_token_version_is_accepted()
    {
        using var h = Start();
        var v1 = TestTokens.Create(s =>
        {
            s.Issuer = $"https://sts.windows.net/{TestIds.Tenant}/";
            s.Audience = $"api://{TestIds.ClientId}";
            s.Claims.Remove("azp"); s.Claims.Remove("idtyp"); s.Claims.Remove("ver");
            s.Claims["appid"] = TestIds.FunctionClientId;
        });
        Assert.Equal(HttpStatusCode.OK, (await Post(h, v1)).StatusCode);
        Assert.Equal(1, h.Sync.Calls);
    }

    [Fact]
    public async Task A_custom_app_id_uri_is_an_accepted_audience()
    {
        using var h = Start(new() { ["AzureAd:AppIdUri"] = "api://copilot-usage-dashboard" });
        Assert.Equal(HttpStatusCode.OK, (await Post(h, TestTokens.Create(s => s.Audience = "api://copilot-usage-dashboard"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(h, TestTokens.Create(s => s.Audience = TestIds.ClientId))).StatusCode);
    }

    [Fact]
    public async Task A_second_call_while_one_is_running_reports_already_running_and_starts_nothing()
    {
        using var h = Start();
        h.Sync.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Post(h, TestTokens.Create());
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!h.Sync.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(h.Sync.IsRunning);

        var second = await Post(h, TestTokens.Create());
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        using (var json = JsonDocument.Parse(await second.Content.ReadAsStringAsync()))
            Assert.Equal("AlreadyRunning", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, h.Sync.Calls);

        h.Sync.Hold.SetResult();
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(1, h.Sync.Calls);
    }

    [Fact]
    public async Task A_sync_that_outlasts_the_request_budget_gets_202_and_still_finishes()
    {
        using var h = Start(new() { ["Sync:RequestWaitSeconds"] = "1" });
        h.Sync.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var res = await Post(h, TestTokens.Create());
        using (var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync()))
            Assert.Equal("Running", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.True(h.Sync.IsRunning);

        Assert.Equal(HttpStatusCode.Conflict, (await Post(h, TestTokens.Create())).StatusCode); // not doubled meanwhile
        h.Sync.Hold.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (h.Sync.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.False(h.Sync.IsRunning);
        Assert.Equal(1, h.Sync.Calls);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Other_methods_are_405_with_or_without_a_token_and_never_sync(string method)
    {
        using var h = Start();
        foreach (var token in new string?[] { null, TestTokens.Create() })
        {
            var res = await h.App.Client().SendAsync(AppFactory.Request(new HttpMethod(method), "/api/sync", bearer: token));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
            Assert.Equal("POST", string.Join(",", res.Content.Headers.Allow.Concat(res.Headers.TryGetValues("Allow", out var a) ? a : [])));
        }
        Assert.Equal(0, h.Sync.Calls);
    }

    [Fact]
    public async Task Other_api_paths_answer_401_not_a_login_redirect()
    {
        using var h = Start();
        var res = await h.App.Client().SendAsync(AppFactory.Request(HttpMethod.Get, "/api/anything"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task The_call_needs_no_antiforgery_token_and_works_over_plain_http_inside_the_proxy()
    {
        using var h = Start();
        var res = await h.App.Client().SendAsync(AppFactory.Request(HttpMethod.Post, "/api/sync", bearer: TestTokens.Create(), https: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task With_the_real_sync_service_in_demo_mode_the_endpoint_reseeds_and_reports_success()
    {
        using var app = new AppFactory("Production");
        var res = await app.Client().SendAsync(AppFactory.Request(HttpMethod.Post, "/api/sync", bearer: TestTokens.Create()));
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Succeeded", body.GetProperty("status").GetString());
        Assert.Equal("DemoSeed", body.GetProperty("steps")[0].GetProperty("step").GetString());
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CopilotUsage.Data.CopilotUsageDbContext>();
        Assert.Contains(db.SyncRuns.ToList(), r => r.Trigger == SyncTrigger.Api && r.Status == SyncStatus.Succeeded);
    }

    [Fact]
    public async Task Without_sign_in_in_development_the_endpoint_is_open_for_local_use()
    {
        using var app = new AppFactory("Development");
        var res = await app.Client("localhost").SendAsync(AppFactory.Request(HttpMethod.Post, "/api/sync", https: false));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }
}

public class SyncRecoveryTests
{
    [Fact]
    public async Task Runs_left_marked_running_by_a_stopped_app_are_closed_as_failed_and_visible_in_health()
    {
        var now = new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
        await using var host = new TestHost(now, demo: true);
        await host.WithDbAsync(async db =>
        {
            db.SyncRuns.Add(new SyncRun { StartedAt = now.AddMinutes(-30), Status = SyncStatus.Running, Trigger = SyncTrigger.Api });
            await db.SaveChangesAsync();
            return 0;
        });
        var closed = await host.WithDbAsync(db => CopilotUsage.Web.Sync.SyncRecovery.CloseInterruptedRunsAsync(db, host.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        Assert.Equal(1, closed);
        var run = await host.WithDbAsync(db => Task.FromResult(db.SyncRuns.Single()));
        Assert.Equal(SyncStatus.Failed, run.Status);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains("Interrupted", run.Error);
        Assert.Equal(0, await host.WithDbAsync(db => CopilotUsage.Web.Sync.SyncRecovery.CloseInterruptedRunsAsync(db, host.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)));
    }
}

public class InAppTimerTests
{
    private static bool WorkerRegistered(AppFactory app) =>
        app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<SyncWorker>().Any();

    [Fact]
    public void The_in_app_timer_is_off_by_default_in_production_and_development()
    {
        using var prod = new AppFactory("Production");
        _ = prod.Client();
        Assert.False(WorkerRegistered(prod));
        using var dev = new AppFactory("Development");
        _ = dev.Client("localhost");
        Assert.False(WorkerRegistered(dev));
    }

    [Fact]
    public void It_runs_only_when_asked_for()
    {
        using var app = new AppFactory("Development", new() { ["Sync:InAppTimer"] = "true" });
        _ = app.Client("localhost");
        Assert.True(WorkerRegistered(app));
    }
}
