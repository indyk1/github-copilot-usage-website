# Copilot Usage Dashboard

A .NET 10 Blazor dashboard showing how a GitHub organization consumes its monthly Copilot AI-credit allowance
(Copilot Business: 1,900 credits per seat, pooled across the org, 1 credit = $0.01, reset 00:00 UTC on the 1st).
See [PLAN.md](PLAN.md) for the full spec. A background job syncs GitHub data into SQLite; pages read only from SQLite.

**Status: phase 4 (shippable).** Foundation (calculations, GitHub client, database, sync, demo data), the "glass and aurora"
theme, the home page, the Users table (`/users`), the per-user drill-down (`/users/{login}`), a health endpoint, production
configuration, security headers, Azure infrastructure as code (`infra/`), CI and a manual deploy workflow (`.github/workflows/`)
are in place. Nothing has been deployed to Azure or run against the real GitHub API yet: read
[docs/DEPLOY.md](docs/DEPLOY.md) (it ends with a first-run checklist) and [docs/API-ASSUMPTIONS.md](docs/API-ASSUMPTIONS.md).
The hosting design has changed (App Service Free plan, sync scheduled by an Azure Function, Microsoft Entra ID sign-in); this phase covers the
application code. `infra/`, `.github/workflows/` and `docs/DEPLOY.md` still describe the old design and are replaced in the next phase.
The dashboard now requires Entra sign-in (see [docs/AUTH.md](docs/AUTH.md)); only Development may switch it off.

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
export GitHub__Org=your-org
export GitHub__Token=...      # never commit this
export Demo__Enabled=false
dotnet run --project src/CopilotUsage.Web
```

Or use `dotnet user-secrets` for the token. The app refuses to start, with a message naming the missing key, if demo mode
is off and `GitHub:Org` or `GitHub:Token` is missing. The GitHub endpoints and field names are **unverified assumptions**:
read [docs/API-ASSUMPTIONS.md](docs/API-ASSUMPTIONS.md) before pointing this at a real organization.

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `Demo:Enabled` | `false` (`true` in Development) | Skip GitHub, seed demo data. Refused (startup and Refresh) if the database holds data from a real sync; see below |
| `Demo:Scenario` | `normal` | Demo dataset: `normal` (about half the pool), `over` (pool exhausted with overage), `empty` (seats, no usage yet) |
| `GitHub:Org` | none | Organization login (required unless demo) |
| `GitHub:Token` | none | Access token, configuration only (required unless demo) |
| `GitHub:BaseUrl` | `https://api.github.com/` | API base URL |
| `GitHub:ApiVersion` | `2026-03-10` | Sent as `X-GitHub-Api-Version` |
| `Copilot:CreditsPerSeat` | `1900` | Credits per seat per month |
| `Sync:IntervalHours` | `4` | How often a sync is expected (match the Function's schedule). `/healthz` reports "degraded" after three missed intervals. With `Sync:InAppTimer` it is also the timer period |
| `Sync:InAppTimer` | `false` | Run the in-process timer. Off everywhere by default: in production the Azure Function calls `POST /api/sync`. Switch on for local live runs |
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

Environment variables use `__` as the separator (`GitHub__Org`). `appsettings.Production.json` (committed, no secrets) sets the
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
- **Scheduler**: the Function in `src/CopilotUsage.SyncFunction` (timer, default every 4 hours). A run interrupted by the app stopping is
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
- The footer shows the version and commit (`Version 1.0.0 (abc1234)`); the deploy workflow passes the commit as
  `SourceRevisionId`.
- Logs never contain the token or an Authorization header (`LoggingSafetyTests` checks the real HttpClient pipeline at Trace level).

## Azure and CI

| Path | What |
|---|---|
| `infra/main.bicep` | Linux App Service plan and web app (.NET 10, one instance, Always On, HTTPS only, TLS 1.2, websockets), Key Vault with the token, managed identity access, App Settings, **required** IP access restrictions on the site and the SCM site |
| `infra/main.parameters.example.json` | Placeholder parameters; the token is passed on the command line, never in a file |
| `.github/workflows/ci.yml` | Restore, build with warnings as errors, test, on pull requests and pushes to `main` |
| `.github/workflows/deploy.yml` | Manual only; build, test, publish, deploy with OIDC (no stored secret); app name and resource group from repository variables |
| `docs/DEPLOY.md` | Step-by-step runbook, first-run checklist and operating notes |

## Build and test

```bash
dotnet build
dotnet test
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
infra                    Bicep template and example parameters
.github/workflows        ci.yml and deploy.yml
docs                     AUTH.md (Entra settings), DEPLOY.md (runbook, outdated), API-ASSUMPTIONS.md
tests/CopilotUsage.Tests xUnit and bUnit tests (fixtures are synthetic)
```
