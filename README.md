# Copilot Usage Dashboard

A .NET 10 Blazor dashboard showing how a GitHub organization consumes its monthly Copilot AI-credit allowance
(Copilot Business: 1,900 credits per seat, pooled across the org, 1 credit = $0.01, reset 00:00 UTC on the 1st).
See [PLAN.md](PLAN.md) for the full spec. A background job syncs GitHub data into SQLite; pages read only from SQLite.

**Status: phase 2 (theme and home page).** Foundation (calculations, GitHub client, database, sync, demo data) plus the
"glass and aurora" theme and the home page are done. The Users pages are "coming soon" stubs.

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
| `ConnectionStrings:Default` | `Data Source=data/copilot-usage.db` | SQLite file; a relative path is resolved against the web project folder |

Environment variables use `__` as the separator (`GitHub__Org`).

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
src/CopilotUsage.Web     host, config validation, sync service and worker, placeholder page
tests/CopilotUsage.Tests xUnit tests (fixtures are synthetic)
```
