# Offline test (no Azure access, no credentials): `terraform test` in this directory after `terraform init`.
# The provider is mocked, so this checks the CONFIGURATION: app-setting key names (a misspelt key fails silently in the app),
# cross-references, the Free-plan rules, and that nothing secret is passed as a value.
# The same key names are also checked against the C# options classes by tests/CopilotUsage.Tests/Infra/InfraSettingsTests.cs.

mock_provider "azurerm" {
  mock_data "azurerm_resource_group" {
    defaults = {
      id       = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-test"
      location = "westeurope"
    }
  }
  mock_data "azurerm_client_config" {
    defaults = {
      tenant_id = "11111111-1111-1111-1111-111111111111"
    }
  }
}

variables {
  resource_group_name = "rg-test"
  name_prefix         = "copilotusage"
  github_org          = "acme"
  entra_tenant_id     = "11111111-1111-1111-1111-111111111111"
  entra_client_id     = "22222222-2222-2222-2222-222222222222"
}

run "defaults_are_free_and_correct" {
  command = plan

  assert {
    condition     = azurerm_service_plan.web.sku_name == "F1" && azurerm_service_plan.web.os_type == "Linux"
    error_message = "The web app must default to the Free (F1) Linux plan."
  }

  assert {
    condition     = azurerm_linux_web_app.web.site_config[0].always_on == false
    error_message = "F1 does not support Always On; it must be false."
  }

  assert {
    condition     = azurerm_linux_web_app.web.site_config[0].health_check_path == null
    error_message = "No health-check path on the Free plan."
  }

  assert {
    condition     = azurerm_linux_web_app.web.https_only && azurerm_linux_web_app.web.site_config[0].minimum_tls_version == "1.2" && azurerm_linux_web_app.web.site_config[0].ftps_state == "Disabled" && azurerm_linux_web_app.web.site_config[0].websockets_enabled
    error_message = "Web app hardening settings changed."
  }

  assert {
    condition = toset(keys(azurerm_linux_web_app.web.app_settings)) == toset([
      "AzureAd__TenantId", "AzureAd__ClientId",
      "Sync__AllowedCallerObjectId", "Sync__AllowedCallerClientId", "Sync__IntervalHours",
      "GitHub__Org", "GitHub__Token",
      "Copilot__CreditsPerSeat", "Demo__Enabled",
      "ConnectionStrings__Default", "DataProtection__KeysPath",
      "WEBSITES_ENABLE_APP_SERVICE_STORAGE",
    ])
    error_message = "The web app's settings are not exactly the documented keys (docs/AUTH.md and README.md)."
  }

  assert {
    condition     = startswith(azurerm_linux_web_app.web.app_settings["GitHub__Token"], "@Microsoft.KeyVault(VaultName=") && endswith(azurerm_linux_web_app.web.app_settings["GitHub__Token"], ";SecretName=github-token)")
    error_message = "GitHub__Token must be a Key Vault reference, never a value."
  }

  assert {
    condition     = azurerm_linux_web_app.web.app_settings["Demo__Enabled"] == "false"
    error_message = "Demo mode must be off in production."
  }

  assert {
    condition = toset(keys(azurerm_function_app_flex_consumption.sync.app_settings)) == toset([
      "AzureWebJobsStorage__accountName", "AzureWebJobsStorage__credential", "AzureWebJobsStorage__clientId",
      "SyncSchedule", "SyncApi__BaseUrl", "SyncApi__Scope", "SyncApi__ManagedIdentityClientId",
    ])
    error_message = "The Function's settings are not exactly the documented keys."
  }

  assert {
    condition     = azurerm_function_app_flex_consumption.sync.app_settings["SyncSchedule"] == "0 0 */4 * * *" && azurerm_linux_web_app.web.app_settings["Sync__IntervalHours"] == "4"
    error_message = "The schedule and the staleness interval must both follow sync_interval_hours (4 by default)."
  }

  assert {
    condition     = azurerm_function_app_flex_consumption.sync.app_settings["SyncApi__Scope"] == "api://22222222-2222-2222-2222-222222222222/.default"
    error_message = "The token scope must be the App ID URI plus /.default."
  }

  assert {
    condition     = azurerm_storage_account.function.shared_access_key_enabled == false && azurerm_storage_account.function.allow_nested_items_to_be_public == false && azurerm_storage_account.function.min_tls_version == "TLS1_2" && azurerm_storage_account.function.account_replication_type == "LRS"
    error_message = "The Function's storage account must be LRS, TLS 1.2, no public blobs and no shared keys."
  }

  assert {
    condition     = azurerm_key_vault.main.sku_name == "standard" && azurerm_key_vault.main.rbac_authorization_enabled
    error_message = "Key Vault must be standard tier with RBAC authorisation."
  }

  assert {
    condition     = length(azurerm_log_analytics_workspace.main) == 1 && azurerm_log_analytics_workspace.main[0].daily_quota_gb == 0.1 && azurerm_log_analytics_workspace.main[0].retention_in_days == 30
    error_message = "Monitoring must default to a 0.1 GB daily cap and 30 day retention."
  }
}

run "paid_plan_switches_on_always_on_and_health_check" {
  command = plan

  variables {
    web_sku = "B1"
  }

  assert {
    condition     = azurerm_linux_web_app.web.site_config[0].always_on == true && azurerm_linux_web_app.web.site_config[0].health_check_path == "/healthz"
    error_message = "A paid plan must turn on Always On and the /healthz health check."
  }
}

run "monitoring_can_be_switched_off" {
  command = plan

  variables {
    enable_monitoring = false
  }

  assert {
    condition     = length(azurerm_log_analytics_workspace.main) == 0 && length(azurerm_application_insights.main) == 0
    error_message = "enable_monitoring=false must create no workspace and no Application Insights."
  }
}

run "optional_entra_settings_appear_only_when_set" {
  command = plan

  variables {
    entra_app_id_uri    = "api://custom-uri"
    web_public_base_url = "https://dashboard.example.com"
  }

  assert {
    condition     = azurerm_linux_web_app.web.app_settings["AzureAd__AppIdUri"] == "api://custom-uri" && azurerm_linux_web_app.web.app_settings["AzureAd__PublicBaseUrl"] == "https://dashboard.example.com" && azurerm_function_app_flex_consumption.sync.app_settings["SyncApi__Scope"] == "api://custom-uri/.default"
    error_message = "A custom App ID URI and public base URL must reach both apps."
  }
}

run "enterprise_scope_sets_only_the_enterprise_key_when_the_org_is_empty" {
  command = plan

  variables {
    github_org        = ""
    github_enterprise = "big-corp"
  }

  assert {
    condition     = azurerm_linux_web_app.web.app_settings["GitHub__Enterprise"] == "big-corp" && !contains(keys(azurerm_linux_web_app.web.app_settings), "GitHub__Org")
    error_message = "Enterprise scope must set GitHub__Enterprise and no empty GitHub__Org."
  }
}

run "both_set_passes_both_to_the_app" {
  command = plan

  variables {
    github_enterprise = "big-corp"
  }

  assert {
    condition     = azurerm_linux_web_app.web.app_settings["GitHub__Enterprise"] == "big-corp" && azurerm_linux_web_app.web.app_settings["GitHub__Org"] == "acme"
    error_message = "When both are given both reach the app (the app lets the enterprise win)."
  }
}

run "one_of_org_or_enterprise_is_required" {
  command = plan

  variables {
    github_org        = ""
    github_enterprise = ""
  }

  expect_failures = [var.github_org]
}
