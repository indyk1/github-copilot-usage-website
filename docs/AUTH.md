# Microsoft Entra ID: what the app expects

This is the contract between the code and the Entra tenant. The app registration is created by hand by the tenant's IT admin; nothing
here is created by the application. Replace `<tenant-id>`, `<client-id>` and so on with the real values.

## How it fits together

- People sign in to the dashboard with OpenID Connect. The app asks Entra for a signed **ID token only** (response type `id_token`,
  form post). It never redeems a code and calls no Microsoft API, so **no client secret, certificate or federated credential is needed.**
- An Azure Function calls `POST /api/sync` with an **access token** that its managed identity obtains for this same app registration.
  The app validates the token and checks the `Sync.Run` app role and the caller's object id.
- Both use one app registration (the "web app registration"). Single tenant.

## 1. The app registration (web app)

| Setting | Value |
|---|---|
| Supported account types | **Accounts in this organizational directory only (single tenant)** |
| Platform | **Web** |
| Redirect URI | `https://<public-host>/signin-oidc` (one per host the site is reached on; must be https) |
| Redirect URI (sign-out) | `https://<public-host>/signout-callback-oidc` (add it as a second Web redirect URI) |
| Front-channel logout URL | leave empty (the site sends `frame-ancestors 'none'`, so Entra's logout iframe could not load) |
| Implicit grant and hybrid flows | tick **ID tokens (used for implicit and hybrid flows)**. Leave **Access tokens** unticked |
| Certificates and secrets | **none** |
| Application ID URI (Expose an API) | `api://<client-id>` (the default Entra proposes). Any other URI works if you also set `AzureAd:AppIdUri` |
| Token version (manifest) | set `requestedAccessTokenVersion` (shown as `accessTokenAcceptedVersion` in the older manifest) to `2`. The app also accepts version 1 tokens from the same tenant, so this is a preference, not a requirement |
| Optional claims | none needed. The app reads `name`, `preferred_username` and `tid` from the ID token, which Entra includes by default with the `openid profile` scope |

`<public-host>` is the host the browser uses (the `*.azurewebsites.net` name or a custom domain). The app builds the redirect URI from the
request's host over https. To pin it regardless of the host header, set `AzureAd:PublicBaseUrl` (for example `https://dashboard.contoso.com`).

Everyone in the tenant may sign in. There are no roles for people. To restrict it further, set **Assignment required** on the enterprise
application (that is an Entra setting; the app does not look at it). Guests of the tenant have the tenant's `tid` and are let in unless you do.

### The `Sync.Run` app role

In the app registration, **App roles**, create:

| Field | Value |
|---|---|
| Display name | `Run sync` |
| Allowed member types | **Applications** (not Users/Groups) |
| Value | `Sync.Run` (exact, case sensitive) |
| Description | `Allows the sync Azure Function to run the Copilot usage sync.` |
| Do you want to enable this app role? | yes |

Manifest form:

```json
{
  "allowedMemberTypes": [ "Application" ],
  "displayName": "Run sync",
  "description": "Allows the sync Azure Function to run the Copilot usage sync.",
  "isEnabled": true,
  "value": "Sync.Run",
  "id": "<new GUID>"
}
```

## 2. Granting the role to the Function's managed identity

The Function app must have a managed identity (system-assigned is simplest). Its **object (principal) ID** is what the web app pins to.
The role is assigned to that identity on the web app registration's **enterprise application** (service principal), not on the
registration itself. Portal: Enterprise applications, the web app, Users and groups, cannot assign an app role to a managed identity,
so use PowerShell or the CLI (a Cloud Application Administrator or Application Administrator role, plus consent rights, is required):

```powershell
Connect-MgGraph -Scopes "AppRoleAssignment.ReadWrite.All","Application.Read.All"
$mi   = Get-MgServicePrincipal -ServicePrincipalId "<function-managed-identity-object-id>"
$api  = Get-MgServicePrincipal -Filter "appId eq '<client-id>'"          # the web app's enterprise application
$role = $api.AppRoles | Where-Object Value -eq "Sync.Run"
New-MgServicePrincipalAppRoleAssignment -ServicePrincipalId $mi.Id `
    -PrincipalId $mi.Id -ResourceId $api.Id -AppRoleId $role.Id
```

Assign it to nothing and nobody else. (Azure CLI equivalent: `az rest --method POST --uri https://graph.microsoft.com/v1.0/servicePrincipals/<mi-object-id>/appRoleAssignments --body '{"principalId":"<mi-object-id>","resourceId":"<api-service-principal-object-id>","appRoleId":"<role-id>"}'`.)

## 3. Configuration: web app

App Settings (environment variables use `__` for `:`). None of these is a secret.

| Key | Value | Required |
|---|---|---|
| `AzureAd__TenantId` | Directory (tenant) ID, a GUID. Never `common`, `organizations` or `consumers` | yes |
| `AzureAd__ClientId` | Application (client) ID of the web app registration | yes |
| `AzureAd__AppIdUri` | Application ID URI if it is not `api://<client-id>` | no |
| `AzureAd__PublicBaseUrl` | `https://<public-host>`, to pin redirect URIs | no |
| `AzureAd__Instance` | `https://login.microsoftonline.com/` (default; change only for a sovereign cloud) | no |
| `Sync__AllowedCallerObjectId` | Object (principal) ID of the Function's managed identity | yes |
| `Sync__AllowedCallerClientId` | The managed identity's client (application) ID, as an extra pin | no |
| `Sync__IntervalHours` | The Function's period in hours (default 4); `/healthz` calls a sync stale after three of these | no |

The app **refuses to start** in any environment other than Development if the three required keys are missing or malformed, naming each.
`Auth__Enabled=false` is honoured only in Development; elsewhere it also stops the app from starting.

Accepted audiences for the Function's token: the client id and the App ID URI. Accepted issuers: the tenant's v2 issuer
(`https://login.microsoftonline.com/<tenant-id>/v2.0`) and v1 issuer (`https://sts.windows.net/<tenant-id>/`). Signing keys are fetched
from the tenant's published keys. The token must also be app-only (no `scp` claim, `sub` equal to `oid`, `idtyp` `app` when present),
carry `roles` containing `Sync.Run`, have `tid` equal to the tenant, `oid` equal to `Sync__AllowedCallerObjectId`, and (if configured)
`azp`/`appid` equal to `Sync__AllowedCallerClientId`. Anything else is a 401 (token invalid) or 403 (token valid but not allowed), with no
explanation in the response; the reason is in the web app's log.

## 4. Configuration: Azure Function

App Settings on the Function app:

| Key | Value |
|---|---|
| `SyncSchedule` | NCRONTAB, six fields. `0 0 */4 * * *` is every 4 hours. **Required**: Functions cannot default a binding setting, so the app does not start the timer without it |
| `SyncApi__BaseUrl` | `https://<public-host>/` (the web app) |
| `SyncApi__Scope` | `api://<client-id>/.default` (the App ID URI plus `/.default`) |
| `SyncApi__ManagedIdentityClientId` | only for a user-assigned identity: its client ID. Empty for system-assigned |
| `SyncApi__TimeoutSeconds` | per attempt, default 230 |
| `SyncApi__MaxAttempts` | default 3 (first try plus 2 retries) |
| `SyncApi__InitialBackoffSeconds` | default 15, doubling |
| `SyncApi__TotalBudgetSeconds` | default 540, no retry that cannot finish inside this |
| `AzureWebJobsStorage`, `FUNCTIONS_WORKER_RUNTIME=dotnet-isolated` | the usual Functions requirements |

The Function holds no GitHub token and no secret of any kind.

## 5. Behaviour to expect

- `POST /api/sync`: 200 (finished; body says Succeeded, PartialFailure or Failed), 202 (still running after `Sync__RequestWaitSeconds`,
  default 200 s, and carrying on), 409 (one already running, nothing started), 401/403 (see above). Any other method: 405.
- A sleeping web app wakes on the first request; the Function retries on 5xx and timeouts. A 401/403 is reported as a configuration error
  and not retried.
- Sign-out sends the browser to Entra's logout endpoint and back to `/signout-callback-oidc`, then `/signed-out`.
