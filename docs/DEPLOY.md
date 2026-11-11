# Deploying to Azure App Service

A runbook for the owner. It assumes no Azure expertise, but you do need rights to create resources and to run commands in a terminal.

**Status of this runbook:** the Bicep template and the workflows were written and syntax-checked, but never run against a real Azure subscription or GitHub repository. Expect to fix small things on the first real deployment. Steps that are guesses are marked *unverified*.

What you get: one Linux App Service (.NET 10, one instance, Always On, HTTPS only, TLS 1.2+, websockets on), a Key Vault holding the GitHub token, and **access restrictions that only let your listed IP ranges in**. The app has no login, so those restrictions are its only protection.

## 1. Prerequisites

1. **Azure**: a subscription where you are **Owner** of an empty resource group (the template creates a role assignment, which Contributor alone cannot do).
2. **Tools** on your computer: the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`, version with Bicep support; `az bicep install` if asked).
3. **GitHub**: admin rights on this repository (to add variables and run workflows) and an owner of the GitHub organization whose Copilot usage you want to show.
4. **Your network's public IP ranges** (office, VPN), as CIDR. A single address is `198.51.100.7/32`. Ask your network team. Do not guess: a wrong list locks you out, and an empty one is refused.
5. **A GitHub token** the app uses to read Copilot data:
   - Create it as an **organization owner** (or billing manager) under GitHub > Settings > Developer settings > Personal access tokens. Either a classic token with `manage_billing:copilot` and `read:org`, or a fine-grained token on the organization with Copilot Business (read) and Administration (read).
   - The organization's **"Copilot usage metrics" policy must be enabled**.
   - **These scopes are assumptions, not verified.** See "Token and scopes" in [API-ASSUMPTIONS.md](API-ASSUMPTIONS.md). If the first sync fails with 401, 403 or 404, the token type or scopes are the first suspect.
   - Keep it out of files, chat and the repository. You will paste it once into a terminal prompt.

## 2. Deploy the infrastructure

```bash
az login
az account set --subscription "<subscription name or id>"
az group create --name rg-copilot-usage --location westeurope   # skip if you already have an empty group

cp infra/main.parameters.example.json infra/main.parameters.json   # your copy is git-ignored; keep real values out of the example file
# edit infra/main.parameters.json: namePrefix, location, githubOrg, allowedIpRanges

read -rsp "GitHub token: " GH_TOKEN; echo
az deployment group create \
  --resource-group rg-copilot-usage \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json \
  --parameters githubToken="$GH_TOKEN"
unset GH_TOKEN
```

The output lists `webAppName` (you need it in section 3), `webAppHostName` and `keyVaultName`. The token is stored only in Key Vault; the app's settings hold a reference to it.

- `allowedIpRanges` has **no default and may not be empty**; the deployment is refused otherwise. To make the site open to the whole internet on purpose, you must write `["0.0.0.0/0"]` yourself. Do not do that for real data.
- Re-running the same command is safe and is how you change IP ranges, SKU, org or the sync interval. It resets the access restrictions to exactly the list given.
- If the deployment fails creating the secret, your account may lack permission on the vault: grant yourself **Key Vault Secrets Officer** on it and re-run (*unverified*).
- After this step the site exists but is empty (Azure's default page, or an error) until you deploy code.

## 3. Set up deployment from GitHub (OIDC, no stored secret)

The deploy workflow signs in to Azure with a short-lived token that GitHub issues; no password or publish profile is stored anywhere.

```bash
APP_NAME=$(az deployment group show -g rg-copilot-usage -n main --query properties.outputs.webAppName.value -o tsv)
SUB=$(az account show --query id -o tsv); TENANT=$(az account show --query tenantId -o tsv)
REPO="<github-owner>/<repository-name>"

APP_ID=$(az ad app create --display-name "copilot-usage-deploy" --query appId -o tsv)
az ad sp create --id "$APP_ID"
az role assignment create --assignee "$APP_ID" --role Contributor \
  --scope "/subscriptions/$SUB/resourceGroups/rg-copilot-usage"
az ad app federated-credential create --id "$APP_ID" --parameters '{
  "name": "github-main",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:'"$REPO"':ref:refs/heads/main",
  "audiences": ["api://AzureADTokenExchange"]
}'
echo "AZURE_CLIENT_ID=$APP_ID  AZURE_TENANT_ID=$TENANT  AZURE_SUBSCRIPTION_ID=$SUB  AZURE_WEBAPP_NAME=$APP_NAME"
```

(The deployment name `main` is the default derived from `main.bicep`; if `az deployment group show` finds nothing, read the name from the output of section 2.)

In GitHub, open **Settings > Secrets and variables > Actions > Variables** and add these five **repository variables** (none is a secret):

| Variable | Value |
|---|---|
| `AZURE_WEBAPP_NAME` | web app name from section 2 |
| `AZURE_RESOURCE_GROUP` | `rg-copilot-usage` (or yours) |
| `AZURE_CLIENT_ID` | `APP_ID` from above |
| `AZURE_TENANT_ID` | tenant id |
| `AZURE_SUBSCRIPTION_ID` | subscription id |

Then run it: **Actions > Deploy > Run workflow**, branch `main`. It builds, runs the tests, publishes, and deploys. The workflow fails immediately, naming what is missing, if a variable is not set or if it was started on another branch. It never runs on push.

The deployment (SCM) site is locked to your IP ranges too, and GitHub's runners are not on that list, so the workflow adds the runner's address to the SCM rules for the duration of the deploy and removes it afterwards (*unverified*; the step takes about a minute because rule changes take time to apply). If a run is killed half-way, remove the leftover rule `github-deploy` in the portal (web app > Networking > Access restrictions > Advanced tool site) or re-run section 2.

## 4. Lock down access, and check it

The list lives in the `allowedIpRanges` parameter (section 2) and is applied to both the site and the SCM site, with everything else denied.

Check it from **outside** your allowed ranges (a phone on mobile data with Wi-Fi off, and VPN off):

1. `https://<webAppHostName>/` should show **403 Forbidden** ("Ip Forbidden"), not the dashboard.
2. `https://<webAppName>.scm.azurewebsites.net/` should also be **403**.
3. From an allowed address, the dashboard loads over `https://`; `http://` redirects to https.

If the dashboard shows from outside, stop and check the list (portal: web app > Networking > Access restrictions). GitHub-hosted runners are not on your list either; the deploy workflow handles that for the SCM site (section 3). The template also sets `/healthz` as the App Service health-check path; whether Azure's internal probe bypasses the restrictions is *unverified*, and with one instance a failing probe only shows in the portal.

## 5. First-run checklist

The GitHub response shapes in this app were **never verified against the real API**, and a wrong field name does not raise an error: it shows as **zeros or empty lists**. After the first real sync (it starts a few seconds after the app does; allow a couple of minutes, longer for a big organization), check every item. If you cannot see the site, use the log stream (section 6).

1. **Home page says "Data as of" with a recent time**, and no red "Sync failed" or amber "Partly updated" banner. If there is one, read the message under it and the logs (section 6).
2. **Seat count** on the home page ("N seats x 1,900 credits") equals the number of Copilot seats in the GitHub organization settings (Settings > Copilot > Access). If it is 0 or off: `GitHubMappings.cs` (seat summary mapping) and `Dtos/Dtos.cs` (`SeatBillingDto`).
3. **Pool limit** equals seats x credits per seat (1,900 for Copilot Business). If your plan differs, change `Copilot__CreditsPerSeat` (section 2).
4. **Credits are non-zero** once people have used Copilot this month, and the total roughly matches GitHub's billing page (Billing > Usage). All zeros while GitHub shows usage means the credit response fields do not match: `GitHubEndpoints.cs` (the `ai_credit/usage` path), `Dtos/Dtos.cs` (`CreditUsageResponseDto`), `GitHubMappings.cs` (`ToCreditRecords`).
5. **Per-user figures**: the leaderboard and `/users` show credits per person, and one user page matches what GitHub shows for that person. Zeros here with a non-zero pool point at the per-user call (same three files).
6. **Per-model figures**: the "Model split" card lists real model names, and a user page has a model breakdown. If it says "No model usage" or "unknown", check the `model` / `sku` fields in `Dtos/Dtos.cs` and `GitHubMappings.cs`.
7. **Activity data**: user pages show prompts, acceptance rate, features and IDEs. Empty means the usage-metrics report was not read: `GitHubEndpoints.cs` (report paths), `Dtos/Dtos.cs` (the `UserMetricsLineDto` and totals types), and that the "Copilot usage metrics" policy is on.
8. **Health endpoint**: open `https://<host>/healthz` from an allowed address. Expect `"status":"ok"`, `"databaseReachable":true`, `"lastSuccessfulSync"` set, `"demo":false`. `degraded` means the app works but the data is missing, stale (older than three sync intervals) or the last sync failed.
9. **Sync log** (section 6): look for `Sync N succeeded: Seats=Succeeded(...), OrgCreditsByDay=..., UserCreditsByModel=..., UserMetrics=...` with non-zero row counts for each step. A step with `0 rows` and no error is the same "wrong field name" symptom as above.
10. **Footer** shows the version and commit you just deployed (`Version 1.0.0 (abc1234)`).

Where to correct things (all under `src/CopilotUsage.GitHub/`): `GitHubEndpoints.cs` for paths and query strings, `Dtos/Dtos.cs` for JSON field names, `GitHubMappings.cs` for how fields become credits. After a change: `dotnet test`, merge to `main`, run the Deploy workflow. Do not edit the live site.

## 6. Operating notes

**Logs.** Portal: web app > Monitoring > Log stream. Or `az webapp log tail -g rg-copilot-usage -n <webAppName>`. Logs never contain the token or Authorization headers (HttpClient logging is also set to Warning in Production). Look for lines from `CopilotUsage.Web.Sync.SyncService`.

**What a failed sync looks like.** In the UI: a red banner "The last sync failed" (or amber "Partly updated") on the home page, with a short reason and the time of the last good data; figures stay on the last good data. In the logs: `Sync failed fetching ...` or `Sync step ... failed` at Error level. A step gives up after three consecutive failures and the next scheduled sync tries again. `/healthz` shows `degraded` with `lastSyncStatus` `Failed`. Common causes: 401 (token wrong, expired or the Key Vault reference did not resolve), 403 (missing scope, or rate limit), 404 (path wrong, or the metrics policy is off).

**Run exactly one instance, never scale out.** The sync runs inside the web process and SQLite has one writer on a network file share. Two instances would sync twice and write to the same file at once, which can corrupt it. The template pins one instance; do not add autoscale or increase the instance count. A deployment restarts the app for a few seconds, and it syncs again on start.

**Backing up the database.** The data is a single file, `/home/data/copilot-usage.db` (journal mode is the default, not WAL, so it is one file). GitHub only returns the current month, so earlier months cannot be rebuilt: back it up. Stop the app so nothing is writing, copy the file, start it again (*unverified*):

```bash
az webapp stop  -g rg-copilot-usage -n <webAppName>
TOKEN=$(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv)
curl -H "Authorization: Bearer $TOKEN" -o copilot-usage-backup.db "https://<webAppName>.scm.azurewebsites.net/api/vfs/data/copilot-usage.db"
az webapp start -g rg-copilot-usage -n <webAppName>
```

Run it from an allowed IP address (the SCM site is restricted). Store the copy somewhere private; it holds user logins and usage. To restore, upload the file to the same path (Kudu file browser on the SCM site) while the app is stopped.

**Rotating the GitHub token.** Create the new token (section 1), then store it as a new secret version and restart so the app re-reads it:

```bash
read -rsp "New GitHub token: " T; echo
az keyvault secret set --vault-name <keyVaultName> --name github-token --value "$T" >/dev/null; unset T
az webapp restart -g rg-copilot-usage -n <webAppName>
```

You need **Key Vault Secrets Officer** on the vault for that command. Without the restart the app picks the new version up within about a day. Then press Refresh on the home page and check that no sync banner appears. Revoke the old token on GitHub.

**Demo mode.** Demo mode shows synthetic data and never calls GitHub. The app protects real data from it, judging by the sync log stored inside the database (each run records whether it was demo), not by file names:

- **Demo on, database holds data from a real sync:** the app **refuses to start** and prints how to fix it (`Demo__Enabled=false`, or point `ConnectionStrings__Default` at a separate file). Nothing is deleted. The Refresh button is refused the same way and shows a "Sync failed" banner with that message. Data with no sync log at all is treated as real.
- **Demo on, database empty or holding only demo data:** it is filled (or re-filled) with demo data, as before.
- **Demo off, database holds only demo data:** at startup the app clears the demo seats, usage and demo sync runs (a warning line in the log says so), so synthetic users never appear beside real ones; the first real sync then fills it.

To show demo data on the same web app, use a separate file anyway:

```bash
az webapp config appsettings set -g rg-copilot-usage -n <webAppName> --settings Demo__Enabled=true "ConnectionStrings__Default=Data Source=/home/data/demo.db"
```

(`Demo__Scenario` can be `normal`, `over` or `empty`.) A "Demo data" badge appears in the header. To go back, set `Demo__Enabled=false` and `ConnectionStrings__Default=Data Source=/home/data/copilot-usage.db`. Re-running section 2 also restores the template's values. If the app will not start after a settings change, read the log stream (section 6).

**Settings you can change** without code: `GitHub__Org`, `Copilot__CreditsPerSeat`, `Sync__IntervalHours` (section 2, or Configuration in the portal). The app refuses to start, with the reason in the log stream, if the organization or token is missing or if the token is still the unresolved text `@Microsoft.KeyVault(...)`: in that case check that the web app's managed identity has the *Key Vault Secrets User* role on the vault and that the setting shows a green tick under Configuration.

**Security headers and CSP.** The app sends a Content-Security-Policy and the usual hardening headers. If a future change breaks a page with a console error about the policy, adjust `src/CopilotUsage.Web/Hosting/SecurityHeaders.cs`; as a temporary escape hatch the setting `Security__ContentSecurityPolicy` replaces the policy, and an empty value removes it.
