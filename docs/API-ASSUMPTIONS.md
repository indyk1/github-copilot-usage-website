# GitHub API assumptions

docs.github.com and api.github.com were not reachable when the foundation was built, and no token was available,
so **nothing here has been checked against a live response**. This file lists every assumption the code makes and
the one place to change each. It resolves PLAN.md section 3.5 only on paper: the first real run (or the phase 1
spike) must confirm or correct it.

Test fixtures under `tests/CopilotUsage.Tests/Fixtures` are **synthetic**: written by hand to match the DTOs below,
not recorded from GitHub. When real responses are captured, replace the fixtures, run `dotnet test`, and fix what breaks.

## Where to change things

| What | File |
|---|---|
| Every endpoint path and query string | `src/CopilotUsage.GitHub/GitHubEndpoints.cs` |
| Wire field names (JSON property names) | `src/CopilotUsage.GitHub/Dtos/Dtos.cs` |
| DTO to domain mapping and field semantics | `src/CopilotUsage.GitHub/GitHubMappings.cs` |
| API version header, auth header, retry and rate-limit policy | `src/CopilotUsage.GitHub/ServiceCollectionExtensions.cs` and `GitHubOptions.cs` (`GitHub:ApiVersion`) |
| Which calls the sync makes and how often | `src/CopilotUsage.Web/Sync/SyncService.cs` |
| Tolerant parsing rules (wrong types become null) | `src/CopilotUsage.GitHub/Json/LenientJson.cs` |

Parsing never throws on an unexpected field shape: unknown fields are ignored, missing fields are null or zero, wrong-typed
scalars become null, and non-array values where an array is expected become empty lists. Consequence: a renamed field shows
up as **zeros and empty lists, not as an error**, so after the first real sync check that the numbers are non-zero. The only
parse failure that surfaces is a body that is not a JSON object at all (for example an HTML error page), reported as a
`GitHubApiException`.

> Sections 1 to 4 describe **organization scope**. Enterprise scope is described in the last section, "Enterprise scope".

## Common to all calls

| Assumption | Detail |
|---|---|
| Base URL | `https://api.github.com/` (`GitHub:BaseUrl`) |
| Headers | `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2026-03-10`, `Authorization: Bearer <token>`, a `User-Agent` |
| Signed report URLs | Fetched with a separate client that sends **no** Authorization header, https only |
| Rate limits | Retries (max 4, exponential backoff with jitter, 2s base) on 5xx, 408, 429 and on 403 that carries `Retry-After` (secondary rate limit); `Retry-After` is honoured. A 403 without `Retry-After` (missing permission, or primary limit exhausted) is **not** retried and surfaces as an error. 30s per attempt, 5 minutes total per call |

## Token and scopes (PLAN.md 3.5 item 4: unverified)

The code only sends a bearer token; it does not care what type it is. Assumed requirements, to be confirmed:

- Seat endpoints: classic PAT with `manage_billing:copilot` or `read:org`; or a fine-grained token with the organization
  permission "GitHub Copilot Business" (read).
- Billing usage endpoint (`ai_credit/usage`): classic PAT with `manage_billing:copilot` (plus `read:org`), or fine-grained
  with organization "Administration" (read). The caller must be an org owner or billing manager.
- Usage metrics reports: org owner or an account allowed to see Copilot metrics, and the org's "Copilot usage metrics"
  policy must be enabled (otherwise expect 404/403, which the sync records as a failed step).

## 1. Seat summary

- Path: `GET /orgs/{org}/copilot/billing` (`GitHubEndpoints.SeatSummary`). Documented endpoint.
- Fields read (`SeatBillingDto`): `plan_type`, `seat_breakdown.{total, added_this_cycle, pending_invitation, pending_cancellation, active_this_cycle, inactive_this_cycle}`.
- Assumption: `total` is the number of assigned seats and is what the pool is sized from (billing is per assigned seat), not `active_this_cycle`.

## 2. Seats

- Path: `GET /orgs/{org}/copilot/billing/seats?per_page=100&page=N` (`GitHubEndpoints.Seats`). Documented endpoint.
- Pagination: page numbers until a page returns fewer rows than `per_page` (cap 200 pages); `Link` headers are not used.
- Fields read (`SeatsPageDto`, `SeatDto`): `total_seats`, `seats[].{created_at, updated_at, last_activity_at, last_activity_editor, plan_type, assignee.{login,id,avatar_url}, assigning_team.{name,slug}}`.
- PLAN.md 3.5 item 6 (does `last_activity_editor` still exist?): it is read if present and stored as null if not. Nothing depends on it.
- A seat without `assignee.login` is dropped.

## 3. AI-credit usage (billing)

- Path: `GET /organizations/{org}/settings/billing/ai_credit/usage?year=&month=&day=&user=&model=` (`GitHubEndpoints.CreditUsage`). Note `organizations`, not `orgs`. Path from PLAN.md 3.2, unverified.
- Response schema is a guess modelled on the older `premium_request/usage` report (`CreditUsageResponseDto`):
  `{ timePeriod:{year,month,day}, organization, user, usageItems:[{ product, sku, model, unitType, pricePerUnit, grossQuantity, grossAmount, discountQuantity, discountAmount, netQuantity, netAmount }] }`.
- Semantics assumed (`GitHubMappings.ToCreditRecords`):
  - `grossQuantity` = AI credits consumed (before the included pool is applied); if absent, credits = `grossAmount / 0.01`.
  - `grossAmount` = dollars at list price (= credits x $0.01); if absent, computed from credits.
  - Overage is **not** read from `netQuantity` / `netAmount`; it is computed from our own pool (seats x 1,900). If the real response's per-line "included vs billed" fields turn out to be authoritative, switch to them here.
  - Lines with no `model` use `sku`, then the text "unknown". Multiple lines for the same model and day are summed.
  - The response carries **no per-line date**: the day comes from `timePeriod` or from the request, which is why the sync calls the endpoint once per day.
  - A per-line `date` or `user` field, if present, is honoured.
  - No filtering by `product`: every usage line returned is counted as Copilot credits.
- Call pattern (PLAN.md 5): one call per day of the month (org-wide, `year`+`month`+`day`), and one call per seat for the month (`year`+`month`+`user`). Roughly 30 + seats calls per sync; the first 3 days of a month also re-sync the previous month. A step stops after 3 consecutive failures.
- Credits per user per **day** per model are not fetched (that would be seats x days calls). Hence the `UserModelMonthly` table (an addition to the table list in PLAN.md) and the per-user daily chart using the metrics `ai_credits_used` signal.
- PLAN.md 3.5 item 3 (does `usage/summary` return a remaining-pool figure?): not called. The pool is computed.
- PLAN.md 3.5 item 5 (pool sizing when seats change mid-month): ignored. The limit is the current seat count x `Copilot:CreditsPerSeat` for the month shown, even for past months viewed later.

## 4. Usage metrics reports

- Paths (PLAN.md 3.5 item 1, unverified), in `GitHubEndpoints`:
  - `GET /orgs/{org}/copilot/metrics/reports/users-28-day/latest` (used by the sync)
  - `GET /orgs/{org}/copilot/metrics/reports/users-1-day?day=YYYY-MM-DD` (implemented in the client, not used by the sync)
- Link response (`MetricsReportLinksDto`): `{ download_links:[https urls], report_start_day, report_end_day }` (`report_day` also accepted). Each URL is a signed link to an NDJSON file, downloaded without the token.
- NDJSON line = one user for one day (`UserMetricsLineDto`): `user_login`, `user_id`, `day`, `ai_credits_used` (assumed field name for the PLAN.md 3.3 "credits" group), `user_initiated_interaction_count`, `code_generation_activity_count`, `code_acceptance_activity_count`, `loc_suggested_to_add_sum`, `loc_suggested_to_delete_sum`, `loc_added_sum`, `loc_deleted_sum`, `used_chat`, `used_agent`, and the arrays below. The `user_login`, `day`, interaction/generation/acceptance counts, `loc_*` and `totals_by_*` names follow GitHub's published usage-metrics schema as remembered; `ai_credits_used` and `totals_by_client` are assumed from PLAN.md.
- Breakdown arrays (`MetricsTotalsDto`), all mapped to `UserMetricBreakdown` rows:
  - `totals_by_feature[].feature` -> dimension Feature
  - `totals_by_ide[].ide` (+ `last_known_ide_version.ide_version`, else `last_known_plugin_version.plugin_version`) -> Ide, with Version
  - `totals_by_client[].client` with `requests` and `tokens` (or `input_tokens` + `output_tokens`) -> Client (assumed shape)
  - `totals_by_language_feature[].language` (falls back to `totals_by_language_model`) -> Language, summed over features
  - `totals_by_model_feature[].model` (falls back to `totals_by_language_model`) -> Model, summed over features
- `ai_credits_used` is stored per user per day as an analysis signal. It is **never added to** the billed figures (PLAN.md 3.4).
- The sync only reads the latest 28-day report; days older than that are never back-filled.
- Lines without `user_login` or a parseable `day`, and lines that are not JSON, are skipped and counted (logged as a warning).

---

# Enterprise scope

Added after the first real call showed that the owner's organization belongs to a GitHub Enterprise account and **the enterprise, not the
organization, holds the Copilot billing**: the organization-level billing call was refused with 403 "Resource not accessible by personal
access token". There is still **no GitHub access from the build environment**: nothing below was run. What was checked is a web search of
GitHub's public documentation (docs.github.com itself is not reachable from here), so "documented" means "a search result said so", not "tested".
Run `scripts/check-github-token.sh --enterprise <slug>` (docs/DEPLOY.md, section 1.1) to turn the guesses into facts.

## Selection

`GitHub:Enterprise` set means enterprise scope: every call uses the enterprise endpoint. Only `GitHub:Org` set means organization scope, exactly as
described above. **Both set: the enterprise wins and the organization is ignored**, with one information line in the log at startup (least surprising
when a deployment keeps its old `GitHub__Org` and adds `GitHub__Enterprise`; failing to start would turn a harmless leftover into an outage). Startup
validation requires one of the two outside demo mode. Demo mode always behaves as an organization.

## Endpoints (all in `GitHubEndpoints.cs`)

| Call | Enterprise path | Status |
|---|---|---|
| Seats | `GET /enterprises/{enterprise}/copilot/billing/seats?per_page=&page=` | **Documented** (Copilot user management, public beta). Paginated; `total_seats` and `seats`; enterprise owners and billing managers only; classic token with `manage_billing:copilot` or `read:enterprise` per the English documentation as found (a translated page named `admin:enterprise`; **unverified**). A user reachable through several organizations is counted once in `total_seats`. |
| Seat summary | none | **Believed not to exist.** Not called. See the table below. |
| AI-credit usage | `GET /enterprises/{enterprise}/settings/billing/ai_credit/usage?year=&month=&day=&user=&model=` | **Guess.** Mirrors the **documented** enterprise `GET /enterprises/{enterprise}/settings/billing/premium_request/usage` (administrators and billing managers only; documented as not working with fine-grained tokens, GitHub App tokens; data for the past 24 months). That endpoint also accepts `organization` and `cost_center_id` filters; the app uses neither. The response is assumed to have the same shape as the organization one (section 3). |
| Usage metrics, 28-day | `GET /enterprises/{enterprise}/copilot/metrics/reports/users-28-day/latest` | **Guess.** Mirrors the organization path, which is itself unverified (section 4). |
| Usage metrics, one day | `GET /enterprises/{enterprise}/copilot/metrics/reports/users-1-day?day=YYYY-MM-DD` | **Guess**, and not used by the sync. |

Everything else (headers, retries, signed report downloads without the token, tolerant parsing) is as in "Common to all calls". The sync steps keep
their names (`Seats`, `OrgCreditsByDay`, ...); in enterprise scope "org" in those names means the whole enterprise.

## Seat rows

`seats[].organization.login` (the organization that assigned the seat) is read **if present** (**guess**: the organization seat object has an
`organization` field; whether the enterprise list carries it is unknown). `assigning_team` may be an organization team or an enterprise team; the
same `name`/`slug` fields are read. If one login comes back in several rows (one per organization) the rows are merged: organizations are joined
(`org-a, org-b`), the first non-empty value of the other fields is kept, the latest `last_activity_at` wins. If the number of distinct logins differs
from GitHub's `total_seats` a warning is logged and the list is used. An **empty list while `total_seats` is above zero is treated as a failed step**
(the old seats are kept), the same guard as the organization scope has against a renamed field.

The organization is stored on the seat (migration `EnterpriseScope`) and shown on the users table and the user page **only when more than one
distinct organization is present** across the seats.

## Seat summary fields in enterprise scope

The organization summary (`/orgs/{org}/copilot/billing`) supplies `plan_type` and `seat_breakdown`. In enterprise scope nothing is invented:

| Field | Organization scope | Enterprise scope |
|---|---|---|
| `TotalSeats` (the pool size) | `seat_breakdown.total` | **Derived**: the number of distinct logins in the seat list (deduplicated across organizations) |
| `ActiveThisCycle` | `seat_breakdown.active_this_cycle` | **Derived**: seats whose `last_activity_at` falls in the current UTC calendar month (the billing cycle) |
| `InactiveThisCycle` | `seat_breakdown.inactive_this_cycle` | **Derived**: total minus active |
| `AddedThisCycle` | `seat_breakdown.added_this_cycle` | **Derived**: seats whose `created_at` falls in the current UTC month |
| `PlanType` | `plan_type` | **Derived**: the distinct seat `plan_type` values, joined with `+` (for example `business`, or `business+enterprise`); null if none is given |
| `PendingInvitation` | `seat_breakdown.pending_invitation` | **Unavailable** (null, stored as NULL). Invitations are not seats. |
| `PendingCancellation` | `seat_breakdown.pending_cancellation` | **Unavailable** (null). The seat objects may carry `pending_cancellation_date` but that is not relied on. |

Only `TotalSeats` and `ActiveThisCycle` are shown anywhere in the UI; the rest is stored. The derived "active" figure is **not** GitHub's own
`active_this_cycle` and can differ from it (it depends on how GitHub counts activity).

## Pool

In enterprise scope the pool is **all enterprise seats x credits per seat**, deduplicated by user (`TotalSeats` above), exactly like the organization
pool but over the enterprise. `Copilot:CreditsPerSeat` is one number for everyone: if the enterprise mixes plans with different included credits
(`PlanType` shows `business+enterprise`) the pool is wrong by the difference, and the number must be set by hand to a blended value or accepted.
GitHub also lets an enterprise assign cost centers and budgets; those are not modelled.

## Token (enterprise scope)

Classic personal access token created by an enterprise owner (or billing manager where sufficient). Scopes **unverified**: `manage_billing:copilot`
and `read:enterprise`; `manage_billing:enterprise` is the next candidate if the usage line answers 403. Fine-grained tokens are understood not to be
accepted by the enterprise billing endpoints (documented for `premium_request/usage`). If the enterprise or organization enforces SAML SSO the token
must be authorised for it. Failure messages: a 403 in enterprise scope logs a hint at token type and scopes; a 403 or 404 in organization scope on
the seat or billing calls logs a hint that the organization may be billed through an enterprise and `GitHub:Enterprise` should be set.

## What the failure messages contain

- **Application log** (`GitHubClient`, once per failed call after retries): the call kind, the request path **without the query string** (the query
  can hold a user login), the HTTP status, the scope, GitHub's own `message` (one line, truncated to 300 characters, the token value replaced if it
  were echoed) and the hint. Never the token or the Authorization header. A failed signed-report download logs only the status (the URL carries a signature).
- **Sync log and the "Sync failed" banner** (stored in the database, shown to every signed-in user): the step, the call in the step, the HTTP status and the
  hint, for example `Seats step (seat summary) failed: GitHub answered HTTP 403 Forbidden. ...`. No path, no GitHub message text, no organization or
  enterprise name. Failures that are not GitHub responses (network down, timeout) keep their exception message, as before.
- `POST /api/sync` still returns no error text at all.

## Fixtures

New fixtures (`enterprise_seats_page1.json`, `enterprise_seats_page2.json`) are **synthetic**, written by hand to the shapes above; they were not recorded from GitHub.
