# Copilot Usage Dashboard

A .NET 10 Blazor dashboard showing how a GitHub organization consumes its monthly Copilot AI-credit allowance
(Copilot Business: 1,900 credits per seat, pooled across the org, 1 credit = $0.01, reset 00:00 UTC on the 1st).
See [PLAN.md](PLAN.md) for the full spec. A background job syncs GitHub data into SQLite; pages read only from SQLite.

**Status: phase 3 (users).** Foundation (calculations, GitHub client, database, sync, demo data), the "glass and aurora"
theme, the home page, the Users table (`/users`) and the per-user drill-down (`/users/{login}`) are done. Azure hosting and
the deploy pipeline are still to come.

## Pages

| Page | What it shows |
|---|---|
| `/` | Credit pool gauge, stat tiles, burn chart, model split, leaderboard |
| `/users` | Every seat: credits, percent of fair share, prompts, acceptance rate, top model, last active. Search by login as you type; click a column header (or use the sort menu on a phone) to sort, click again to reverse. Default: credits, highest first. Seats with no usage are marked "No usage this month", never hidden. A starting view can be set in the address: `/users?q=chen&sort=prompts&dir=asc` (`sort` is one of `user`, `credits`, `fair`, `prompts`, `acceptance`, `model`, `active`) |
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
