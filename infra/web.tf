# The dashboard: a Linux App Service plan and a .NET 10 web app.

locals {
  # F1 (Free) does not support Always On. The same is assumed (not verified) for the App Service health check, see below.
  is_free = var.web_sku == "F1"

  web_app_settings = merge(
    {
      # Microsoft Entra sign-in (docs/AUTH.md, section 3). None of these is a secret.
      "AzureAd__TenantId" = var.entra_tenant_id
      "AzureAd__ClientId" = var.entra_client_id

      # The only caller of POST /api/sync: the Function's user-assigned managed identity.
      "Sync__AllowedCallerObjectId" = azurerm_user_assigned_identity.sync.principal_id
      "Sync__AllowedCallerClientId" = azurerm_user_assigned_identity.sync.client_id
      # Only used by /healthz to call a sync stale after three intervals. Same variable as the Function's schedule.
      "Sync__IntervalHours" = tostring(var.sync_interval_hours)

      # GitHub. The token is a Key Vault reference (no version, so a rotated secret is picked up). The value is set by hand.
      "GitHub__Org"   = var.github_org
      "GitHub__Token" = "@Microsoft.KeyVault(VaultName=${azurerm_key_vault.main.name};SecretName=${local.token_secret_name})"

      "Copilot__CreditsPerSeat" = tostring(var.credits_per_seat)
      "Demo__Enabled"           = "false"

      # SQLite file and data-protection keys live on the persistent /home disk (these equal the appsettings.Production.json values;
      # set here as well so the intent is visible next to the infrastructure and a change is a reviewed diff).
      "ConnectionStrings__Default" = "Data Source=/home/data/copilot-usage.db"
      "DataProtection__KeysPath"   = "/home/data/dataprotection-keys"

      # /home is persistent for the built-in .NET image by default; stated explicitly so nobody switches it off by accident.
      "WEBSITES_ENABLE_APP_SERVICE_STORAGE" = "true"
    },
    var.entra_app_id_uri != "" ? { "AzureAd__AppIdUri" = var.entra_app_id_uri } : {},
    var.web_public_base_url != "" ? { "AzureAd__PublicBaseUrl" = var.web_public_base_url } : {},
  )
}

resource "azurerm_service_plan" "web" {
  name                = "asp-${var.name_prefix}-web"
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  os_type             = "Linux"
  sku_name            = var.web_sku
  tags                = local.tags
}

resource "azurerm_linux_web_app" "web" {
  name                = local.web_app_name
  resource_group_name = data.azurerm_resource_group.main.name
  location            = local.location
  service_plan_id     = azurerm_service_plan.web.id
  tags                = local.tags

  https_only                    = true
  client_affinity_enabled       = false # one instance, so no session affinity is needed
  public_network_access_enabled = true  # protected by Entra sign-in; network control is Conditional Access + Global Secure Access (docs/DEPLOY.md, step 7)

  # Publishing with a username and password is switched off: the pipeline deploys with its Entra identity.
  ftp_publish_basic_authentication_enabled       = !var.disable_basic_auth_publishing
  webdeploy_publish_basic_authentication_enabled = !var.disable_basic_auth_publishing

  # Reads Key Vault references (GitHub__Token). A system-assigned identity is available on the Free plan as far as the author knows (unverified).
  identity {
    type = "SystemAssigned"
  }

  app_settings = local.web_app_settings

  site_config {
    # F1 does not support Always On, so the app sleeps after about 20 minutes without a request. That is why the Function, not an
    # in-app timer, schedules the sync. On a paid plan Always On is switched on automatically.
    always_on = !local.is_free

    # The App Service health check is configured only on paid plans. Whether the Free plan accepts the setting is unverified, and
    # with a single instance a failing probe has nothing to evict anyway; the deploy pipeline smoke-tests /healthz instead.
    health_check_path                 = local.is_free ? null : "/healthz"
    health_check_eviction_time_in_min = local.is_free ? null : 10

    # Linux has no 32/64-bit choice at runtime; the Free plan is documented to require the 32-bit flag on Windows, so it is set
    # for F1 as the safe value. Unverified for Linux; harmless if ignored.
    use_32_bit_worker = local.is_free

    websockets_enabled  = true # Blazor Server. The Free plan allows about 5 concurrent websocket connections.
    http2_enabled       = true
    minimum_tls_version = "1.2"
    ftps_state          = "Disabled"

    scm_minimum_tls_version = "1.2"

    application_stack {
      dotnet_version = "10.0"
    }
  }

  # Needed for the log stream and `az webapp log tail` (the runbook reads the sync endpoint's refusal reasons there).
  logs {
    application_logs {
      file_system_level = "Information"
    }
  }
}
