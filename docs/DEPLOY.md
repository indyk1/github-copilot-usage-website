# Deploying to Azure: runbook

For the owner. It assumes no Azure expertise, but you do need to be able to run a few commands in a terminal and to click through the Azure,
Azure DevOps and Entra portals. Every step is marked **manual** (a person does it) or **pipeline** (Azure DevOps does it). Do them in order:
each step depends on the ones before it.

**Status of this runbook.** Nothing here has been run against a real Azure subscription, Azure DevOps organization or Entra tenant. The Terraform was
checked offline (format, `validate`, and an offline `terraform test`), the pipeline YAML was only parsed for syntax, and the application code is tested.
Everything that depends on how Azure behaves today is a best understanding, and where the author is unsure it says *(unverified)* next to the claim.
Expect to fix small things on the first real run; section 9 lists what is most likely to bite first.

## What you get

```
 people ──sign in (Entra ID)──▶  Web app (App Service, Free F1, Linux, .NET 10, Blazor Server)
                                    │  SQLite file on /home/data           GitHub token via Key Vault reference
                                    ▲
 Azure Function (Flex Consumption, timer every 4 h, no secrets) ──POST /api/sync with its managed identity's token
```

| Piece | What | Created by |
|---|---|---|
| Web app | App Service plan **F1 (Free)**, Linux, .NET 10. Entra sign-in. SQLite database on the persistent `/home/data` disk | Terraform (`infra/`) |
| Sync | Azure Function on the **Flex Consumption** plan, timer `0 0 */4 * * *`, calls `POST /api/sync` as a managed identity | Terraform |
| GitHub token | Key Vault (standard); the web app reads it through a Key Vault reference; **its value is never in Terraform** | vault: Terraform; value: a person |
| Logging | Log Analytics + Application Insights for the Function, capped, 30 days | Terraform |
| State | Terraform state in a storage account | a person, by hand (step 2) |
| Entra app registration | sign-in and the `Sync.Run` role | the IT admin, by hand (step 4, [AUTH.md](AUTH.md)) |
| Pipelines | CI, infrastructure, deploy, in Azure DevOps, reading this GitHub repository | a person (step 3) |

Network control is **not** done by the site. There are no IP restrictions: Entra sign-in protects the data, and your IT admin can additionally require
the Global Secure Access network with a Conditional Access policy (step 7).

## Expected monthly cost

At one short call every four hours the target is **zero**. These are estimates from the author's understanding of Azure pricing (verify on the Azure pricing pages);
the right-hand column says what would start to cost money.

| Resource | Expected per month | Starts to charge when |
|---|---|---|
| App Service plan **F1 (Free)** | 0 | You change `TF_WEB_SKU` to a paid size (B1 is roughly 10 to 15 USD, *unverified*). Overusing the Free quota does not charge, it stops the app for the day (section 8) |
| Function, **Flex Consumption**, 0 always-ready instances, 512 MB | 0 | You exceed the monthly free grant, understood to be 250,000 executions and 100,000 GB-seconds per subscription *(unverified)*. This workload is 180 runs and a few thousand GB-seconds. Setting always-ready instances or a larger memory size would add cost |
| Function storage account (LRS, a few small blobs) | about 0.01 to 0.10 USD | A lot more data or storage transactions than a timer produces |
| Key Vault (standard) | 0 to about 0.05 USD | Per 10,000 operations (cents). The web app reads one secret about once a day |
| Log Analytics workspace + Application Insights | 0 | More than 5 GB ingested in a month (understood free allowance, *unverified*) or retention beyond the free period. The daily cap of 0.1 GB (about 3 GB a month at most) and 30 day retention are there so this cannot happen; set `TF_ENABLE_MONITORING=false` to remove both |
| Managed identity, role assignments, resource group | 0 | Never |
| Terraform state storage account (manual, step 2) | about 0.01 to 0.05 USD | Never at this size |
| Azure DevOps | 0 | More than 5 users on the Basic plan, or paid parallel jobs. A new organization may first have to **request the free grant of Microsoft-hosted parallel jobs**; see section 9 |
| Microsoft Entra | 0 for the sign-in itself | Conditional Access needs Entra ID P1 or better, Global Secure Access needs its own licence: these are tenant licences, not Azure resources (step 7) |

Total expectation: **0 to well under 1 USD a month**.

---

## 1. Prerequisites and the GitHub token  *(manual)*

1. **An Azure subscription** and a person with **Owner** on it (or on the resource group from step 2) for the one-off role setup.
2. **An Azure DevOps organization** and project (step 3), and rights to create pipelines and service connections in it.
3. **An Entra IT admin** (step 4, step 5.6 and step 7). Creating app registrations and assigning an app role needs directory roles that most people do not have.
4. **The Azure CLI** (`az`) on your computer, version 2.60 or newer *(approximate)*: <https://learn.microsoft.com/cli/azure/install-azure-cli>. Sign in with `az login`.
5. **GitHub**: you must be an owner of the GitHub **enterprise** (when the organization belongs to a GitHub Enterprise account that holds the Copilot billing) or of the **organization** (when it is billed by itself), and an admin of this repository (to let Azure Pipelines read it).
6. **Which scope: enterprise or organization?** Look at who is billed. If the organization belongs to a GitHub Enterprise account, **the enterprise holds the Copilot billing** and the included credits (1,900 per Copilot Business seat) are pooled across the whole enterprise. Then use **enterprise scope**: set `TF_GITHUB_ENTERPRISE` (the enterprise slug, the last part of `github.com/enterprises/<slug>`). The dashboard then shows every Copilot seat in the enterprise, counting a user once even if several organizations assign them a seat. An organization-level call for such an organization is refused with 403 "Resource not accessible by personal access token". Use **organization scope** (`TF_GITHUB_ORG`, the organization login) only for a self-billed organization. If both are set the enterprise is used and the organization is ignored (the app logs one line at startup saying so); give only the one you mean.
7. **A GitHub token** the app uses to read Copilot data. Its type depends on the scope:
   - **Enterprise scope: a classic personal access token** created by an **enterprise owner** (or, where sufficient, a billing manager) under GitHub > Settings > Developer settings > Personal access tokens (classic). The enterprise billing endpoints are understood **not** to accept fine-grained tokens. Scopes, **unverified**: `manage_billing:copilot` and `read:enterprise` (GitHub's documentation for the enterprise seats endpoint names either of the two; the enterprise billing and usage-metrics endpoints may need more, and `manage_billing:enterprise` is the next one to try if the checker below answers 403 on the usage line).
   - **If the enterprise or the organization enforces SAML single sign-on, authorise the token for it** (the token's "Configure SSO" button). A token that is not authorised is refused with 403, and the response then carries an `X-GitHub-SSO` header (the checker reports it).
   - **A classic token is broader than a fine-grained one** (it carries the scope for everything that scope covers, not one organization's Copilot data). Treat it accordingly: store it **only in Key Vault** (step 5.4), give it an expiry, create it from an account that only needs this role, and never put it in a file, chat, pipeline variable or the repository.
   - **Organization scope (self-billed organization):** create it as an **organization owner** (or billing manager). Either a classic token with `manage_billing:copilot` and `read:org`, or a fine-grained token on the organization with Copilot Business (read) and Administration (read). The fine-grained route is the narrower one and is preferred here.
   - The **"Copilot usage metrics" policy must be enabled** (for the organization, or for the enterprise in enterprise scope).
   - **These scopes are assumptions, not verified.** See "Token and scopes" and "Enterprise scope" in [API-ASSUMPTIONS.md](API-ASSUMPTIONS.md). If the first sync fails with 401, 403 or 404, the token type and scopes are the first suspect; the next subsection tells you how to find out before deploying.
   - You will paste the token **once**, into a terminal prompt or the Key Vault page, in step 5.4. A classic token can be given an expiry. Put the expiry date in your calendar: section 8 explains rotation.

### 1.1 Check the token before you deploy anything  *(manual, do this first)*

`scripts/check-github-token.sh` calls every GitHub endpoint the app will use, for your scope, and prints one line per endpoint. It needs only `bash` and `curl`, takes the token from the environment variable `GITHUB_TOKEN` (never from an argument, never printed), and never prints response values: for a 200 it prints only the **field names** of the answer so you can compare them with the assumptions.

```bash
read -rs GITHUB_TOKEN && export GITHUB_TOKEN     # paste the token, press Enter; nothing is shown
scripts/check-github-token.sh --enterprise <enterprise-slug>      # enterprise scope
scripts/check-github-token.sh --org <organization-login>          # organization scope
unset GITHUB_TOKEN
```

Each line looks like `403  GET /enterprises/<slug>/copilot/billing/seats  [seats]  FIX THE TOKEN: ...`. It also prints the scopes GitHub reports for the token, and for a non-200 answer GitHub's short error text (an error message, not data). What the results mean:

| Result | Meaning | What to do |
|---|---|---|
| **200** on every line | The token and the endpoints work | Compare the listed field names with [API-ASSUMPTIONS.md](API-ASSUMPTIONS.md); a renamed field would show in the app as zeros |
| **401** | GitHub rejected the token | **Fix the token**: wrong, expired or revoked |
| **403** on every line | The token is valid but not allowed | **Fix the token**: not a classic token (enterprise), missing scope, you are not an enterprise owner or billing manager, or SAML SSO is not authorised (the line says so when GitHub sent the SSO header). With `--org` a 403 on the seat and usage lines usually means the organization is billed through an enterprise: run with `--enterprise` |
| **404** on every line | Wrong slug, or the token cannot see it | Check the slug and the token owner's role. With `--org`, also try `--enterprise` |
| **200** on seats but **404** on the usage or metrics line | **An endpoint assumption is wrong**, not the token | See [API-ASSUMPTIONS.md](API-ASSUMPTIONS.md) (the enterprise usage and metrics paths are guesses) and `GitHubEndpoints.cs`; tell the developer the line you got |
| **403** on only the metrics line | The usage-metrics policy is off, or the token lacks access to metrics | Enable the policy; check the scope |

Exit code 0 means every endpoint answered 200. `--deep` also lists nested field names (names only; use it on a screen nobody else sees). Do not continue to step 5.4 until the seats and usage lines are 200.

## 2. Terraform state storage and the resource group  *(manual)*

Terraform remembers what it built in a **state file**. It lives in a storage account that you create by hand, **once, before the first pipeline run**, in its own resource group so that
it is never deleted together with the application. State can contain resource ids and settings, so the account has no public access, no shared keys (Entra only), versioning and soft delete.

Run these in a terminal (replace the placeholders; the storage account name must be globally unique, 3 to 24 lowercase letters and digits):

```bash
az login
az account set --subscription "<subscription name or id>"

LOCATION=westeurope            # the region you want everything in
STATE_RG=rg-copilot-tfstate
STATE_SA=<your unique name, e.g. sttfcopilot12345>
APP_RG=rg-copilot-usage        # the application's resource group (Terraform deploys into it, it does not create it)

# 2.1 Resource providers that Terraform will use (it is told not to register them itself, which would need subscription-wide rights). One-off.
for p in Microsoft.Web Microsoft.Storage Microsoft.KeyVault Microsoft.ManagedIdentity Microsoft.OperationalInsights Microsoft.Insights; do
  az provider register --namespace "$p" --wait
done

# 2.2 The application's resource group (empty)
az group create --name "$APP_RG" --location "$LOCATION"

# 2.3 The state storage account: LRS, TLS 1.2, no public blobs, no shared keys
az group create --name "$STATE_RG" --location "$LOCATION"
az storage account create --name "$STATE_SA" --resource-group "$STATE_RG" --location "$LOCATION" \
  --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 \
  --allow-blob-public-access false --allow-shared-key-access false

# 2.4 Versioning and soft delete (30 days) for blobs and containers, so a damaged or deleted state file can be recovered
az storage account blob-service-properties update --account-name "$STATE_SA" --resource-group "$STATE_RG" \
  --enable-versioning true \
  --enable-delete-retention true --delete-retention-days 30 \
  --enable-container-delete-retention true --container-delete-retention-days 30

# 2.5 The container, created through Azure Resource Manager (needs no storage key)
az storage container-rm create --storage-account "$STATE_SA" --resource-group "$STATE_RG" --name tfstate

# 2.6 Optional: stop anyone deleting the account by accident
az lock create --name keep-tfstate --lock-type CanNotDelete --resource-group "$STATE_RG" --resource-name "$STATE_SA" --resource-type Microsoft.Storage/storageAccounts
```

Write down `STATE_SA` (the account name) and `tfstate` (the container). The state **key** (file name) is `copilot-usage.tfstate`; you set it in step 3.5.

**Who needs which role on the state account.** With shared keys off, access is by Entra role only. Roles are granted at the **container**, the smallest scope:

| Who | Role | Scope | Why |
|---|---|---|---|
| The Azure DevOps service connection's identity (step 3.3) | **Storage Blob Data Contributor** | the `tfstate` container | The pipeline reads and writes state. Granted in step 3.4 |
| Anyone who will run Terraform by hand (optional) | **Storage Blob Data Contributor** | the container | Local `terraform init` / `plan` |
| Anyone who should only look | **Storage Blob Data Reader** | the container | Read the state |

State access is not needed by anyone else. The state also holds the Function's Application Insights connection string, so treat reading it as sensitive.

## 3. Azure DevOps setup  *(manual)*

### 3.1 Organization and project

Create (or use) an Azure DevOps organization and a project at <https://dev.azure.com>. The Basic plan is free for the first five users. See section 9 about the parallel-jobs grant: a brand-new organization may refuse to run Microsoft-hosted
pipelines until you request it (a form linked from the error message).

### 3.2 Connect the GitHub repository

Pipelines > **New pipeline** > **GitHub** > choose this repository > install and authorize the **Azure Pipelines** GitHub app when asked (needs a GitHub repository admin) > **Existing Azure Pipelines YAML file** (step 3.7 lists the three files). The code stays in GitHub; Azure Pipelines only reads it.
Optional but recommended: in GitHub, protect `main` and require the status check of the CI pipeline, so a pull request cannot merge on a red build.

### 3.3 The Azure service connection (workload identity federation, no secret)

Project settings > **Service connections** > **New service connection** > **Azure Resource Manager** > choose **Workload identity federation** (the "automatic" option is the simplest) > scope level **Resource group** > your subscription and `rg-copilot-usage` >
name it `copilot-usage-azure` (or anything; the name goes into the variable group in 3.5). Do **not** tick "Grant access permission to all pipelines": authorize each pipeline when it first asks.

This creates (or, with the manual option, lets the IT admin create) a **second Entra app registration, the deployment identity**, with a federated credential. It is separate from the web app's registration in step 4 and has no secret. Creating it needs directory rights
(the automatic option needs you to be allowed to create app registrations) and Owner on the resource group. *(Exact portal wording is from memory and may differ.)*

### 3.4 Azure roles for the deployment identity (least privilege)

The deployment identity needs **no Entra directory role** (Terraform here never reads or writes the directory). It needs only these Azure roles, and only on these scopes:

| Role | Scope | Why |
|---|---|---|
| **Contributor** | the resource group `rg-copilot-usage` | Create and change the app, plan, Function, storage, vault, logs. Also deploys the code. The automatic service connection normally grants this |
| **Role Based Access Control Administrator** | the same resource group | Terraform creates role assignments: the web app reading Key Vault secrets, the Function's identity using its storage, and (optionally) the people who may set the secret. Contributor cannot create role assignments. This is narrower than Owner |
| **Storage Blob Data Contributor** | the `tfstate` container (step 2) | Read and write the Terraform state |

Commands (find the identity's **application (client) id** on the service connection page under "Workload Identity federation details", or under Entra > App registrations):

```bash
DEPLOY_APP_ID=<application (client) id of the deployment identity>
SUB=$(az account show --query id -o tsv)

az role assignment create --assignee "$DEPLOY_APP_ID" --role "Role Based Access Control Administrator" \
  --scope "/subscriptions/$SUB/resourceGroups/$APP_RG"

az role assignment create --assignee "$DEPLOY_APP_ID" --role "Storage Blob Data Contributor" \
  --scope "$(az storage account show -n "$STATE_SA" -g "$STATE_RG" --query id -o tsv)/blobServices/default/containers/tfstate"
```

(Check the Contributor assignment exists: `az role assignment list --assignee "$DEPLOY_APP_ID" --scope "/subscriptions/$SUB/resourceGroups/$APP_RG" -o table`; add it with `--role Contributor` if the service connection did not.)
Role Based Access Control Administrator can be restricted further with an assignment condition (so it can only hand out the four roles Terraform uses). That is optional and the condition syntax is not
given here (see Microsoft's "delegate Azure role assignment management with conditions"). The simple, wider alternative is **User Access Administrator** on the resource group. Roles can take a few minutes to take effect.

### 3.5 The variable group

Pipelines > **Library** > **+ Variable group**, name it exactly **`copilot-usage`**. None of these is a secret, so none needs the padlock. Do **not** add the GitHub token or any other secret.

| Variable | Required | Used by | Meaning and example |
|---|---|---|---|
| `AZURE_SERVICE_CONNECTION` | yes | infrastructure, deploy | Name of the service connection from 3.3, e.g. `copilot-usage-azure` |
| `TFSTATE_STORAGE_ACCOUNT` | yes | infrastructure | State account from step 2 |
| `TFSTATE_CONTAINER` | yes | infrastructure | `tfstate` |
| `TFSTATE_KEY` | yes | infrastructure | State file name, `copilot-usage.tfstate` |
| `TF_RESOURCE_GROUP` | yes | infrastructure, deploy | Application resource group, `rg-copilot-usage` |
| `TF_NAME_PREFIX` | yes | infrastructure | 3 to 12 lowercase letters or digits, e.g. `copilotusage` |
| `TF_GITHUB_ENTERPRISE` | one of these two | infrastructure | The GitHub enterprise slug, when the enterprise holds the Copilot billing (enterprise scope, section 1) |
| `TF_GITHUB_ORG` | one of these two | infrastructure | The GitHub organization login, for a self-billed organization. If both are set the enterprise is used and the organization is ignored |
| `TF_ENTRA_TENANT_ID` | yes | infrastructure | Directory (tenant) ID from the app registration (step 4) |
| `TF_ENTRA_CLIENT_ID` | yes | infrastructure | Application (client) ID of the web app registration (step 4) |
| `WEBAPP_NAME` | for deploy | deploy | Web app name: an output of the infrastructure run (step 5.2) |
| `FUNCTIONAPP_NAME` | for deploy | deploy | Function app name: an output of the infrastructure run (step 5.2) |
| `TF_LOCATION` | no | infrastructure | Region; default is the resource group's |
| `TF_ENTRA_APP_ID_URI` | no | infrastructure | Only if the Application ID URI is not `api://<client id>` |
| `TF_CREDITS_PER_SEAT` | no | infrastructure | Default 1900 |
| `TF_SYNC_INTERVAL_HOURS` | no | infrastructure | 1, 2, 3, 4, 6, 8 or 12; default 4. Drives both the schedule and the staleness check |
| `TF_WEB_SKU` | no | infrastructure | Default `F1`. See section 8 for moving to a paid plan |
| `TF_WEB_PUBLIC_BASE_URL` | no | infrastructure | Only with a custom domain on a paid plan |
| `TF_ENABLE_MONITORING` | no | infrastructure | `false` removes Log Analytics and Application Insights |
| `TF_DISABLE_BASIC_AUTH_PUBLISHING` | no | infrastructure | Default `true` (no publishing passwords). Set `false` only if a deployment is refused by the SCM site (section 9, risk 5) |
| `TF_KEY_VAULT_SECRETS_OFFICER_OBJECT_IDS` | no | infrastructure | Comma-separated object ids of the people or groups who may set the GitHub token secret in Key Vault (step 5.4). Find yours with `az ad signed-in-user show --query id -o tsv` |

A pipeline that finds a required variable missing stops in its first step and names it. Neither of the two GitHub variables is required on its own: Terraform refuses the plan unless at least one is given.

### 3.6 The environment with an approval

Pipelines > **Environments** > **New environment**, name exactly **`copilot-usage-infrastructure`**, resource **None**. Open it > **Approvals and checks** > **+** > **Approvals** > add yourself and/or another person
(untick "Allow approvers to approve their own runs" if you want a second pair of eyes). The infrastructure pipeline's Apply stage waits here until someone approves, so nothing changes in Azure without a human saying yes.

### 3.7 Create the three pipelines

For each, Pipelines > **New pipeline** > GitHub > this repository > **Existing Azure Pipelines YAML file** > branch `main` > the path > **Save** (not Run yet). Rename each after saving.

| Name | YAML file | Runs |
|---|---|---|
| `copilot-usage-ci` | `/azure-pipelines/ci.yml` | on pull requests and on pushes to `main`: restore, build with warnings as errors, test, Terraform format and validate |
| `copilot-usage-infrastructure` | `/azure-pipelines/infrastructure.yml` | by hand, and when `infra/` changes on `main`: plan, then (after approval) apply the saved plan |
| `copilot-usage-deploy` | `/azure-pipelines/deploy.yml` | by hand, on `main`: build, test, deploy the web app and the Function, smoke-check `/healthz` |

The first time a pipeline uses the variable group, the service connection or the environment, Azure DevOps asks you to **Permit** it: click it. Task versions in the YAML are major versions (for example `AzureCLI@2`), which is how Azure DevOps pins them.
The pipelines use only built-in tasks and plain scripts; **no marketplace extension is needed**. The pipeline files also install Terraform 1.17.2 from HashiCorp and verify its checksum.

## 4. The Entra app registration  *(manual, IT admin)*

The IT admin creates the web app's registration **precisely as [AUTH.md](AUTH.md) describes**: single tenant, Web platform, ID-token flow, no secret, Application ID URI `api://<client-id>`, and the `Sync.Run` application role.
Read section 1 of AUTH.md and do it; this runbook does not repeat the settings so the two cannot drift apart.

Two things differ from a stand-alone setup:

- **Leave the redirect URIs for step 5.3.** They contain the web app's address, which exists only once Terraform has created it. (Entra requires at least one for a Web platform in some portal flows: add a placeholder such as `https://localhost/signin-oidc` and replace it in 5.3. *Unverified.*)
- Hand back to the owner: the **Directory (tenant) ID** and the **Application (client) ID**. They go into the variable group as `TF_ENTRA_TENANT_ID` and `TF_ENTRA_CLIENT_ID` (step 3.5). They are identifiers, not secrets.

## 5. Create the infrastructure

### 5.1 Run the infrastructure pipeline  *(pipeline)*

Pipelines > `copilot-usage-infrastructure` > **Run pipeline** > branch `main`.

1. **Plan stage** (automatic): checks formatting, initializes Terraform against the state account, validates, and prints the plan in the log and saves it as the `tfplan` artifact. **Read the plan.** The first run should show only creations (about 15 resources) and no destroys.
2. **Apply stage** (waits for approval): approve it under the run's **Review** button. It applies **exactly the saved plan**, not a new one. If anything changed in Azure in between, Terraform refuses the stale plan and you run the pipeline again.

Expect the first Apply to take several minutes. A run with nothing to change skips Apply.

### 5.2 Copy the names into the variable group  *(manual)*

At the end of the Apply log is a block "Outputs for the manual steps". Copy into the `copilot-usage` variable group:
`web_app_name` as `WEBAPP_NAME` and `function_app_name` as `FUNCTIONAPP_NAME`. Keep the log page open: you need the other outputs in the next steps (they are also repeated by `terraform output` when run locally with state access).
The outputs, and what they are for:

| Output | Used for |
|---|---|
| `web_app_url` | Where the dashboard is |
| `entra_redirect_uris` | The two redirect URIs to register (5.3) |
| `function_identity_object_id`, `function_identity_client_id` | The app-role assignment (5.6). The web app already has them as `Sync__AllowedCallerObjectId` and `Sync__AllowedCallerClientId` |
| `key_vault_name`, `github_token_secret_name` | Setting the GitHub token (5.4) |
| `resource_group_name`, `web_app_name`, `function_app_name`, `application_insights_name` | Commands and logs |

### 5.3 Register the redirect URIs  *(manual, IT admin)*

On the app registration: **Authentication** > Web > redirect URIs. Replace any placeholder with the **two** `entra_redirect_uris` outputs (`https://<host>/signin-oidc` and `https://<host>/signout-callback-oidc`). Other settings stay as in [AUTH.md](AUTH.md) section 1.
Until this is done, sign-in fails with an Entra "redirect URI does not match" error.

### 5.4 Set the GitHub token in Key Vault  *(manual)*

Terraform created an **empty** vault and pointed the web app's `GitHub__Token` setting at a secret named `github-token`. The value is never in Terraform, in the state, in a variable or in a log; you set it by hand, **once**.
Until you do, the web app refuses to start and its log says the Key Vault reference could not be resolved. That is why this step comes **before the first deploy**.

You need the role **Key Vault Secrets Officer** on the vault. If you listed your object id in `TF_KEY_VAULT_SECRETS_OFFICER_OBJECT_IDS`, you already have it (allow a few minutes). Otherwise someone with Owner on the resource group grants it:

```bash
KV=<key_vault_name output>
ME=$(az ad signed-in-user show --query id -o tsv)
az role assignment create --assignee-object-id "$ME" --assignee-principal-type User --role "Key Vault Secrets Officer" \
  --scope "$(az keyvault show --name "$KV" --query id -o tsv)"
```

Set the secret. **Either** the portal (no terminal): Key Vault > Secrets > **Generate/Import**, name `github-token`, paste the token, Create. **Or** the command line, which prompts without echoing and **must** end with `--output none`
(otherwise Azure prints the secret value back):

```bash
read -rsp "GitHub token: " T; echo
az keyvault secret set --vault-name "$KV" --name github-token --value "$T" --output none
unset T
```

(The token is briefly visible in your own computer's process list while the command runs; use the portal on a shared machine.) Then check that it exists, without printing it: `az keyvault secret show --vault-name "$KV" --name github-token --query "{name:name,enabled:attributes.enabled}"`.

If the web app already started before the secret existed, restart it so it resolves the reference: `az webapp restart -g "$APP_RG" -n <web_app_name>`. The deploy in step 6 restarts it anyway.
Whether a Key Vault reference works on the Free plan is *unverified*: after the first start, the portal (web app > Settings > Environment variables) should show a green tick next to `GitHub__Token`. A red cross there means the reference did not resolve: check the secret name, the role *Key Vault Secrets User* on the vault for the web app's identity, and give role assignments a few minutes to take effect.

### 5.5 Check the infrastructure is what you expect  *(manual)*

In the portal, resource group `rg-copilot-usage` should hold: the web app and its plan (Free), the Function app and its plan (Flex Consumption), a storage account, a Key Vault, a managed identity, and (unless switched off) a Log Analytics workspace and an Application Insights resource.

### 5.6 Give the Function's identity the `Sync.Run` role  *(manual, IT admin)*

The web app only accepts the sync call from one identity: the Function's managed identity, and only if it holds the `Sync.Run` application role. Terraform cannot grant the role (it does not touch Entra), and **the Entra portal cannot assign an app role to a managed identity**, so use the Azure CLI as a **Cloud Application Administrator or Application Administrator** (see [AUTH.md](AUTH.md) section 2 for the roles needed and a PowerShell equivalent).

```bash
CLIENT_ID=<web app registration's application (client) id>          # same as TF_ENTRA_CLIENT_ID
MI_OBJECT_ID=<function_identity_object_id output>

# the web app registration's enterprise application (service principal); create it first if this prints nothing
API_SP_ID=$(az ad sp show --id "$CLIENT_ID" --query id -o tsv)
# az ad sp create --id "$CLIENT_ID"        # only if the line above found nothing

ROLE_ID=$(az ad sp show --id "$CLIENT_ID" --query "appRoles[?value=='Sync.Run'].id | [0]" -o tsv)
echo "service principal: $API_SP_ID   role: $ROLE_ID"      # both must be GUIDs; an empty role id means the Sync.Run role does not exist yet (step 4)

az rest --method POST \
  --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$MI_OBJECT_ID/appRoleAssignments" \
  --headers "Content-Type=application/json" \
  --body "{\"principalId\":\"$MI_OBJECT_ID\",\"resourceId\":\"$API_SP_ID\",\"appRoleId\":\"$ROLE_ID\"}"
```

Check it: `az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$MI_OBJECT_ID/appRoleAssignments" --query "value[].{role:appRoleId,resource:resourceDisplayName}"`
should list one entry. Assign it to nothing and nobody else.

Do this **before the Function's first scheduled run**. A managed identity's access token is cached by Azure for a long time (up to about a day, *unverified*), so a token fetched before the role existed keeps being refused with 403 until it expires. If it happens: wait, or restart the Function app (`az functionapp restart`).

## 6. Deploy the code and check it works

### 6.1 Run the deploy pipeline  *(pipeline)*

Make sure `WEBAPP_NAME` and `FUNCTIONAPP_NAME` are in the variable group (5.2) and the secret is set (5.4). Pipelines > `copilot-usage-deploy` > **Run pipeline** > `main`. It:

1. checks the branch and every required variable (before spending time on a build),
2. restores, builds with warnings as errors and runs the tests,
3. publishes the web app and the Function, passing the commit as `SourceRevisionId` so the footer and `/healthz` show what is deployed,
4. deploys the web app (zip deploy with the pipeline's Entra identity, no publishing password) and the Function (Flex Consumption),
5. smoke-checks `https://<host>/healthz` for up to 10 minutes: HTTP 200, database reachable, demo off, **and the version contains this commit**.

The smoke check fails the run with a message if the site does not come up. On the Free plan the first request wakes the app and creates the database, which takes a while.
The `deployWeb` and `deployFunction` run parameters let you deploy only one of the two.

### 6.2 First-run checklist  *(manual)*

The GitHub response shapes in this app were **never verified against the real API**, and a wrong field name does not raise an error: it shows as **zeros or empty lists**. Do not wait for the Function's first run to check them: sign in and press **Refresh** on the home page (it runs a sync
now, takes a minute or more), then check every item. If you cannot see the site, use the log stream (section 8).

**Real GitHub numbers**

1. **Home page says "Data as of" with a recent time**, and no red "Sync failed" or amber "Partly updated" banner. If there is one, read the message under it and the logs. The banner and the stored sync log name the failed step and the HTTP status and carry a hint (for example: *Seats step (seat summary) failed: GitHub answered HTTP 403 Forbidden. This organization may be billed through a GitHub Enterprise account ... set GitHub:Enterprise*). The **application log** has more: the request path (without query string or token), GitHub's own error text (truncated) and the same hint. A 403 or 404 on the seat or billing calls in organization scope means the organization is probably billed through an enterprise: set `TF_GITHUB_ENTERPRISE`. A 403 in enterprise scope points at the token type, scopes, role or SAML authorisation (section 1).
2. **Seat count** on the home page ("N seats x 1,900 credits") equals the number of Copilot seats in the GitHub organization settings (Settings > Copilot > Access), or, in enterprise scope, the number of distinct users with a seat across the enterprise (a user with seats through two organizations counts once). If it is 0 or off: `GitHubMappings.cs` (seat summary mapping) and `Dtos/Dtos.cs` (`SeatBillingDto`).
3. **Pool limit** equals seats x credits per seat (1,900 for Copilot Business). If your plan differs, set `TF_CREDITS_PER_SEAT` and run the infrastructure pipeline.
4. **Credits are non-zero** once people have used Copilot this month, and the total roughly matches GitHub's billing page (Billing > Usage). All zeros while GitHub shows usage means the credit response fields do not match: `GitHubEndpoints.cs` (the `ai_credit/usage` path), `Dtos/Dtos.cs` (`CreditUsageResponseDto`), `GitHubMappings.cs` (`ToCreditRecords`).
5. **Per-user figures**: the leaderboard and `/users` show credits per person, and one user page matches what GitHub shows for that person. Zeros here with a non-zero pool point at the per-user call (same three files).
6. **Per-model figures**: the "Model split" card lists real model names, and a user page has a model breakdown. If it says "No model usage" or "unknown", check the `model` / `sku` fields in `Dtos/Dtos.cs` and `GitHubMappings.cs`.
7. **Activity data**: user pages show prompts, acceptance rate, features and IDEs. Empty means the usage-metrics report was not read: `GitHubEndpoints.cs` (report paths), `Dtos/Dtos.cs` (the `UserMetricsLineDto` and totals types), and that the "Copilot usage metrics" policy is on.
8. **Health endpoint**: open `https://<host>/healthz` (anonymous). Expect `"status":"ok"`, `"databaseReachable":true`, `"lastSuccessfulSync"` set, `"demo":false`. `degraded` means the app works but the data is missing, stale (older than three sync intervals) or the last sync failed.
9. **Sync log** (section 8): look for `Sync N succeeded: Seats=Succeeded(...), OrgCreditsByDay=..., UserCreditsByModel=..., UserMetrics=...` with non-zero row counts for each step. A step with `0 rows` and no error is the same "wrong field name" symptom as above.
10. **Footer** shows the version and commit you just deployed (`Version 1.0.0 (abc1234)`).

Where to correct things (all under `src/CopilotUsage.GitHub/`): `GitHubEndpoints.cs` for paths and query strings, `Dtos/Dtos.cs` for JSON field names, `GitHubMappings.cs` for how fields become credits.
After a change: `dotnet test`, merge to `main`, run the deploy pipeline. Do not edit the live site.

**Sign-in**

11. **Sign-in works.** Open the site in a private window: it must redirect to `login.microsoftonline.com`. Sign in with a work account from your tenant: you land on the dashboard and your name shows in the header. Sign out: you reach the signed-out page.
12. **Someone outside the tenant is refused.** In a private window sign in with an account from another tenant or a personal Microsoft account (for example an outlook.com address). Expected: Entra refuses it itself (a single-tenant app does not accept it), or the app shows its "access denied" page. They must **never** see the dashboard. The web app's log has a `Sign-in refused` or `Sign-in failed` line.
    A **guest** invited into your tenant is let in unless the enterprise application has **Assignment required** set (see [AUTH.md](AUTH.md)).
13. **The sync endpoint is closed to everyone else.** `curl -i -X POST https://<host>/api/sync` must answer **401**, not 200.

**The Function**

14. **Its first call succeeded.** The timer fires at 00:00, 04:00, 08:00, 12:00, 16:00 and 20:00 UTC (`SyncSchedule`, `0 0 */4 * * *`). After the first one, look in either place:
    - **The dashboard**: `/healthz` shows `lastSyncAt` moving to the time of the call, and the home page's "Data as of" changes without you pressing Refresh.
    - **Application Insights** (portal: resource group > the Application Insights resource > **Logs**), if monitoring is on. Allow a few minutes for data to arrive:
      ```
      requests | where name == "SyncTimer" | project timestamp, success, resultCode, duration | order by timestamp desc
      ```
      ```
      traces | where timestamp > ago(1d) | where message has "Sync" or message has "web app" | project timestamp, severityLevel, message | order by timestamp desc
      ```
      A good run logs `Sync succeeded. Attempts: 1. Took ...s Steps: ...`. `The sync is still running inside the web app` (a 202) is also fine: the sync carries on in the app. `exceptions` and severity 3 traces are failures.
    You do not have to wait for the next boundary: in the portal, Function app > Functions > `SyncTimer` > **Code + Test** > **Test/Run** may run it now (*unverified for Flex Consumption*).
15. **What a 401 or 403 from `/api/sync` means.** The Function logs "The web app refused the call (HTTP 401|403). Configuration error". The **reason is only in the web app's log** (the response carries none, on purpose). Read it with
    `az webapp log tail -g "$APP_RG" -n <web_app_name>` (or the portal's Log stream) and look for these lines:

    | Web app log line | Meaning | Fix |
    |---|---|---|
    | `Sync token rejected during validation (...)` followed by `no authenticated caller` (401) | The token did not validate: wrong audience, wrong tenant, expired, or not a token for this app | `SyncApi__Scope` must be `api://<client id>/.default` (output `sync_token_scope`); the tenant id must match |
    | `token lacks the Sync.Run role` (403) | The role was not assigned to the Function's identity, or the identity holds an older cached token | Step 5.6. If just done, wait or restart the Function app |
    | `caller object id is not the allowed one` (403) | The token is from another identity than `Sync__AllowedCallerObjectId` | The Function's identity was replaced: run the infrastructure pipeline so the web app learns the new id, then redo 5.6 for it |
    | `caller client id is not the allowed one` (403) | `Sync__AllowedCallerClientId` differs from the token's `azp`/`appid` | Same as above |
    | `token is a delegated (user) token` / `not an app-only token` | Someone called with a user token | Not the Function |
    | `no allowed caller is configured` | `Sync__AllowedCallerObjectId` empty | Run the infrastructure pipeline |

    On the **Free plan**, a plain **403** can also be Azure itself refusing the request because the app's daily CPU quota is used up (section 8). The web app's log then has **no** "Sync call refused" line at all.
    The Function's own log line `Could not get an access token for the web app` means the managed identity could not get a token for the scope at all (identity not attached, wrong scope, or the Application ID URI differs: set `TF_ENTRA_APP_ID_URI`).

## 7. Protect the dashboard network-side: Conditional Access and Global Secure Access  *(manual, IT admin)*

> **Read this first.** These steps were written **without access to Microsoft's current documentation** and from the author's memory of how the features work. Portal names, option names, licence requirements and preview/general-availability status change often.
> The admin must verify every statement against Microsoft's current documentation for Conditional Access and Global Secure Access before relying on it, and should test in **report-only** mode first.

**What this does and does not do.** Conditional Access decides whether a **sign-in** is allowed. It does **not** close the network: the site stays reachable on the internet from anywhere, and anyone can load its (sign-in redirect) page. Without a sign-in that passed the policy they see **nothing** from the dashboard,
and the data is never sent. Treat it as strong protection of the data, not as a firewall. Nothing was built into the app or Azure for IP filtering.

**The Function is unaffected.** It signs in as a **workload** (a managed identity), not as a user, and user-scoped Conditional Access policies do not apply to it. (As understood: managed identities are not evaluated by Conditional Access. If your tenant targets workload identities with a policy, check that it does not include this one.)

**Licences, as understood (verify).** Conditional Access requires **Microsoft Entra ID P1** (included in Microsoft 365 E3/E5 and Business Premium) for the users covered. Global Secure Access and its **compliant network** check require a **Microsoft Entra Internet Access** (or Private Access) licence, or a suite that includes it (for example **Microsoft Entra Suite**),
and the **Global Secure Access client** on the users' devices. Which traffic-forwarding profile must be on for a custom web app to count as covered by the compliant-network signal is something the author is unsure of.

**Steps**

1. **Is there already a tenant-wide policy?** In Entra admin center > Protection > **Conditional Access** > Policies, look for a policy that applies to **All resources** (formerly "All cloud apps") and requires the compliant network (for example: Locations: include any location, exclude **All Compliant Network locations**; Grant: Block).
   - If yes: open it and **confirm this app is not in its Exclude list**. Then you are done: the app is covered. Check with the **What If** tool, picking a user, this app and a location outside the network.
   - If it targets named apps only: **add this app** (the enterprise application named like the app registration) to it.
2. **Otherwise create a policy** (start with **Report-only**):
   - Users: the people who should use the dashboard (or all users). **Exclude** your emergency (break-glass) accounts.
   - Target resources: **Select resources**, this app.
   - Conditions > Locations: Include **Any location**; Exclude **All Compliant Network locations**.
   - Grant: **Block access**.
3. **The office IP, only if needed.** If office machines do **not** run the Global Secure Access client, add the office public IP range as a **trusted named location** (Conditional Access > Named locations) and also **exclude** that location in the policy, so office users are not blocked. If they do run the client, do not add the IP range: the compliant network signal is the stronger one.
4. Check sign-in logs (Entra > Sign-in logs > Conditional Access tab) for your own sign-in and for one from outside; when they show what you expect, switch the policy from Report-only to **On**.
5. Repeat the checklist items 11 and 12 (section 6.2) from inside and from outside the network.

## 8. Operating notes

### The Free plan (F1), in plain terms

These are from the author's understanding of the Free plan; anything marked *unverified* the admin should check against the App Service plan comparison page.

- **It sleeps.** After about 20 minutes without a request the app unloads. The next visitor, or the Function's next call, waits for a **cold start** (several seconds, longer when the database is migrated). Always On is not available on Free, which is why the schedule lives in the Function.
- **CPU quota: 60 CPU-minutes a day** (a daily allowance of processor time, not clock time). Syncing and page loads use some. When it is used up, Azure **stops the app until the quota resets** (visitors get an error page, usually HTTP 403 "quota exceeded"; *unverified*) and **the Function's call fails with 403 as well**: it looks like an authorization error but is not (see item 15 of the checklist). The dashboard is for a handful of people, so this is unlikely; the daily figure is on the portal's web app page under Quotas / "CPU time (Free)" *(unverified location)*.
- **5 concurrent WebSocket connections.** The dashboard is Blazor Server: every open browser tab holds one WebSocket. With more than about five open at once, further tabs cannot connect: they show the "reconnecting" dialog and then the "could not reconnect" state, and a reload may help once someone closes a tab *(exact behaviour unverified)*. Fine for a team of two or three, not for an office. That, and the CPU quota, are the usual reasons to move up (below).
- **1 GB memory and 1 GB disk.** The SQLite file is small (megabytes), and so are the logs (filesystem logging is capped).
- **No custom domain with TLS, no deployment slots, no scale-out, no virtual network integration, no health-check eviction, no SLA.** (Custom domains on Free: not available as far as the author knows; *unverified*.) The site uses the `*.azurewebsites.net` address.
- **One instance only, always.** SQLite has one writer. Never raise the instance count. The Function's sync is also single (an overlapping call is answered 409).

### Logs

- **Web app**: portal > the web app > Monitoring > **Log stream**, or `az webapp log tail -g "$APP_RG" -n <web_app_name>`. Logs never contain the token or Authorization headers. Look for `CopilotUsage.Web.Sync.SyncService` lines and `Sync call refused` / `Sign-in refused` lines. A failed GitHub call logs one `GitHub call ... failed: GET <path> returned HTTP <status> ...` line from `CopilotUsage.GitHub.GitHubClient` with the path, GitHub's own message (truncated, token removed) and a hint.
- **Function**: Application Insights > **Logs** (queries in 6.2, item 14). Without monitoring: the Function app > Monitor in the portal shows only recent invocations *(unverified for Flex)*.

### When the dashboard looks stale

1. Open `/healthz`: `lastSuccessfulSync` older than three intervals (12 hours) makes it `degraded`; `lastSyncStatus` says whether the last sync `Failed` or was a `PartialFailure`.
2. Did the Function run? Application Insights query in 6.2 item 14 (or the dashboard's `lastSyncAt`).
3. If the Function ran but got 401/403: item 15 of the checklist (including the Free-plan CPU quota).
4. If the sync reached the app but failed: the home page banner and the web app log show the GitHub error. 401 means the token is wrong or expired (or the Key Vault reference did not resolve), 403 a missing scope or rate limit, 404 a wrong path or the usage-metrics policy being off.
5. Press **Refresh** on the home page to run a sync right now, which separates "the Function did not call" from "the sync itself fails".
6. A deployment restarts the app for a few seconds; a stale reading right after a deploy usually fixes itself at the next run.

### Backing up the database and the keys

The data is a single SQLite file, `/home/data/copilot-usage.db` (default journal mode, not WAL, so one file). GitHub only returns the current month, so **earlier months cannot be rebuilt: back the file up.**
Stop the app so nothing is writing, copy the file, start it again (*the Kudu download is unverified with basic-auth publishing disabled*; it uses your Entra token):

```bash
az webapp stop  -g "$APP_RG" -n <web_app_name>
TOKEN=$(az account get-access-token --resource https://management.azure.com --query accessToken -o tsv)
curl -H "Authorization: Bearer $TOKEN" -o copilot-usage-backup.db "https://<web_app_name>.scm.azurewebsites.net/api/vfs/data/copilot-usage.db"
az webapp start -g "$APP_RG" -n <web_app_name>
```

Store the copy somewhere private; it holds user logins and usage. To restore, upload the file to the same path (Kudu file browser at `https://<web_app_name>.scm.azurewebsites.net`, signed in with your Entra account) while the app is stopped.

The **data-protection keys** are in `/home/data/dataprotection-keys`. Backing them up is **optional**: if they are lost, everyone simply signs in again. They are **sensitive**: they are stored unencrypted, and anyone who can read them can forge a valid session cookie.
Who can read them: anyone who can open the web app's Kudu/SCM site or file share, which in practice means anyone with **Contributor or Owner on the resource group** (or a custom role with web app publishing rights). Keep that list short, and do not put the keys into a widely readable backup.

### Rotating the GitHub token

Create the new token (step 1), then set a new secret version and restart so the app re-reads it. You need the role Key Vault Secrets Officer on the vault (step 5.4):

```bash
read -rsp "New GitHub token: " T; echo
az keyvault secret set --vault-name "$KV" --name github-token --value "$T" --output none; unset T
az webapp restart -g "$APP_RG" -n <web_app_name>
```

Without the restart the app picks the new version up on its own within about a day. Then press Refresh on the home page and check that no sync banner appears. Revoke the old token on GitHub. The token lives only in Key Vault: nothing in Terraform, the pipelines or Azure DevOps needs updating.

### Demo mode

Demo mode shows synthetic data and never calls GitHub (`Demo__Enabled=true`). The app protects real data from it, judging by the sync log stored inside the database, not by file names:

- **Demo on, database holds data from a real sync:** the app **refuses to start** and says how to fix it. Nothing is deleted. The Refresh button is refused the same way.
- **Demo on, database empty or holding only demo data:** it is filled with demo data.
- **Demo off, database holds only demo data:** at startup the app clears the demo rows (a warning line says so), so synthetic users never appear beside real ones.

To look at demo data on the production web app, use a separate file and expect Terraform to put things back at the next apply (it owns these settings):

```bash
az webapp config appsettings set -g "$APP_RG" -n <web_app_name> --settings Demo__Enabled=true "ConnectionStrings__Default=Data Source=/home/data/demo.db"
```

Demo data is always worded as an organization, even if `GitHub__Enterprise` is set. (`Demo__Scenario` can be `normal`, `over` or `empty`.) A "Demo data" badge appears. To go back, run the infrastructure pipeline (it detects the drift and shows it in the plan; approve it), or set `Demo__Enabled=false` and `ConnectionStrings__Default=Data Source=/home/data/copilot-usage.db` by hand.

### Changing settings

`GitHub` org, credits per seat and the sync interval are Terraform variables (section 3.5): change the variable, run the infrastructure pipeline, approve. Editing settings by hand in the portal works until the next apply, which resets them to what Terraform says.
The security headers and Content-Security-Policy are in `src/CopilotUsage.Web/Hosting/SecurityHeaders.cs`; the setting `Security__ContentSecurityPolicy` replaces the policy temporarily (an empty value removes it).

### When to move to a paid plan

Move up when the 5-WebSocket limit, the 60 CPU-minute quota or the cold starts get in the way, or when you need a custom domain. It is **one variable**: set `TF_WEB_SKU` in the variable group to `B1` (or another size from the list in `infra/variables.tf`) and run the infrastructure pipeline.
Terraform then also switches on **Always On** and the **`/healthz` health check** automatically, because the Free plan cannot. Expect a monthly charge for the plan (B1 is roughly 10 to 15 USD, *unverified*). The app must still run on **exactly one instance**; do not add scale-out.
With a custom domain also set `TF_WEB_PUBLIC_BASE_URL` and register that domain's two redirect URIs on the app registration. Going back down is the same variable. The Function stays as it is.

## 9. Risks for the first real run, in the order they are likely to bite

1. **No hosted parallelism in Azure DevOps.** A new organization may have zero free Microsoft-hosted parallel jobs: the first run then waits or fails with a message about parallelism and a link to the free-grant request form (can take a couple of business days). Alternative: a self-hosted agent (change `pool:` in the YAML).
2. **Service connection and permissions.** The automatic federated connection needs directory rights to create its app registration, and the role assignments in 3.4 can take minutes to apply. Typical symptoms: `AuthorizationFailed` on creating a role assignment (missing Role Based Access Control Administrator), or a 403 reading the state blob (missing Storage Blob Data Contributor on the container).
3. **Resource providers not registered** (step 2.1) or the **region lacking Flex Consumption or the Free Linux plan**: plan or apply fails with a SKU, quota or region message. Set `TF_LOCATION` to another region. Free plan quota is limited per region and subscription.
4. **`terraform plan` differences from the offline check.** The offline `terraform test` evaluates the configuration against a mock provider; it cannot know what Azure accepts. The attributes most likely to be refused by the real API: the Flex `runtime_version = "10.0"` and `maximum_instance_count = 40`, Linux `use_32_bit_worker` on F1, and `health_check_path` on paid plans.
5. **Function deployment.** The Flex deployment task input names (`isFlexConsumption`) and whether the deployment works with basic-auth publishing disabled are unverified. If the Function deploy step fails with 401/403 from the SCM site, set `TF_DISABLE_BASIC_AUTH_PUBLISHING` to `false` in the variable group, run the infrastructure pipeline and approve, then deploy again. For the web app, `az webapp deploy` is expected to work with an Entra token; if not, the same switch applies.
6. **The web app does not start** (smoke check fails): almost always the GitHub token secret not set yet or the Key Vault reference unresolved (step 5.4), or `AzureAd__*` / `Sync__AllowedCallerObjectId` missing (the app names each missing key in its log).
7. **Sign-in error AADSTS50011 (redirect URI mismatch)**: step 5.3 not done, or the URIs differ from the outputs (including the exact host name).
8. **Function gets 403** after step 5.6 was done late (cached token), or the Free-plan CPU quota (section 8).
9. **The GitHub endpoints and field names** are unverified assumptions (checklist items 2 to 9): zeros, not errors. In enterprise scope only the seats endpoint is documented; the usage and metrics paths are guesses (run `scripts/check-github-token.sh`, section 1.1, before deploying).
10. **Soft-deleted Key Vault.** If you ever destroy and re-create the infrastructure, the vault name is held for the soft-delete period (7 days): purge it with `az keyvault purge --name <name>` or use a different `TF_NAME_PREFIX`.
