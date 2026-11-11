using System.Net;
using System.Security.Claims;
using CopilotUsage.Web;
using CopilotUsage.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using CopilotUsage.Tests.Support;

namespace CopilotUsage.Tests.Web;

/// <summary>
/// The whole app host (real middleware, demo data in a temp SQLite file), driven over an in-process server.
/// Sign-in is configured with placeholder tenant values. The only test-specific change to the auth setup is where the identity
/// provider's metadata comes from: a fixed document holding the TEST signing key and the real-format endpoints, instead of a
/// download from login.microsoftonline.com. Every validation rule is the production code's.
/// </summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    private readonly string _env;
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _services;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "copilot-usage-tests-" + Guid.NewGuid().ToString("N"));

    public AppFactory(string env = "Production", Dictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        _env = env;
        _services = services;
        Directory.CreateDirectory(_dir);
        _settings = new()
        {
            ["Demo:Enabled"] = "true",
            ["ConnectionStrings:Default"] = $"Data Source={Path.Combine(_dir, "t.db")}",
            ["DataProtection:KeysPath"] = Path.Combine(_dir, "keys"),
            ["AzureAd:TenantId"] = TestIds.Tenant,
            ["AzureAd:ClientId"] = TestIds.ClientId,
            ["Sync:AllowedCallerObjectId"] = TestIds.FunctionOid,
        };
        foreach (var kv in settings ?? []) _settings[kv.Key] = kv.Value;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_env);
        foreach (var kv in _settings) builder.UseSetting(kv.Key, kv.Value);
        builder.ConfigureTestServices(services =>
        {
            var metadata = new OpenIdConnectConfiguration
            {
                Issuer = TestIds.V2Issuer(),
                AuthorizationEndpoint = $"https://login.microsoftonline.com/{TestIds.Tenant}/oauth2/v2.0/authorize",
                EndSessionEndpoint = $"https://login.microsoftonline.com/{TestIds.Tenant}/oauth2/v2.0/logout",
                TokenEndpoint = $"https://login.microsoftonline.com/{TestIds.Tenant}/oauth2/v2.0/token",
                JwksUri = $"https://login.microsoftonline.com/{TestIds.Tenant}/discovery/v2.0/keys",
            };
            metadata.SigningKeys.Add(TestTokens.SigningKey);
            IConfigurationManager<OpenIdConnectConfiguration> Static() => new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            // Only when sign-in is on do these schemes exist; PostConfigure on a scheme nobody registered is harmless.
            services.PostConfigure<OpenIdConnectOptions>(AuthSetup.OidcScheme, o => o.ConfigurationManager = Static());
            services.PostConfigure<JwtBearerOptions>(AuthSetup.BearerScheme, o => o.ConfigurationManager = Static());
            _services?.Invoke(services);
        });
    }

    public HttpClient Client(string host = "dashboard.example.test", bool followRedirects = false) =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, BaseAddress = new Uri($"http://{host}/") });

    /// <summary>A request as App Service's front end forwards it: plain http inside, X-Forwarded-Proto says https.</summary>
    public static HttpRequestMessage Request(HttpMethod method, string path, string? cookie = null, string? bearer = null, bool https = true)
    {
        var req = new HttpRequestMessage(method, path);
        if (https) req.Headers.Add("X-Forwarded-Proto", "https");
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
        return req;
    }

    /// <summary>A genuine sign-in cookie, protected with the host's own data-protection keys, exactly as the cookie handler would issue it.</summary>
    public string SignedInCookie(string name = "Ada Lovelace", string email = "ada@example.test")
    {
        var options = Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(AuthSetup.CookieScheme);
        var identity = new ClaimsIdentity(
            [new("name", name), new("preferred_username", email), new("tid", TestIds.Tenant)], AuthSetup.CookieScheme, "name", "roles");
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity),
            new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1), IssuedUtc = DateTimeOffset.UtcNow }, AuthSetup.CookieScheme);
        return $"{options.Cookie.Name}={options.TicketDataFormat.Protect(ticket)}";
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}
