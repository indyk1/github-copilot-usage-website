using System.Net;
using System.Text.RegularExpressions;
using CopilotUsage.Tests.Support;
using CopilotUsage.Web.Auth;
using CopilotUsage.Web.Config;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CopilotUsage.Tests.Web;

/// <summary>Dashboard sign-in, tested against the whole host with placeholder tenant values.</summary>
public class SignInTests
{
    private static HttpRequestMessage Get(string path, string? cookie = null, bool html = true, bool https = true)
    {
        var r = AppFactory.Request(HttpMethod.Get, path, cookie, https: https);
        if (html) r.Headers.Accept.ParseAdd("text/html");
        return r;
    }

    private static Dictionary<string, string> Query(Uri uri) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""));

    [Theory]
    [InlineData("/")]
    [InlineData("/users")]
    [InlineData("/users/someone")]
    [InlineData("/no-such-page")]
    public async Task Anonymous_page_requests_are_sent_to_sign_in_with_a_correct_https_redirect_uri(string path)
    {
        using var app = new AppFactory("Production");
        var res = await app.Client().SendAsync(Get(path));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var to = res.Headers.Location!;
        Assert.Equal("login.microsoftonline.com", to.Host);
        Assert.Equal($"/{TestIds.Tenant}/oauth2/v2.0/authorize", to.AbsolutePath);
        var q = Query(to);
        Assert.Equal(TestIds.ClientId, q["client_id"]);
        Assert.Equal("id_token", q["response_type"]);            // ID token only: no code, so no client secret
        Assert.Equal("form_post", q["response_mode"]);
        Assert.Equal("https://dashboard.example.test/signin-oidc", q["redirect_uri"]); // https and the public host
        Assert.Equal("openid profile", q["scope"]);
        Assert.False(string.IsNullOrEmpty(q["nonce"]));
        Assert.False(q.ContainsKey("client_secret"));
        Assert.False(q.ContainsKey("code_challenge"));
    }

    [Fact]
    public async Task The_redirect_uri_follows_the_forwarded_host_scheme_and_a_configured_public_base_url_wins()
    {
        using var app = new AppFactory("Production");
        var res = await app.Client("my-site.azurewebsites.net").SendAsync(Get("/"));
        Assert.Equal("https://my-site.azurewebsites.net/signin-oidc", Query(res.Headers.Location!)["redirect_uri"]);

        using var pinned = new AppFactory("Production", new() { ["AzureAd:PublicBaseUrl"] = "https://dashboard.contoso.example/" });
        var res2 = await pinned.Client("my-site.azurewebsites.net").SendAsync(Get("/"));
        Assert.Equal("https://dashboard.contoso.example/signin-oidc", Query(res2.Headers.Location!)["redirect_uri"]);
    }

    [Fact]
    public async Task The_challenge_cookies_are_secure_and_cross_site_capable_for_the_post_back_from_microsoft()
    {
        using var app = new AppFactory("Production");
        var res = await app.Client().SendAsync(Get("/"));
        var cookies = res.Headers.GetValues("Set-Cookie").ToList();

        Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.Correlation.") && c.Contains("secure", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=none", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cookies, c => c.StartsWith(".AspNetCore.OpenIdConnect.Nonce.") && c.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.All(cookies, c => Assert.Contains("httponly", c, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_sign_in_cookie_is_http_only_secure_and_lax()
    {
        using var app = new AppFactory("Production");
        var o = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthSetup.CookieScheme);
        Assert.True(o.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, o.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, o.Cookie.SameSite);
        Assert.Equal("/access-denied", o.AccessDeniedPath.Value);
    }

    [Fact]
    public async Task Healthz_stays_anonymous_and_the_sign_in_pages_and_assets_work_without_a_session()
    {
        using var app = new AppFactory("Production");
        var client = app.Client();
        foreach (var path in new[] { "/healthz", "/signed-out", "/access-denied", "/theme.css", "/app.css", "/js/glass.js", "/not-found" })
        {
            var res = await client.SendAsync(Get(path));
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{path} gave {res.StatusCode}");
        }
        var signedOut = await (await client.SendAsync(Get("/signed-out"))).Content.ReadAsStringAsync();
        Assert.Contains("You have signed out", signedOut);
        var denied = await (await client.SendAsync(Get("/access-denied"))).Content.ReadAsStringAsync();
        Assert.Contains("Access denied", denied);
        Assert.Matches(@"href=""[^""]*theme[^""]*\.css""", denied); // themed
    }

    [Fact]
    public async Task The_blazor_circuit_endpoint_is_protected_too()
    {
        using var app = new AppFactory("Production");
        var req = AppFactory.Request(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        var res = await app.Client().SendAsync(req);
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("login.microsoftonline.com", res.Headers.Location?.Host);
    }

    [Fact]
    public async Task A_signed_in_user_sees_the_page_with_their_name_and_a_sign_out_form()
    {
        using var app = new AppFactory("Production");
        var res = await app.Client().SendAsync(Get("/", app.SignedInCookie("Ada Lovelace", "ada@example.test")));
        var html = await res.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Ada Lovelace", html);
        Assert.Contains("action=\"/auth/signout\"", html);
        Assert.DoesNotContain("No sign-in", html);
    }

    [Fact]
    public async Task A_bearer_token_does_not_open_the_pages()
    {
        using var app = new AppFactory("Production");
        var req = AppFactory.Request(HttpMethod.Get, "/", bearer: TestTokens.Create());
        req.Headers.Accept.ParseAdd("text/html");
        var res = await app.Client().SendAsync(req);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("login.microsoftonline.com", res.Headers.Location!.Host);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_returns_through_microsofts_logout_to_the_signed_out_page()
    {
        using var app = new AppFactory("Production");
        var res = await app.Client().SendAsync(AppFactory.Request(HttpMethod.Post, "/auth/signout", cookie: app.SignedInCookie()));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        var to = res.Headers.Location!;
        Assert.Equal($"/{TestIds.Tenant}/oauth2/v2.0/logout", to.AbsolutePath);
        Assert.Equal("https://dashboard.example.test/signout-callback-oidc", Query(to)["post_logout_redirect_uri"]);
        Assert.Contains(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".CopilotUsage.Auth=;")); // cookie cleared
    }

    // ---- the round trip: the same request Microsoft's page makes after sign-in ----

    private static async Task<(HttpResponseMessage Post, string Cookies)> SignInWithAsync(AppFactory app, Func<string, string> idToken)
    {
        var client = app.Client();
        var challenge = await client.SendAsync(Get("/"));
        var q = Query(challenge.Headers.Location!);
        var cookies = string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["id_token"] = idToken(q["nonce"]), ["state"] = q["state"] });
        var post = AppFactory.Request(HttpMethod.Post, "/signin-oidc", cookie: cookies);
        post.Content = form;
        return (await client.SendAsync(post), cookies);
    }

    [Fact]
    public async Task A_valid_id_token_from_our_tenant_signs_the_user_in_and_the_session_opens_the_dashboard()
    {
        using var app = new AppFactory("Production");
        var (res, _) = await SignInWithAsync(app, nonce => TestTokens.IdToken(nonce));

        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("/", res.Headers.Location!.OriginalString);
        var auth = res.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(".CopilotUsage.Auth="));
        Assert.Contains("httponly", auth, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", auth, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", auth, StringComparison.OrdinalIgnoreCase);

        var page = await app.Client().SendAsync(Get("/", auth.Split(';')[0]));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Ada Lovelace", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_id_token_from_another_tenant_is_refused_and_lands_on_the_access_denied_page()
    {
        using var app = new AppFactory("Production");
        var (res, _) = await SignInWithAsync(app, nonce => TestTokens.IdToken(nonce, issuerTenant: TestIds.OtherTenant, tidClaim: TestIds.OtherTenant));

        Assert.Equal("/access-denied", res.Headers.Location!.OriginalString);
        Assert.DoesNotContain(res.Headers.GetValues("Set-Cookie"), c => c.StartsWith(".CopilotUsage.Auth=") && !c.Contains("expires=Thu, 01 Jan 1970"));
    }

    [Fact]
    public async Task An_id_token_with_our_issuer_but_a_foreign_tenant_claim_is_refused()
    {
        using var app = new AppFactory("Production");
        var (res, _) = await SignInWithAsync(app, nonce => TestTokens.IdToken(nonce, tidClaim: TestIds.OtherTenant));
        Assert.Equal("/access-denied", res.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task An_id_token_for_another_application_or_with_a_forged_signature_is_refused()
    {
        using var app = new AppFactory("Production");
        var (wrongAudience, _) = await SignInWithAsync(app, nonce => TestTokens.Create(s => { s.Audience = "someone-else"; s.Claims.Clear(); s.Claims["nonce"] = nonce; s.Claims["tid"] = TestIds.Tenant; }));
        Assert.Equal("/access-denied", wrongAudience.Headers.Location!.OriginalString);
        var (forged, _) = await SignInWithAsync(app, nonce => TestTokens.Create(s => { s.Key = TestTokens.UntrustedKey; s.Claims.Clear(); s.Claims["nonce"] = nonce; s.Claims["tid"] = TestIds.Tenant; }));
        Assert.Equal("/access-denied", forged.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_post_back_without_the_sign_in_cookies_is_refused()
    {
        using var app = new AppFactory("Production");
        var post = AppFactory.Request(HttpMethod.Post, "/signin-oidc");
        post.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id_token"] = "x.y.z", ["state"] = "nope" });
        var res = await app.Client().SendAsync(post);
        Assert.Equal("/access-denied", res.Headers.Location!.OriginalString);
    }

    // ---- development without sign-in, and the refusals ----

    [Fact]
    public async Task Development_with_sign_in_off_serves_pages_anonymously_and_says_so()
    {
        using var app = new AppFactory("Development");
        var res = await app.Client("localhost").SendAsync(Get("/", https: false));
        var html = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("No sign-in", html);
        Assert.DoesNotContain("action=\"/auth/signout\"", html);
    }

    [Fact]
    public async Task Development_can_turn_sign_in_on_with_the_same_settings_and_is_then_challenged()
    {
        using var app = new AppFactory("Development", new() { ["Auth:Enabled"] = "true" });
        var res = await app.Client("localhost").SendAsync(Get("/", https: false));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal("login.microsoftonline.com", res.Headers.Location!.Host);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Sign_in_cannot_be_switched_off_outside_development(string env)
    {
        using var app = new AppFactory(env, new() { ["Auth:Enabled"] = "false" });
        Assert.Throws<InvalidOperationException>(() => app.CreateClient()); // the host never starts
    }

    [Theory]
    [InlineData("AzureAd:TenantId")]
    [InlineData("AzureAd:ClientId")]
    [InlineData("Sync:AllowedCallerObjectId")]
    public void Production_does_not_start_without_sign_in_settings(string missing)
    {
        using var app = new AppFactory("Production", new() { [missing] = null });
        Assert.Throws<InvalidOperationException>(() => app.CreateClient());
    }

    [Fact]
    public void Production_does_not_start_for_the_multi_tenant_common_endpoint()
    {
        using var app = new AppFactory("Production", new() { ["AzureAd:TenantId"] = "common" });
        Assert.Throws<InvalidOperationException>(() => app.CreateClient());
    }

    private static IReadOnlyList<string> Validate(bool dev, params (string, string?)[] kv) =>
        StartupConfigValidator.Validate(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Demo:Enabled"] = "true", ["ConnectionStrings:Default"] = "Data Source=x.db" }
                .Concat(kv.Select(x => new KeyValuePair<string, string?>(x.Item1, x.Item2))).ToDictionary()).Build(), dev);

    [Fact]
    public void The_startup_check_names_every_missing_key_and_rejects_aliases_and_bad_ids()
    {
        var errors = Validate(false);
        Assert.Contains(errors, e => e.StartsWith("AzureAd:TenantId"));
        Assert.Contains(errors, e => e.StartsWith("AzureAd:ClientId"));
        Assert.Contains(errors, e => e.StartsWith("Sync:AllowedCallerObjectId"));
        Assert.Contains(Validate(false, ("AzureAd:TenantId", "organizations"), ("AzureAd:ClientId", TestIds.ClientId), ("Sync:AllowedCallerObjectId", TestIds.FunctionOid)),
            e => e.Contains("other tenants"));
        Assert.Contains(Validate(false, ("AzureAd:TenantId", TestIds.Tenant), ("AzureAd:ClientId", "not-a-guid"), ("Sync:AllowedCallerObjectId", TestIds.FunctionOid)),
            e => e.StartsWith("AzureAd:ClientId must"));
        Assert.Contains(Validate(false, ("Auth:Enabled", "false"), ("AzureAd:TenantId", TestIds.Tenant), ("AzureAd:ClientId", TestIds.ClientId), ("Sync:AllowedCallerObjectId", TestIds.FunctionOid)),
            e => e.Contains("only honoured in the Development"));
        Assert.Empty(Validate(false, ("AzureAd:TenantId", TestIds.Tenant), ("AzureAd:ClientId", TestIds.ClientId), ("Sync:AllowedCallerObjectId", TestIds.FunctionOid)));
        Assert.Empty(Validate(true, ("Auth:Enabled", "false")));
    }

    [Fact]
    public void Auth_mode_can_only_be_off_in_development()
    {
        var off = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:Enabled"] = "false" }).Build();
        Assert.False(AuthMode.Resolve(off, isDevelopment: true));
        Assert.True(AuthMode.Resolve(off, isDevelopment: false));
        Assert.True(AuthMode.Resolve(new ConfigurationBuilder().Build(), isDevelopment: true));
    }
}
