# Copilot Usage Dashboard — Build Plan

A .NET dashboard that shows how one GitHub billing entity (an enterprise, or a self-billed organization)
is consuming its monthly Copilot allowance, who is consuming it, and what each person is doing with it.

## 1. Decisions

| Topic | Decision |
|---|---|
| Account | One GitHub billing entity: an **enterprise** (`GitHub:Enterprise`, when the enterprise holds the Copilot billing) or a single self-billed **organization** (`GitHub:Org`). The enterprise wins if both are set. See the addendum at the end of section 3 |
| Plan | Copilot Business |
| Limit model | Pool of the billing entity (enterprise-wide or organization-wide), plus a notional per-user "fair share" |
| Stack | Blazor Web App (.NET 10, Interactive Server) |
| Data | Sync into SQLite (EF Core), triggered by `POST /api/sync` |
| Scheduler | An Azure Function (Flex Consumption, timer, every 4 hours) calls the sync endpoint with its managed identity. No in-app timer in production: the Free plan sleeps the web app when idle |
| Access | Microsoft Entra ID sign-in (single tenant, ID-token flow, no secret). No IP restrictions: network-level control is an Entra Conditional Access policy with Global Secure Access, owned by the IT admin |
| User drill-down | Credits and cost, model breakdown, feature and tool mix, productivity signals |
| Design | Glass and aurora (dark, frosted cards, soft gradient light) |
| Hosting | Azure App Service **Free (F1)**, Linux, .NET 10; SQLite on the persistent `/home/data` disk (no Azure SQL); cheapest option for every resource, target cost zero |
| Infrastructure | Terraform (azurerm, single root module), state in a hand-made storage account. Entra objects are created by hand (no directory permissions needed) |
| CI/CD | Azure DevOps Pipelines reading this GitHub repository: CI, infrastructure (plan, approved apply) and deploy. No GitHub Actions |
| Extras | None (no alerts, team views, seat hygiene or export in v1) |

## 2. How Copilot billing works now

This changes what "limit" and "remaining" mean, so it drives the whole design.

- Since 1 June 2026 Copilot bills in **AI credits**, not premium requests.
  1 credit = $0.01. Cost depends on the model and the tokens consumed.
- Copilot Business includes **1,900 credits per seat per month**.
- Credits are **pooled across the billing entity**: the organization when it is billed by itself, the whole enterprise when the enterprise holds the billing. 20 seats = one shared pool of
  38,000 credits. GitHub has no per-person allowance.
- The pool resets at 00:00 UTC on the 1st of each month. Unused credits do not roll over.
- Inline code completions and next edit suggestions are unlimited and cost no credits.
  Chat, agent mode, CLI, cloud agent, code review and third-party agents do.
- Once the pool is spent, extra usage is billed at $0.01 per credit unless the
  org's "AI credits paid usage" policy is off, in which case usage is blocked
  until the next month.

So the dashboard's numbers are:

- **Limit** = active seats x 1,900 (per-seat figure held in config)
- **Used** = sum of credits consumed this calendar month (UTC)
- **Remaining** = limit - used, floored at zero
- **Overage** = credits used beyond the limit, shown in dollars
- **Fair share** = 1,900 per user; a display benchmark only, not a GitHub limit

## 3. What GitHub exposes

All calls use `X-GitHub-Api-Version: 2026-03-10`.

### 3.1 Seats — Copilot user management API

- `GET /orgs/{org}/copilot/billing` — plan type and seat counts: total, added this
  cycle, pending invitation, pending cancellation, active this cycle, inactive this cycle.
- `GET /orgs/{org}/copilot/billing/seats` — one row per seat: assignee (login,
  avatar), created and updated dates, plan type, assigning team, last activity date.

Used for: the seat count behind the pool size, the user list, avatars, last active.

### 3.2 Credits — billing usage API

- `GET /organizations/{org}/settings/billing/ai_credit/usage` — AI credit usage for
  the org, filterable by `year`, `month`, `day`, `user` and `model`.
- `GET /organizations/{org}/settings/billing/usage/summary` — org usage summary
  (public preview).

Used for: the headline used and remaining figures, the leaderboard, and each
user's credits per model. This is the billed figure, so it is the source of truth
for anything with a currency sign on it.

### 3.3 Activity — Copilot usage metrics API

Daily and rolling 28-day reports at org level and per user. Each call returns
short-lived signed URLs to NDJSON files, which the sync job downloads. Requires
the org's "Copilot usage metrics" policy to be enabled.

Per user, per day, the reports contain:

| Field group | Contents |
|---|---|
| Credits | `ai_credits_used` — total only, not split by model or feature |
| Prompts | `user_initiated_interaction_count` — explicit prompts sent |
| Suggestions | generation count, acceptance count (gives acceptance rate) |
| Lines of code | suggested, added, deleted |
| By feature | chat ask / edit / agent modes, inline chat, completions, CLI, code review |
| By IDE | per editor, with last known IDE and plugin version |
| By client | VS Code, JetBrains, Copilot CLI, and third-party tools such as Claude Code, Codex, OpenCode (requests and tokens) |
| By language | activity per programming language, crossed with feature and model |
| By model | activity per model, crossed with feature (chat only, not completions) |
| Flags | whether the user used chat, used agent mode |

Used for: everything on the drill-down page that is not a credit figure.

### 3.4 Limits of the data

- **No real-time data.** Reports are generated daily. The dashboard shows a
  "data as of" timestamp and should not imply live figures.
- **Credits per model come from billing, not metrics.** The metrics API gives
  activity per model; only the billing API gives credits per model. Per-user model
  cost therefore needs one billing call per user per sync.
- **Two credit figures exist.** `ai_credits_used` in metrics is described by GitHub
  as an analysis signal, not a billed total. The dashboard uses billing for totals
  and never sums the two.
- **Per-feature credit cost is not available.** GitHub does not split credits by
  feature. The feature mix can only be shown as activity counts.
- **No prompt content, no per-repository per-user data.**
- **Fair share is ours, not GitHub's.** Label it clearly in the UI.

### 3.5 To verify against a live response before coding

docs.github.com was unreachable from the planning session, so the items below
come from search summaries of the docs and need a first-day check:

1. Exact paths of the usage metrics report endpoints for an organization.
2. Response schema of `ai_credit/usage` (field names for quantity, included vs
   billed amounts, model, user).
3. Whether the org `usage/summary` endpoint returns a remaining-pool figure. If
   it does, use it instead of the computed one.
4. Token type accepted by the org billing endpoints (classic PAT with
   `manage_billing:copilot` and `read:org`, versus fine-grained with org
   Administration read).
5. How GitHub sizes the pool when seats are added or removed mid-month.
6. Whether the seats response still includes `last_activity_editor`.

### 3.6 Enterprise scope (addendum)

The first real call showed that the organization belongs to a GitHub Enterprise account and the **enterprise holds the Copilot billing**, so the
organization-level billing call is refused (403). With `GitHub:Enterprise` set the app uses the enterprise endpoints for every call and the pool is
all enterprise seats x credits per seat, each user counted once:

- `GET /enterprises/{enterprise}/copilot/billing/seats` (documented); there is no known enterprise seat-summary endpoint, so the summary is derived from the seats.
- `GET /enterprises/{enterprise}/settings/billing/ai_credit/usage` and `GET /enterprises/{enterprise}/copilot/metrics/reports/users-28-day/latest`
  (guesses that mirror the organization paths and the documented enterprise `premium_request/usage`).
- A classic token from an enterprise owner (scopes unverified: `manage_billing:copilot`, `read:enterprise`).

Details, what is documented and what is a guess, and how each summary field is handled: docs/API-ASSUMPTIONS.md, "Enterprise scope". Verify with
`scripts/check-github-token.sh` before the first deployment (docs/DEPLOY.md, section 1.1). Organization scope (self-billed organizations) is unchanged.

## 4. Pages

### 4.1 Home `/`

- **Pool gauge** — large radial meter: credits used of limit, percent, remaining.
- **Stat row** — remaining credits, dollar value used, overage so far, days until
  reset, active seats.
- **Burn chart** — cumulative credits by day this month against the limit line and
  an even-pace line.
- **Leaderboard** — users ranked by credits this month: avatar, name, credits,
  share of pool, bar against fair share (changes colour past 100%). Each row links
  to the user page.
- **Model split** — org-wide credits by model.
- "Data as of" timestamp and a manual refresh button.

### 4.2 Users `/users`

Searchable, sortable table of every seat: credits, percent of fair share, prompts,
acceptance rate, top model, last active. Rows link to the drill-down.

### 4.3 User drill-down `/users/{login}`

- **Credits and cost** — credits this month against fair share, dollar value,
  daily credits chart, rank in the org.
- **Model breakdown** — credits and share per model, plus activity per model.
- **Feature and tool mix** — activity by feature, by IDE (with versions), by client.
- **Productivity signals** — prompts sent, suggestions and acceptance rate, lines
  added and deleted, languages, active days, last activity.

## 5. Architecture

```
CopilotUsage.sln
  src/CopilotUsage.Web      Blazor Web App: pages, components, charts, theme
  src/CopilotUsage.Core     Domain models, calculations (pool, fair share, ranks)
  src/CopilotUsage.GitHub   Typed HttpClient, DTOs, NDJSON report reader
  src/CopilotUsage.Data     EF Core DbContext, migrations, queries
  tests/CopilotUsage.Tests  xUnit: calculations, parsers against recorded fixtures
```

- **GitHub client** — typed `HttpClient` with resilience (retry, rate-limit
  backoff). Octokit does not cover these endpoints, so DTOs are hand-written and
  tolerant of unknown fields.
- **Sync** — a service the web app runs when `POST /api/sync` is called (by the Azure Function, every few hours; callable only by its managed identity), and when
  a signed-in user presses Refresh. An in-process timer exists but is off outside local runs:
  1. seats and seat summary
  2. org credit usage for the current month, by day
  3. credit usage per user per model
  4. per-user daily metrics reports (NDJSON)
  Each step upserts, so re-running is safe. Each run is logged in a `SyncRun` table.
- **Database** — SQLite via EF Core. Tables: `Seat`, `SeatSnapshot`,
  `CreditUsageDaily` (day, user, model, credits, amount), `UserMetricDaily`,
  `UserMetricBreakdown` (day, user, dimension, key, counts), `SyncRun`.
- **UI** — Blazor Interactive Server, so the GitHub token stays on the server.
  Charts with Blazor-ApexCharts. Pages read only from SQLite, never from GitHub.
- **Config** — `GitHub:Enterprise` or `GitHub:Org` (one is required; the enterprise wins), `GitHub:Token`, `Copilot:CreditsPerSeat` (1900),
  `Sync:IntervalHours`, `AzureAd:*`, `Sync:AllowedCallerObjectId` (see docs/AUTH.md).
- **Azure Function** — `src/CopilotUsage.SyncFunction`: a timer that gets a token from its managed identity and calls the sync endpoint. It holds no secret
  and never touches the database or GitHub.

## 6. Design: glass and aurora

- Deep navy-black base with slow-moving blurred gradient light (violet, teal,
  blue) behind the content.
- Frosted translucent cards: backdrop blur, 1px light border, large radius, soft glow.
- Gradient-stroked gauge and bars; tabular numerals for all figures.
- Restrained motion: gauge sweep and count-up on load, hover lift on cards.
  Respects `prefers-reduced-motion`.
- Status colour is carried by more than hue (label and icon as well) so over-limit
  states remain readable.
- Design tokens as CSS variables in one theme file; responsive down to phone width.

## 7. Hosting on Azure

Details, cost and the order of operations are in docs/DEPLOY.md; the Entra contract is in docs/AUTH.md.

- **Web app**: Linux App Service on the **Free (F1)** plan, .NET 10, a single instance (SQLite needs one writer). F1 has no Always On, so the app sleeps when idle
  and cold-starts; it has a daily CPU quota and a small WebSocket limit (documented in DEPLOY.md). Moving up is one Terraform variable (`web_sku`), which also
  turns on Always On and the `/healthz` health check.
- **Data**: SQLite file under `/home/data` (persistent). That storage is a network share, so use the default journal mode rather than WAL.
- **Sync**: an Azure Function on the Flex Consumption plan (timer, every 4 hours) calls `POST /api/sync` with its user-assigned managed identity. It wakes the
  web app, which does the GitHub calls.
- **Secrets**: the GitHub token is in Key Vault, referenced from App Settings; its value is set by hand and is never in Terraform, state or a pipeline. The
  data-protection keys are files under `/home/data`.
- **Access**: Entra ID sign-in in the app. No IP restrictions and no private endpoint. Network control, if wanted, is a Conditional Access policy requiring the
  Global Secure Access compliant network, which blocks the sign-in (not the network) and is configured by the IT admin.
- **Infrastructure and delivery**: Terraform in `infra/` and Azure DevOps Pipelines in `azure-pipelines/` (CI; infrastructure with an approval gate; deploy with a
  `/healthz` smoke check). The Entra app registration is made by hand.

## 8. Build phases

1. **Spike** — call each endpoint with a real token, save responses as fixtures,
   resolve section 3.5.
2. **Foundation** — solution, EF Core schema and migrations, GitHub client, sync
   job, tests against fixtures.
3. **Home page** — theme, pool gauge, stat row, burn chart, leaderboard.
4. **Users** — users table and drill-down page.
5. **Sign-in and scheduling** — Entra ID sign-in, the sync endpoint, the Azure Function.
6. **Ship** — Terraform, Azure DevOps pipelines, runbook, README. Then the first real deployment and the first-run checklist.

## 9. Needed from the organization

- A token for the sync job (type confirmed by `scripts/check-github-token.sh`): an enterprise owner's classic token in enterprise scope, an org owner's token in organization scope.
- "Copilot usage metrics" policy enabled for the organization.
- An Entra IT admin: the app registration (docs/AUTH.md), the `Sync.Run` role assignment and, optionally, the Conditional Access policy.
- An Azure subscription, an Azure DevOps organization and a place for the Terraform state (docs/DEPLOY.md, steps 2 and 3).

## Sources

- https://docs.github.com/en/copilot/concepts/billing-and-usage/organizations-and-enterprises/billing
- https://docs.github.com/en/copilot/concepts/billing/budgets-for-usage-based-billing
- https://docs.github.com/en/rest/billing/usage
- https://docs.github.com/en/rest/copilot/copilot-usage-metrics
- https://docs.github.com/en/copilot/reference/copilot-usage-metrics/copilot-usage-metrics
- https://docs.github.com/en/rest/copilot/copilot-user-management
- https://github.blog/news-insights/company-news/github-copilot-is-moving-to-usage-based-billing/
- https://github.blog/changelog/2026-06-19-ai-credits-consumed-per-user-now-in-the-copilot-usage-metrics-api/
- https://github.blog/changelog/2026-06-04-budget-and-usage-management-apis-now-generally-available/
- https://github.blog/changelog/2026-10-23-usage-metrics-api-adds-usage-from-tools-like-opencode/
