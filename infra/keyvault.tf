# Key Vault (standard tier) holding the GitHub token.
#
# The token value is NEVER in Terraform: no variable, no resource, therefore not in the state file or in a plan or pipeline log.
# Terraform creates the empty vault and points the web app's GitHub__Token setting at the secret by name (a Key Vault reference).
# A person then sets the secret value by hand (docs/DEPLOY.md, step 5). Until they do, the web app refuses to start and says why.

data "azurerm_client_config" "current" {}

resource "azurerm_key_vault" "main" {
  name                = local.key_vault_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"
  tags                = local.tags

  rbac_authorization_enabled = true # access is by Azure role assignment, not by access policy

  # Soft delete cannot be switched off. 7 days is the shortest retention. Purge protection is off so a mistaken
  # `terraform destroy` plus re-create with the same name is not blocked for 90 days; the secret is easy to re-create.
  soft_delete_retention_days = 7
  purge_protection_enabled   = false

  # The web app is on the Free plan, which has no virtual network integration, so the vault cannot be limited to a private network.
  public_network_access_enabled = true
}

# The web app's identity may read secrets in this vault, and nothing else.
resource "azurerm_role_assignment" "web_reads_secrets" {
  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = azurerm_linux_web_app.web.identity[0].principal_id
  principal_type       = "ServicePrincipal"

  # Do not look the identity up in Entra: Terraform has no directory permissions, and a fresh identity may not have replicated yet.
  skip_service_principal_aad_check = true
}

# People who may set or rotate the token. Optional (see the variable).
resource "azurerm_role_assignment" "secrets_officers" {
  for_each = toset(var.key_vault_secrets_officer_object_ids)

  scope                = azurerm_key_vault.main.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = each.value
}
