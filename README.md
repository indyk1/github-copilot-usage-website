# Copilot Usage Dashboard

A .NET 10 Blazor dashboard showing how a GitHub **enterprise** (when the enterprise holds the Copilot billing) or a self-billed **organization**
consumes its monthly Copilot AI-credit allowance (Copilot Business: 1,900 credits per seat, pooled across the billing entity, 1 credit = $0.01,
reset 00:00 UTC on the 1st). Set `GitHub:Enterprise` for enterprise scope, `GitHub:Org` for organization scope (see Configuration).
See [PLAN.md](PLAN.md) for the full spec. A sync, started by an Azure Function on a timer, copies GitHub data into SQLite; pages read only from SQLite.

**Status: shippable, not yet deployed.** The application (calculations, GitHub client, database, sync, demo data, the "glass and aurora" theme,
the home page, `/users`, the per-user drill-down, `/healthz`, security headers), Microsoft Entra ID sign-in, the `POST /api/sync` endpoint, the Azure Function that
schedules the sync, the Terraform in `infra/` and the Azure DevOps pipelines in `azure-pipelines/` are all written and tested as far as is possible without
Azure. Nothing has been deployed to Azure or run against the real GitHub API yet: read [docs/DEPLOY.md](docs/DEPLOY.md) (a numbered runbook that ends with a
first-run checklist) and [docs/API-ASSUMPTIONS.md](docs/API-ASSUMPTIONS.md). Sign-in is required everywhere except Development
(see [docs/AUTH.md](docs/AUTH.md)).

## Architecture

```
 people ──Entra ID sign-in──▶ Web app: Blazor Server on App Service Free (F1, Linux, .NET 10)
                               │   pages read only from SQLite (/home/data), GitHub token from Key Vault
                               ▲
 Azure Function (Flex Consumption, timer every 4 h) ──POST /api/sync, token of its managed identity (role Sync.Run)
                               │
                               ▼  the web app, not the Function, calls GitHub and writes the database
```

- **Hosting**: Terraform ([infra/](infra)) builds a resource group's contents; Azure DevOps Pipelines ([azure-pipelines/](azure-pipelines)) run CI, Terraform
  (plan, then an approved apply) and the deployment of both apps. The code stays in this GitHub repository.
- **Protection**: Microsoft Entra ID sign-in. There are no IP restrictions; network-level control is the IT admin's Entra Conditional Access policy with
  Global Secure Access (runbook step 7).
- **Scheduling**: the Function. The Free plan puts the web app to sleep when idle, so the app does not schedule its own syncs.

**Cost target: zero.** F1 web app, Function on Flex Consumption inside its free grant, SQLite on the app's disk (no Azure SQL), logging inside the free
allowance with a daily cap, Key Vault at cents. Details, caveats and what would start to charge: [the cost table in docs/DEPLOY.md](docs/DEPLOY.md#expected-monthly-cost).

## Pages

| Page | What it shows |
|---|---|
| `/` | Credit pool gauge, stat tiles, burn chart, model split, leaderboard |
| `/users` | Every seat: credits, percent of fair share, prompts, acceptance rate, top model, last active. Search by login as you type; click a column header (or use the sort menu on a phone) to sort, click again to reverse. Default: credits, highest first. Seats with no usage are marked "No usage this month", never hidden. A starting view can be set in the address: `/users?q=chen&sort=prompts&dir=asc` (`sort` is one of `user`, `credits`, `fair`, `prompts`, `acceptance`, `model`, `active`) |
| `/signed-out`, `/access-denied` | Themed pages shown after sign-out and when a sign-in is refused (anonymous) |
| `POST /api/sync` | Runs a sync; called only by the Azure Function (see below). Not a page |
| `/healthz` | JSON for probes: database reachable, time and status of the last sync, demo flag, version. No secrets. Not affected by the unknown-user check or the https redirect |
| `/users/{login}` | Credits and cost (billed, against the fair share, with a daily estimate chart), model breakdown, feature and tool mix (activity counts, not cost), productivity signals. An unknown login gets a not-found page and an HTTP 404 |

Billed figures (monthly total, credits per model) come from GitHub's billing data. The daily chart on a user page comes from
the usage metrics signal and is labelled as an estimate because it will not add up exactly to the billed total.

## Run it in demo mode (no GitHub needed)

```bash
dotnet run --project src/CopilotUsage.Web
```

Then open http://localhost:5103. In Development, `Auth:Enabled` is false (no sign-in, no tenant needed; the header shows "No sign-in") and
`Demo:Enabled` is true (see `appsettings.Development.json`), so GitHub is
never contacted and the SQLite database (`src/CopilotUsage.Web/data/copilot-usage.db`, git-ignored) is migrated and filled
with deterministic, synthetic data for the current and previous month: 25 users (four over their 1,900 fair share, some
light, two inactive), six models, and activity by feature, IDE, client and language.

## Run it against GitHub

```bash
export GitHub__Enterprise=your-enterprise   # enterprise scope: the enterprise holds the Copilot billing
# export GitHub__Org=your-org               # organization scope instead, for a self-billed organization
export GitHub__Token=...      # never commit this
export Demo__Enabled=false
dotnet run --project src/CopilotUsage.Web
```

Or use `dotnet user-secrets` for the token. The app refuses to start, with a message naming the missing key, if demo mode
is off and neither `GitHub:Enterprise` nor `GitHub:Org`, or `GitHub:Token`, is set. The GitHub endpoints and field names are **unverified assumptions**
(in enterprise scope only the seats endpoint is documented): read [docs/API-ASSUMPTIONS.md](docs/API-ASSUMPTIONS.md) before pointing this at real data, and run
`scripts/check-github-token.sh` first ([docs/DEPLOY.md](docs/DEPLOY.md), section 1.1). A 403 "Resource not accessible by personal access token" from the
organization endpoints usually means the organization is billed through an enterprise: set `GitHub:Enterprise`.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Demo:Enabled` | `false` (`true` in Development) | Skip GitHub, seed demo data. Refused (startup and Refresh) if the database holds data from a real sync; see below |
| `Demo:Scenario` | `normal` | Demo dataset: `normal` (about half the pool), `over` (pool exhausted with overage), `empty` (seats, no usage yet) |
| `GitHub:Enterprise` | none | Enterprise slug. Switches the app to **enterprise scope**: every call uses the enterprise endpoints and the pool is all enterprise seats x credits per seat, counting each user once. Needs a classic token from an enterprise owner. Required unless demo, or `GitHub:Org` is set |
| `GitHub:Org` | none | Organization login (organization scope, for a self-billed organization). **If `GitHub:Enterprise` is also set the enterprise wins and the organization is ignored**, with one log line at startup. Required unless demo, or `GitHub:Enterprise` is set |
| `GitHub:Token` | none | Access token, configuration only (required unless demo) |
| `GitHub:BaseUrl` | `https://api.github.com/` | API base URL |
| `GitHub:ApiVersion` | `2026-03-10` | Sent as `X-GitHub-Api-Version` |
| `Copilot:CreditsPerSeat` | `1900` | Credits per seat per month |
| `Sync:IntervalHours` | `4` | How often a sync is expected (Terraform sets it and the Function's schedule from one variable). `/healthz` reports "degraded" after three missed intervals. With `Sync:InAppTimer` it is also the timer period |
| `Sync:InAppTimer` | `false` | Run the in-process timer. For **local live runs only**: in Azure the app is never scheduled this way (the Function calls `POST /api/sync`) and the setting is not set |
| `Sync:RunOnStartup` | `true` | With `Sync:InAppTimer` only: sync at startup (live mode) |
| `Sync:RequestWaitSeconds` | `200` | How long `POST /api/sync` waits for the sync before answering 202 and letting it finish in the background (1 to 220) |
| `Sync:AllowedCallerObjectId` | none | Object id of the Function's managed identity, the only caller of `/api/sync`. **Required** when sign-in is on (always, outside Development) |
| `Sync:AllowedCallerClientId` | none | Optional extra pin: the identity's client id |
| `AzureAd:TenantId` | none | Entra directory (tenant) id, a GUID. **Required** outside Development |
| `AzureAd:ClientId` | none | App registration client id. **Required** outside Development |
| `AzureAd:AppIdUri` | `api://{ClientId}` | Application ID URI, accepted as audience of the sync token |
| `AzureAd:PublicBaseUrl` | none | Pin the sign-in redirect origin (`https://dashboard.contoso.com`) instead of using the request host |
| `AzureAd:Instance` | `https://login.microsoftonline.com/` | Login host |
| `DataProtection:KeysPath` | `data/dataprotection-keys` (`/home/data/dataprotection-keys` in Production) | Where the data-protection keys live, so sessions survive restarts. Created at startup; the app refuses to start if it is not writable. **Sensitive**: whoever can read these files can forge a session cookie |
| `Auth:Enabled` | `true` (`false` in Development) | Development only. In any other environment `false` stops the app from starting |
| `ConnectionStrings:Default` | `Data Source=data/copilot-usage.db` (`/home/data/copilot-usage.db` in Production) | SQLite file; a relative path is resolved against the web project folder |
| `Security:ContentSecurityPolicy` | built-in policy | Replace the Content-Security-Policy; empty turns it off. `{nonce}` is replaced per request |

Environment variables use `__` as the separator (`GitHub__Enterprise`, `GitHub__Org`). `appsettings.Production.json` (committed, no secrets) sets the
App Service database path, demo off and quieter log levels (HttpClient at Warning). The token is only ever read from
configuration and the app refuses to start with an unresolved `@Microsoft.KeyVault(...)` text in its place.

## Demo data and real data never mix

Demo seeding replaces whatever usage data it finds, so the app checks first, using the sync log stored in the database
(each run records whether it was demo), never file names:

- Demo on and the database holds a real sync (or data with no sync log): the app **refuses to start**, names the problem and
  the fix (`Demo__Enabled=false`, or a separate `ConnectionStrings__Default` file). The Refresh button is refused too.
  Nothing is deleted.
- Demo on and the database is empty or demo-only: seeded as usual.
- Demo off and the database holds only demo data: the demo rows and demo sync runs are cleared at startup (logged), so
  synthetic users never appear beside real ones.

## Sign-in and the sync endpoint

- **Dashboard sign-in**: the plain ASP.NET Core OpenID Connect handler, single tenant, ID-token-only flow. No client secret or certificate is
  stored or needed. Every page and the Blazor circuit require a signed-in user; there are no roles. `/healthz`, static assets and the
  signed-out/access-denied pages are anonymous. The auth cookie is HttpOnly, Secure and SameSite=Lax.
- **`POST /api/sync`**: callable only with an Entra access token issued for this app to the Function's managed identity: signature,
  issuer (this tenant), audience, lifetime, the `Sync.Run` app role, an app-only token, and `oid` equal to `Sync:AllowedCallerObjectId`.
  Cookie users and delegated user tokens cannot call it; bearer tokens do not open pages. Refusals are a bare 401/403, the reason is logged.
  Answers: 200 with a small JSON summary, 202 if the sync outlasts the request budget (it carries on in the app), 409 if one is already running.
- **Scheduler**: the Function in `src/CopilotUsage.SyncFunction` (timer, every 4 hours by default). A run interrupted by the app stopping is
  closed as failed at the next start and shows in `/healthz`.
- Everything the Entra side needs is in [docs/AUTH.md](docs/AUTH.md).

## The Azure Function

`src/CopilotUsage.SyncFunction`: Functions v4, .NET 10 isolated worker, one timer function (`SyncSchedule` app setting, NCRONTAB). It gets a
token from its managed identity (`SyncApi__Scope`), POSTs to `SyncApi__BaseUrl` + `api/sync`, retries with backoff while the web app wakes,
treats "already running" as success and reports 401/403 as a configuration error. Settings are in docs/AUTH.md; copy
`local.settings.example.json` to `local.settings.json` (git-ignored) to run it locally. It holds no GitHub token and never touches the database.

## Production behaviour

- Behind App Service's reverse proxy the app trusts `X-Forwarded-For` / `X-Forwarded-Proto`, redirects http to https and sends
  HSTS (not in Development, so local http works). `/healthz` is exempt from the redirect.
- Every response carries `X-Content-Type-Options`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a
  `Permissions-Policy` and a Content-Security-Policy (same-origin scripts plus one per-request nonce for the framework's import
  map, same-origin websocket, GitHub avatar images, `frame-ancestors 'none'`). Inline event handlers are not allowed, so avatar
  fallbacks live in `wwwroot/js/glass.js`.
- The footer shows the version and commit (`Version 1.0.0 (abc1234)`); the deploy pipeline passes the commit as
  `SourceRevisionId`.
- Logs never contain the token or an Authorization header (`LoggingSafetyTests` checks the real HttpClient pipeline at Trace level).

## Azure and pipelines

| Path | What |
|---|---|
| `infra/` | Terraform (azurerm, one root module): Linux App Service plan **F1** and the .NET 10 web app, Flex Consumption plan and Function with its storage and a user-assigned identity, Key Vault (standard, RBAC) for the GitHub token, optional Log Analytics and Application Insights with a daily cap. App Settings for both apps, outputs for the manual steps. State in a hand-made storage account (Entra auth). No Entra objects, no secrets. `terraform test` (offline) checks the setting keys |
| `infra/terraform.tfvars.example` | Placeholder values only. Real values live in an Azure DevOps variable group |
| `azure-pipelines/ci.yml` | Pull requests and `main`: restore, build with warnings as errors, test, publish results; Terraform format and validate |
| `azure-pipelines/infrastructure.yml` | Manual (and on `infra/` changes on `main`): fmt, init, validate, plan; then an Apply stage gated by an Azure DevOps environment approval that applies exactly the saved plan |
| `azure-pipelines/deploy.yml` | Manual, on `main`: build, test, publish and deploy the web app and the Function, smoke-check `/healthz` for the deployed commit |
| `docs/DEPLOY.md` | Numbered runbook (manual and pipeline steps), cost table, first-run checklist, operating notes, Conditional Access and Global Secure Access |
| `docs/AUTH.md` | What the app expects from Microsoft Entra ID and every configuration key |

Pipelines authenticate to Azure with a workload-identity-federation service connection (no secret) and use only built-in tasks and scripts.
The GitHub token is set by hand in Key Vault and is never in the repository, Terraform, the state or a pipeline variable.

## Build and test

```bash
dotnet build
dotnet test
terraform -chdir=infra init -backend=false && terraform -chdir=infra validate && terraform -chdir=infra test   # optional: the infrastructure checks, offline
```

## Layout

```
src/CopilotUsage.Core    domain models, pool / fair-share / leaderboard / burn / reset calculations
src/CopilotUsage.GitHub  typed HttpClient, DTOs, NDJSON reader, endpoint paths and mappings
src/CopilotUsage.Data    EF Core SQLite context, migrations, idempotent writer, read queries, demo data
src/CopilotUsage.Web     Blazor Server host: pages (Home, Users, UserDetail, SignedOut, AccessDenied, NotFound, Error), UI components,
                         config validation, Entra sign-in and sync-endpoint authorization (Auth/), sync service, endpoint and optional
                         worker, health endpoint, security headers
src/CopilotUsage.SyncFunction  Azure Function (timer) that calls POST /api/sync
infra                    Terraform (root module), example variables, offline tests
azure-pipelines          ci.yml, infrastructure.yml, deploy.yml and their templates and scripts
docs                     AUTH.md (Entra settings), DEPLOY.md (runbook), API-ASSUMPTIONS.md
tests/CopilotUsage.Tests xUnit and bUnit tests (fixtures are synthetic)
```
