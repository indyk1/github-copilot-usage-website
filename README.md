# Copilot Usage Dashboard

A .NET 10 Blazor dashboard showing how a GitHub organization consumes its monthly Copilot AI-credit allowance
(Copilot Business: 1,900 credits per seat, pooled across the org, 1 credit = $0.01, reset 00:00 UTC on the 1st).
See [PLAN.md](PLAN.md) for the full spec. A background job syncs GitHub data into SQLite; pages read only from SQLite.

**Status: phase 4 (shippable).** Foundation (calculations, GitHub client, database, sync, demo data), the "glass and aurora"
theme, the home page, the Users table (`/users`), the per-user drill-down (`/users/{login}`), a health endpoint, production
configuration, security headers, Azure infrastructure as code (`infra/`), CI and a manual deploy workflow (`.github/workflows/`)
are in place. Nothing has been deployed to Azure or run against the real GitHub API yet: read
[docs/DEPLOY.md](docs/DEPLOY.md) (it ends with a first-run checklist) and [docs/API-ASSUMPTIONS.md](docs/API-ASSUMPTIONS.md).
The app has **no login**; in production, access restrictions on the App Service are the only protection.

## Pages

| Page | What it shows |
|---|---|
| `/` | Credit pool gauge, stat tiles, burn chart, model split, leaderboard |
| `/users` | Every seat: credits, percent of fair share, prompts, acceptance rate, top model, last active. Search by login as you type; click a column header (or use the sort menu on a phone) to sort, click again to reverse. Default: credits, highest first. Seats with no usage are marked "No usage this month", never hidden. A starting view can be set in the address: `/users?q=chen&sort=prompts&dir=asc` (`sort` is one of `user`, `credits`, `fair`, `prompts`, `acceptance`, `model`, `active`) |
| `/healthz` | JSON for probes: database reachable, time and status of the last sync, demo flag, version. No secrets. Not affected by the unknown-user check or the https redirect |
| `/users/{login}` | Credits and cost (billed, against the fair share, with a daily estimate chart), model breakdown, feature and tool mix (activity counts, not cost), productivity signals. An unknown login gets a not-found page and an HTTP 404 |

Billed figures (monthly total, credits per model) come from GitHub's billing data. The daily chart on a user page comes from
the usage metrics signal and is labelled as an estimate because it will not add up exactly to the billed total.

## Run it in demo mode (no GitHub needed)

```bash
dotnet run --project src/CopilotUsage.Web
```

Then open http://localhost:5103. In Development, `Demo:Enabled` is true (see `appsettings.Development.json`), so GitHub is
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
| `Demo:Enabled` | `false` (`true` in Development) | Skip GitHub, seed demo data |
| `Demo:Scenario` | `normal` | Demo dataset: `normal` (about half the pool), `over` (pool exhausted with overage), `empty` (seats, no usage yet) |
| `GitHub:Org` | none | Organization login (required unless demo) |
| `GitHub:Token` | none | Access token, configuration only (required unless demo) |
| `GitHub:BaseUrl` | `https://api.github.com/` | API base URL |
| `GitHub:ApiVersion` | `2026-03-10` | Sent as `X-GitHub-Api-Version` |
| `Copilot:CreditsPerSeat` | `1900` | Credits per seat per month |
| `Sync:IntervalHours` | `4` | Hours between syncs |
| `Sync:RunOnStartup` | `true` | Sync at startup (live mode) |
| `ConnectionStrings:Default` | `Data Source=data/copilot-usage.db` (`/home/data/copilot-usage.db` in Production) | SQLite file; a relative path is resolved against the web project folder |
| `Security:ContentSecurityPolicy` | built-in policy | Replace the Content-Security-Policy; empty turns it off. `{nonce}` is replaced per request |

Environment variables use `__` as the separator (`GitHub__Org`). `appsettings.Production.json` (committed, no secrets) sets the
App Service database path, demo off and quieter log levels (HttpClient at Warning). The token is only ever read from
configuration and the app refuses to start with an unresolved `@Microsoft.KeyVault(...)` text in its place.

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
src/CopilotUsage.Web     Blazor Server host: pages (Home, Users, UserDetail, NotFound, Error), UI components, config validation,
                         sync service and worker, health endpoint, security headers
infra                    Bicep template and example parameters
.github/workflows        ci.yml and deploy.yml
docs                     DEPLOY.md (runbook), API-ASSUMPTIONS.md
tests/CopilotUsage.Tests xUnit and bUnit tests (fixtures are synthetic)
```
