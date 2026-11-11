# Copilot Usage Dashboard: Azure infrastructure.
#
# Layout:  main.tf (shared names, identity)   web.tf (App Service)   function.tf (sync Function + its storage)
#          keyvault.tf (GitHub token)         monitoring.tf (logs)   outputs.tf
#
# Nothing here creates Microsoft Entra objects and nothing here reads the directory: the app registration is made by hand
# (docs/AUTH.md) and its ids arrive as variables. The deploying identity therefore needs Azure roles only (docs/DEPLOY.md, step 3).
#
# COST (details and caveats in docs/DEPLOY.md): everything here is designed to cost about nothing at one short sync call every
# four hours. The expected monthly cost is zero to a few cents, see the table in the runbook.

# The resource group is created by hand and only read here. Why: creating a group needs rights on the whole subscription, and
# deleting one by accident (terraform destroy) would also delete the Key Vault and the web app's data. With an existing group the
# pipeline's identity can be limited to that one group.
data "azurerm_resource_group" "main" {
  name = var.resource_group_name
}

locals {
  location = coalesce(var.location, data.azurerm_resource_group.main.location)

  # Short, stable suffix so globally unique names (web app host name, storage account, Key Vault) do not collide with other people's.
  suffix = substr(sha1(data.azurerm_resource_group.main.id), 0, 5)

  web_app_name      = "app-${var.name_prefix}-${local.suffix}"
  function_app_name = "func-${var.name_prefix}-${local.suffix}"
  key_vault_name    = "kv-${var.name_prefix}-${local.suffix}" # 3 to 24 characters: 3 + 12 + 1 + 5 = 21 at most
  storage_name      = "st${var.name_prefix}${local.suffix}"   # 3 to 24 lowercase alphanumerics: 2 + 12 + 5 = 19 at most
  identity_name     = "id-${var.name_prefix}-sync"

  token_secret_name = "github-token"

  tags = merge({ application = "copilot-usage-dashboard" }, var.tags)

  # The Function asks Entra for a token for this scope; the web app accepts audience = client id or the Application ID URI.
  app_id_uri = var.entra_app_id_uri != "" ? var.entra_app_id_uri : "api://${var.entra_client_id}"
}

# The Function's identity is created FIRST and on its own. That breaks what would otherwise be a dependency cycle:
#   the web app needs the Function identity's object id (Sync__AllowedCallerObjectId),
#   the Function needs the web app's URL (SyncApi__BaseUrl).
# With a user-assigned identity the web app depends on the identity, and the Function depends on both; nothing depends back.
# It also gives the object id and client id before the Function exists, which is what the Entra app-role assignment needs.
resource "azurerm_user_assigned_identity" "sync" {
  name                = local.identity_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  tags                = local.tags
}
