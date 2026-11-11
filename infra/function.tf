# The sync Azure Function: a timer that calls POST /api/sync on the web app with a token from its managed identity.
#
# PLAN CHOICE: Flex Consumption (SKU FC1), not the classic Consumption plan.
#   - Classic Linux Consumption is on a retirement path, and the author could not confirm that it supports .NET 10 isolated. Flex Consumption
#     supports .NET 10 isolated (runtime_version "10.0"; the provider is expected to reject it at validate time if not).
#   - Flex Consumption uses no storage keys and no Azure Files content share: the host and the deployment package use the Function's
#     managed identity against a blob container. So the storage account can have shared-key access switched off and no secret exists anywhere.
#   - Cost: pay per execution with a monthly free grant (understood to be 250,000 executions and 100,000 GB-seconds per subscription;
#     verify on the pricing page). Always-ready instances are 0 (nothing is billed while idle). One run is at most a few minutes at 0.5 GB:
#     6 runs a day x 4 minutes x 0.5 GB is about 3,600 GB-seconds a month, a few percent of the grant. Expected cost: 0.
#   - Trade-off: the timer is only checked while an instance exists. Flex scales to zero and the platform wakes the app for the schedule;
#     if a run is ever missed, the `IsPastDue` warning is logged and /healthz on the web app shows the data as stale after three intervals.

# ---- Storage account the Function needs (host state and the deployment package) -------------------------------------------------

resource "azurerm_storage_account" "function" {
  name                = local.storage_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  tags                = local.tags

  account_kind             = "StorageV2"
  account_tier             = "Standard"
  account_replication_type = "LRS" # cheapest redundancy

  min_tls_version                 = "TLS1_2"
  https_traffic_only_enabled      = true
  allow_nested_items_to_be_public = false # no public blob access
  shared_access_key_enabled       = false # identity-based access only; no key exists to leak
  default_to_oauth_authentication = true
}

# Flex Consumption keeps the deployed package in a blob container.
resource "azurerm_storage_container" "deployment" {
  name                  = "app-package-${local.function_app_name}"
  storage_account_id    = azurerm_storage_account.function.id # management-plane creation: no storage key needed
  container_access_type = "private"
}

# What the Function's identity may do on its own storage account (host state, timer lease, package).
# Blob Data Owner is what the Functions host documents for blob-based coordination; the queue and table roles cover the host's
# internal use. Unverified whether the last two are strictly needed for a timer-only app; they are cheap and scoped to this account.
resource "azurerm_role_assignment" "function_storage" {
  for_each = toset([
    "Storage Blob Data Owner",
    "Storage Queue Data Contributor",
    "Storage Table Data Contributor",
  ])

  scope                            = azurerm_storage_account.function.id
  role_definition_name             = each.value
  principal_id                     = azurerm_user_assigned_identity.sync.principal_id
  principal_type                   = "ServicePrincipal"
  skip_service_principal_aad_check = true
}

# ---- The plan and the app ---------------------------------------------------------------------------------------------------------

resource "azurerm_service_plan" "function" {
  name                = "asp-${var.name_prefix}-func"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  os_type             = "Linux"
  sku_name            = "FC1" # Flex Consumption
  tags                = local.tags
}

resource "azurerm_function_app_flex_consumption" "sync" {
  name                = local.function_app_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  service_plan_id     = azurerm_service_plan.function.id
  tags                = local.tags

  https_only                                     = true
  webdeploy_publish_basic_authentication_enabled = !var.disable_basic_auth_publishing

  # Where the deployed package lives, and how the platform reads it: with the user-assigned identity, not a key.
  storage_container_type            = "blobContainer"
  storage_container_endpoint        = "${azurerm_storage_account.function.primary_blob_endpoint}${azurerm_storage_container.deployment.name}"
  storage_authentication_type       = "UserAssignedIdentity"
  storage_user_assigned_identity_id = azurerm_user_assigned_identity.sync.id

  runtime_name           = "dotnet-isolated" # the Function is a .NET 10 isolated worker (src/CopilotUsage.SyncFunction)
  runtime_version        = "10.0"
  instance_memory_in_mb  = var.function_instance_memory_mb
  maximum_instance_count = 40 # a timer runs once at a time regardless; this only caps scale-out of anything else. 40 is believed to be the minimum Flex accepts.

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.sync.id]
  }

  site_config {
    http2_enabled                          = true
    minimum_tls_version                    = "1.2"
    application_insights_connection_string = local.application_insights_connection_string
  }

  # FUNCTIONS_WORKER_RUNTIME and FUNCTIONS_EXTENSION_VERSION are deliberately absent: on Flex Consumption the runtime comes from
  # runtime_name above and the platform does not accept those two as app settings (as understood; unverified).
  app_settings = {
    # Host storage by identity instead of a connection string.
    "AzureWebJobsStorage__accountName" = azurerm_storage_account.function.name
    "AzureWebJobsStorage__credential"  = "managedidentity"
    "AzureWebJobsStorage__clientId"    = azurerm_user_assigned_identity.sync.client_id

    # The schedule: NCRONTAB, six fields. The setting must exist (a timer binding cannot default it).
    "SyncSchedule" = "0 0 */${var.sync_interval_hours} * * *"

    # The call to the web app (src/CopilotUsage.SyncFunction/SyncApiOptions.cs). Timeouts and retries keep their code defaults.
    "SyncApi__BaseUrl"                 = "https://${azurerm_linux_web_app.web.default_hostname}/"
    "SyncApi__Scope"                   = "${local.app_id_uri}/.default"
    "SyncApi__ManagedIdentityClientId" = azurerm_user_assigned_identity.sync.client_id
  }
}
