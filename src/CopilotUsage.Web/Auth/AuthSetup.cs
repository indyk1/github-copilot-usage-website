using System.Security.Claims;
using CopilotUsage.Web.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace CopilotUsage.Web.Auth;

/// <summary>
/// Two independent ways in, kept apart on purpose:
/// <list type="bullet">
/// <item>People: the cookie scheme (default) filled by an OpenID Connect sign-in, tenant-pinned. Guards every page and the Blazor circuit
/// through the fallback authorization policy.</item>
/// <item>The Azure Function: a bearer scheme used only by the policy on POST /api/sync. A cookie user has no bearer token, and a bearer
/// token is never looked at for a page, so neither can stand in for the other.</item>
/// </list>
/// </summary>
public static class AuthSetup
{
    public const string CookieScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string OidcScheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string BearerScheme = "SyncBearer";
    public const string SyncPolicy = "SyncCaller";
    public const string SyncRole = "Sync.Run";
    public const string AccessDeniedPath = "/access-denied";
    public const string SignedOutPath = "/signed-out";
    public const string SignOutPath = "/auth/signout";

    public static IServiceCollection AddDashboardAuth(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var enabled = AuthMode.Resolve(config, env.IsDevelopment());
        services.AddSingleton(new AuthMode(enabled));
        services.AddCascadingAuthenticationState();
        if (!enabled)
        {
            // Development only (the validator refuses this elsewhere). No schemes, no fallback policy: everything is open.
            services.AddAuthentication();
            services.AddAuthorization();
            return services;
        }

        var ad = config.GetSection(AzureAdOptions.SectionName).Get<AzureAdOptions>()!;
        var tenant = ad.TenantId!.Trim();
        var clientId = ad.ClientId!.Trim();
        var instance = ad.Instance.TrimEnd('/');
        var authority = $"{instance}/{tenant}/v2.0";
        var secureCookies = env.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        var publicBase = string.IsNullOrWhiteSpace(ad.PublicBaseUrl) ? null : ad.PublicBaseUrl.Trim().TrimEnd('/');

        services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieScheme;
                o.DefaultChallengeScheme = OidcScheme;
            })
            .AddCookie(CookieScheme, o =>
            {
                o.Cookie.Name = ".CopilotUsage.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = secureCookies;
                // Lax: sent on normal navigation, never on a cross-site POST (which also makes the sign-out form safe from CSRF).
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                o.AccessDeniedPath = AccessDeniedPath;
            })
            .AddOpenIdConnect(OidcScheme, o =>
            {
                o.Authority = authority;               // tenant-specific, never /common
                o.ClientId = clientId;
                // ID-token-only flow ("implicit" for sign-in): Entra posts a signed ID token straight back, so there is no code
                // to redeem and therefore no client secret or certificate. Nothing calls an API on the user's behalf.
                o.ResponseType = OpenIdConnectResponseType.IdToken;
                o.ResponseMode = OpenIdConnectResponseMode.FormPost;
                o.UsePkce = false;
                o.SaveTokens = false;
                o.GetClaimsFromUserInfoEndpoint = false;
                o.MapInboundClaims = false;
                o.SignInScheme = CookieScheme;
                o.CallbackPath = "/signin-oidc";
                o.SignedOutCallbackPath = "/signout-callback-oidc";
                o.SignedOutRedirectUri = SignedOutPath;
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,             // issuer comes from the tenant's metadata, so only this tenant passes
                    ValidateAudience = true,
                    ValidAudience = clientId,
                    ValidateLifetime = true,
                    NameClaimType = "name",
                };
                // Entra posts back cross-site, so these must be SameSite=None; the handler's defaults already say so. Make them Secure.
                o.CorrelationCookie.SecurePolicy = secureCookies;
                o.NonceCookie.SecurePolicy = secureCookies;
                o.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = ctx =>
                    {
                        if (ctx.Request.Path.StartsWithSegments("/api"))
                        {
                            // A script calling the API gets a plain 401, never a redirect to a login page.
                            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            ctx.HandleResponse();
                            return Task.CompletedTask;
                        }
                        if (publicBase is not null)
                        {
                            if (ctx.ProtocolMessage.RedirectUri is not null) ctx.ProtocolMessage.RedirectUri = publicBase + o.CallbackPath;
                            if (ctx.ProtocolMessage.PostLogoutRedirectUri is not null) ctx.ProtocolMessage.PostLogoutRedirectUri = publicBase + o.SignedOutCallbackPath;
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = ctx =>
                    {
                        // Belt and braces next to issuer validation: the tenant id inside the token must be ours.
                        var tid = ctx.Principal?.FindFirstValue("tid");
                        if (!string.Equals(tid, tenant, StringComparison.OrdinalIgnoreCase))
                        {
                            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotUsage.Auth")
                                .LogWarning("Sign-in refused: the ID token is for a different tenant.");
                            ctx.Fail("tenant mismatch");
                        }
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = ctx =>
                    {
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotUsage.Auth")
                            .LogWarning("Sign-in failed: {Reason}", ctx.Failure?.Message ?? "unknown");
                        ctx.Response.Redirect(AccessDeniedPath);
                        ctx.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            })
            .AddJwtBearer(BearerScheme, o =>
            {
                o.Authority = authority;               // signing keys come from the tenant's published keys (jwks_uri)
                o.RequireHttpsMetadata = true;
                o.SaveToken = false;
                o.MapInboundClaims = false;
                var appIdUri = string.IsNullOrWhiteSpace(ad.AppIdUri) ? $"api://{clientId}" : ad.AppIdUri.Trim();
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = true,
                    // v2 tokens and v1 tokens (the default for an app registration that has not set accessTokenAcceptedVersion=2); same tenant only.
                    ValidIssuers = [$"{instance}/{tenant}/v2.0", $"https://sts.windows.net/{tenant}/"],
                    ValidateAudience = true,
                    ValidAudiences = [clientId, appIdUri],
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromMinutes(2),
                    RoleClaimType = "roles",
                    NameClaimType = "oid",
                };
                o.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        // The exception type and message name the check that failed; they go to the log only, never to the caller.
                        ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotUsage.Auth")
                            .LogWarning("Sync token rejected: {Reason}", ctx.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                    OnChallenge = ctx =>
                    {
                        // Bare 401: the default header would say which validation failed.
                        ctx.HandleResponse();
                        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        ctx.Response.Headers.WWWAuthenticate = "Bearer";
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization(o =>
        {
            // Every endpoint (pages, the Blazor circuit, anything new) needs a signed-in person unless it says AllowAnonymous.
            // No scheme list: authenticate with the default (cookie), challenge with the default (OpenID Connect).
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy(SyncPolicy, p => p
                .AddAuthenticationSchemes(BearerScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new SyncCallerRequirement()));
        });
        services.AddSingleton<IAuthorizationHandler, SyncCallerHandler>();
        return services;
    }
}

public sealed class SyncCallerRequirement : IAuthorizationRequirement;

/// <summary>
/// Pins POST /api/sync to the Azure Function's managed identity. The token is already validated (signature, issuer, audience, lifetime).
/// Here: it must be an app-only token (no delegated scope, subject equals object id), carry the Sync.Run application role, and come
/// from exactly the configured object id (and client id, when configured) in the configured tenant. The reason for a refusal is
/// logged, never returned.
/// </summary>
public sealed class SyncCallerHandler(IOptions<SyncOptions> sync, IOptions<AzureAdOptions> ad, ILogger<SyncCallerHandler> logger)
    : AuthorizationHandler<SyncCallerRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SyncCallerRequirement requirement)
    {
        var reason = Check(context.User);
        if (reason is null) context.Succeed(requirement);
        else logger.LogWarning("Sync call refused: {Reason}", reason);
        return Task.CompletedTask;
    }

    private string? Check(ClaimsPrincipal user)
    {
        var expectedOid = sync.Value.AllowedCallerObjectId?.Trim();
        if (string.IsNullOrEmpty(expectedOid)) return "no allowed caller is configured";

        if (user.FindFirstValue("scp") is not null || user.FindFirstValue("http://schemas.microsoft.com/identity/claims/scope") is not null)
            return "token is a delegated (user) token";
        var idtyp = user.FindFirstValue("idtyp");
        if (idtyp is not null && !idtyp.Equals("app", StringComparison.OrdinalIgnoreCase)) return "token is not an app-only token";
        var oid = user.FindFirstValue("oid");
        if (oid is null) return "token has no oid";
        var sub = user.FindFirstValue("sub");
        if (sub is not null && !sub.Equals(oid, StringComparison.OrdinalIgnoreCase)) return "subject differs from object id (not a service principal token)";
        if (!user.FindAll("roles").Any(c => c.Value == AuthSetup.SyncRole)) return "token lacks the " + AuthSetup.SyncRole + " role";
        if (!oid.Equals(expectedOid, StringComparison.OrdinalIgnoreCase)) return "caller object id is not the allowed one";

        var expectedClient = sync.Value.AllowedCallerClientId?.Trim();
        if (!string.IsNullOrEmpty(expectedClient))
        {
            var client = user.FindFirstValue("azp") ?? user.FindFirstValue("appid");
            if (client is null || !client.Equals(expectedClient, StringComparison.OrdinalIgnoreCase)) return "caller client id is not the allowed one";
        }
        var tid = user.FindFirstValue("tid");
        if (tid is null || !tid.Equals(ad.Value.TenantId?.Trim(), StringComparison.OrdinalIgnoreCase)) return "token tenant is not the configured one";
        return null;
    }
}
