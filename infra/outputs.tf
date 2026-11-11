# Everything the manual steps in docs/DEPLOY.md need. None of these is a secret.

output "resource_group_name" {
  description = "Resource group everything lives in."
  value       = data.azurerm_resource_group.main.name
}

output "web_app_name" {
  description = "Web app name (variable WEBAPP_NAME for the deploy pipeline)."
  value       = azurerm_linux_web_app.web.name
}

output "web_app_url" {
  description = "Public address of the dashboard."
  value       = "https://${azurerm_linux_web_app.web.default_hostname}"
}

output "entra_redirect_uris" {
  description = "Register BOTH as Web redirect URIs on the Entra app registration (docs/AUTH.md, section 1)."
  value = [
    "https://${azurerm_linux_web_app.web.default_hostname}/signin-oidc",
    "https://${azurerm_linux_web_app.web.default_hostname}/signout-callback-oidc",
  ]
}

output "function_app_name" {
  description = "Function app name (variable FUNCTIONAPP_NAME for the deploy pipeline)."
  value       = azurerm_function_app_flex_consumption.sync.name
}

output "function_identity_object_id" {
  description = "Object (principal) ID of the Function's managed identity. Needed to assign the Sync.Run app role (docs/DEPLOY.md, step 5). Already set on the web app as Sync__AllowedCallerObjectId."
  value       = azurerm_user_assigned_identity.sync.principal_id
}

output "function_identity_client_id" {
  description = "Client (application) ID of the Function's managed identity."
  value       = azurerm_user_assigned_identity.sync.client_id
}

output "sync_token_scope" {
  description = "Scope the Function requests a token for (SyncApi__Scope)."
  value       = "${local.app_id_uri}/.default"
}

output "key_vault_name" {
  description = "Key Vault that must hold the secret github-token."
  value       = azurerm_key_vault.main.name
}

output "github_token_secret_name" {
  description = "Name of the secret to create in the vault."
  value       = local.token_secret_name
}

output "application_insights_name" {
  description = "Application Insights resource holding the Function's logs (null when monitoring is off)."
  value       = one(azurerm_application_insights.main[*].name)
}

output "log_analytics_workspace_name" {
  description = "Log Analytics workspace behind it (null when monitoring is off)."
  value       = one(azurerm_log_analytics_workspace.main[*].name)
}

output "function_storage_account_name" {
  description = "Storage account used by the Function (not the Terraform state account)."
  value       = azurerm_storage_account.function.name
}

output "web_sku" {
  description = "Plan the web app runs on."
  value       = var.web_sku
}
