using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CopilotUsage.Tests.Support;

/// <summary>Fixed identities used by the auth tests. All GUIDs are invented.</summary>
public static class TestIds
{
    public const string Tenant = "11111111-2222-3333-4444-555555555555";
    public const string OtherTenant = "99999999-8888-7777-6666-555555555555";
    public const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    public const string FunctionOid = "f0f0f0f0-1111-2222-3333-444444444444";
    public const string FunctionClientId = "c1c1c1c1-1111-2222-3333-444444444444";
    public const string SomeoneElseOid = "0e0e0e0e-1111-2222-3333-444444444444";
    public static string V2Issuer(string tenant = Tenant) => $"https://login.microsoftonline.com/{tenant}/v2.0";
}

/// <summary>
/// Tokens signed with a key that exists only in the tests. The app is told to trust that key through the test host (see AppFactory),
/// which swaps the source of the signing keys and nothing else; issuer, audience, lifetime and algorithm checks are the production ones.
/// </summary>
public static class TestTokens
{
    public static readonly RsaSecurityKey SigningKey = NewKey("test-signing-key");
    public static readonly RsaSecurityKey UntrustedKey = NewKey("test-signing-key"); // same key id, different key: a forged signature

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    /// <summary>An app-only token as the Function's managed identity would get it (v2 format). Tweak the spec to break one thing.</summary>
    public sealed class Spec
    {
        public string Issuer { get; set; } = TestIds.V2Issuer();
        public string? Audience { get; set; } = TestIds.ClientId;
        public SecurityKey Key { get; set; } = SigningKey;
        public DateTime NotBefore { get; set; } = DateTime.UtcNow.AddMinutes(-1);
        public DateTime Expires { get; set; } = DateTime.UtcNow.AddHours(1);
        public Dictionary<string, object> Claims { get; } = new()
        {
            ["oid"] = TestIds.FunctionOid,
            ["sub"] = TestIds.FunctionOid,
            ["tid"] = TestIds.Tenant,
            ["azp"] = TestIds.FunctionClientId,
            ["azpacr"] = "2",
            ["idtyp"] = "app",
            ["ver"] = "2.0",
            ["roles"] = new[] { "Sync.Run" },
        };
    }

    public static string Create(Action<Spec>? tweak = null)
    {
        var spec = new Spec();
        tweak?.Invoke(spec);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = spec.Issuer,
            Audience = spec.Audience,
            NotBefore = spec.NotBefore,
            Expires = spec.Expires,
            SigningCredentials = new SigningCredentials(spec.Key, SecurityAlgorithms.RsaSha256),
            Claims = spec.Claims,
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>A user ID token as Entra posts it back after sign-in.</summary>
    public static string IdToken(string nonce, string issuerTenant = TestIds.Tenant, string tidClaim = TestIds.Tenant, string name = "Ada Lovelace") =>
        Create(s =>
        {
            s.Issuer = TestIds.V2Issuer(issuerTenant);
            s.Claims.Clear();
            s.Claims["nonce"] = nonce;
            s.Claims["tid"] = tidClaim;
            s.Claims["oid"] = "77777777-1111-2222-3333-444444444444";
            s.Claims["sub"] = "pairwise-subject";
            s.Claims["name"] = name;
            s.Claims["preferred_username"] = "ada@example.test";
        });
}
