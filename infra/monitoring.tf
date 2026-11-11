# Logging for the Function, so you can see whether it ran. Optional (var.enable_monitoring).
#
# Cost position: a Log Analytics workspace on the Pay-as-you-go ("PerGB2018") tier includes 5 GB of ingestion a month and 30 days of
# retention at no charge (as understood by the author, verify on the Azure pricing page). This Function writes a few log lines six times a day,
# well under 1 MB a day. The daily cap below (default 0.1 GB, about 3 GB a month at the very most) is a hard stop that keeps even a
# runaway logging loop inside the free allowance. Application Insights itself has no charge: it bills through the workspace.
# The web app is not connected: it has no Application Insights SDK, and its log stream already shows its logs (docs/DEPLOY.md).

resource "azurerm_log_analytics_workspace" "main" {
  count = var.enable_monitoring ? 1 : 0

  name                = "log-${var.name_prefix}"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  sku                 = "PerGB2018"
  retention_in_days   = var.log_retention_days
  daily_quota_gb      = var.log_daily_cap_gb
  tags                = local.tags
}

resource "azurerm_application_insights" "main" {
  count = var.enable_monitoring ? 1 : 0

  name                 = "appi-${var.name_prefix}"
  resource_group_name  = data.azurerm_resource_group.main.name
  location             = local.location
  application_type     = "web"
  workspace_id         = azurerm_log_analytics_workspace.main[0].id
  retention_in_days    = var.log_retention_days
  daily_data_cap_in_gb = var.log_daily_cap_gb
  tags                 = local.tags
}

locals {
  application_insights_connection_string = one(azurerm_application_insights.main[*].connection_string)
}
