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
