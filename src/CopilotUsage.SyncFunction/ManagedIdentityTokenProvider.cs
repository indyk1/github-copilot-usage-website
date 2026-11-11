using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Options;

namespace CopilotUsage.SyncFunction;

/// <summary>
/// Token for the web app from the Function app's managed identity (no secret anywhere). The credential caches and refreshes tokens itself.
/// Only ManagedIdentityCredential is used, not DefaultAzureCredential: a developer's own login would produce a delegated token,
/// which the web app refuses by design.
/// </summary>
public sealed class ManagedIdentityTokenProvider : ISyncTokenProvider
{
    private readonly string? _scope;
    private readonly TokenCredential _credential;

    public ManagedIdentityTokenProvider(IOptions<SyncApiOptions> options)
    {
        _scope = options.Value.Scope;
        var clientId = options.Value.ManagedIdentityClientId;
        _credential = string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId.Trim()));
    }

    public async Task<string?> GetTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_scope)) return null;
        var token = await _credential.GetTokenAsync(new TokenRequestContext([_scope.Trim()]), ct);
        return token.Token;
    }
}
