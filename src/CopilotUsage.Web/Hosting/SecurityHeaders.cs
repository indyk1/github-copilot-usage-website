namespace CopilotUsage.Web.Hosting;

/// <summary>Security response headers for an internal dashboard. Set in OnStarting so error pages and static files get them too.</summary>
public static class SecurityHeaders
{
    public const string SectionKey = "Security:ContentSecurityPolicy";

    /// <summary>Replaced with a fresh random value on every request; the page puts the same value on its one inline script (the import map).</summary>
    public const string NoncePlaceholder = "{nonce}";

    private const string NonceKey = "csp-nonce";

    /// <summary>The nonce for this request's inline import-map script.</summary>
    public static string NonceFor(HttpContext ctx) =>
        ctx.Items.TryGetValue(NonceKey, out var n) && n is string s ? s : "";

    /// <summary>
    /// Everything comes from this origin, except avatar images (GitHub avatar hosts; data: for inline placeholders).
    /// Inline styles are allowed because the components set CSS custom properties with style attributes. Inline scripts are
    /// not, apart from the framework's import map, which carries a per-request nonce.
    /// Blazor Server's websocket is same-origin, covered by connect-src 'self'.
    /// </summary>
    public const string DefaultCsp =
        "default-src 'self'; " +
        "script-src 'self' 'nonce-{nonce}'; " +
        "style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data: https://avatars.githubusercontent.com https://github.com; " +
        "font-src 'self'; " +
        "connect-src 'self'; " +
        "object-src 'none'; " +
        "base-uri 'self'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    /// <summary>The policy to send: the built-in default when <c>Security:ContentSecurityPolicy</c> is unset, a custom one when set, or null (no header) when set to an empty string.</summary>
    public static string? ResolveCsp(IConfiguration config)
    {
        var configured = config[SectionKey];
        return configured is null ? DefaultCsp : configured.Trim().Length == 0 ? null : configured.Trim();
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, string? csp)
    {
        return app.Use((ctx, next) =>
        {
            // base64url, so the value needs no HTML encoding in the attribute
            var nonce = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            ctx.Items[NonceKey] = nonce;
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                h["X-Content-Type-Options"] = "nosniff";
                h["X-Frame-Options"] = "DENY";
                h["Referrer-Policy"] = "no-referrer";
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                if (csp is not null) h["Content-Security-Policy"] = csp.Replace(NoncePlaceholder, nonce, StringComparison.Ordinal);
                return Task.CompletedTask;
            });
            return next();
        });
    }
}
