# ---- Required inputs (no defaults: these are specific to your environment) ------------------------------------------------------

variable "resource_group_name" {
  description = "Name of an EXISTING resource group to deploy into. It is created by hand (docs/DEPLOY.md) so that the pipeline only needs rights on this group, not on the whole subscription."
  type        = string
  validation {
    condition     = length(var.resource_group_name) > 0
    error_message = "resource_group_name must not be empty."
  }
}

variable "name_prefix" {
  description = "Short lowercase prefix used in every resource name (for example \"copilotusage\"). Letters and digits only, 3 to 12 characters, starting with a letter. A short hash of the resource group is appended where Azure needs globally unique names."
  type        = string
  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.name_prefix))
    error_message = "name_prefix must be 3 to 12 lowercase letters or digits and start with a letter."
  }
}

variable "github_enterprise" {
  description = "Slug of the GitHub Enterprise account that holds the Copilot billing. When set, the dashboard shows the whole enterprise (enterprise scope) and github_org is ignored by the app. Leave empty for a self-billed organization."
  type        = string
  default     = ""
  validation {
    condition     = var.github_enterprise == "" || can(regex("^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$", var.github_enterprise))
    error_message = "github_enterprise must be an enterprise slug (letters, digits, hyphens), not a URL."
  }
}

variable "github_org" {
  description = "Login of the GitHub organization whose Copilot usage is shown (organization scope, for a self-billed organization). Optional when github_enterprise is set. At least one of github_org and github_enterprise is required."
  type        = string
  default     = ""
  validation {
    condition     = var.github_org == "" || can(regex("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", var.github_org))
    error_message = "github_org must be a GitHub organization login (letters, digits and hyphens, at most 39 characters)."
  }
  validation {
    condition     = var.github_org != "" || var.github_enterprise != ""
    error_message = "At least one of github_org and github_enterprise is required: the enterprise slug when the enterprise holds the Copilot billing, otherwise the organization login."
  }
}

variable "entra_tenant_id" {
  description = "Directory (tenant) ID of the Microsoft Entra tenant that holds the app registration (a GUID). Not a secret. Created by hand by the IT admin: see docs/AUTH.md."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.entra_tenant_id))
    error_message = "entra_tenant_id must be a GUID (never common, organizations or consumers)."
  }
}

variable "entra_client_id" {
  description = "Application (client) ID of the web app's Entra app registration (a GUID). Not a secret."
  type        = string
  validation {
    condition     = can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", var.entra_client_id))
    error_message = "entra_client_id must be a GUID."
  }
}

# ---- Optional inputs (safe defaults) --------------------------------------------------------------------------------------------

variable "location" {
  description = "Azure region. Leave null to use the resource group's region. Flex Consumption and the Free App Service plan are not available in every region: if the first plan or apply fails with a SKU or region error, choose another region."
  type        = string
  default     = null
}

variable "entra_app_id_uri" {
  description = "Application ID URI of the app registration, only if it is NOT api://<entra_client_id> (the default Entra proposes). Leave empty otherwise."
  type        = string
  default     = ""
}

variable "credits_per_seat" {
  description = "AI credits included per Copilot seat per month (Copilot Business: 1900). Becomes Copilot__CreditsPerSeat."
  type        = number
  default     = 1900
  validation {
    condition     = var.credits_per_seat > 0
    error_message = "credits_per_seat must be greater than zero."
  }
}

variable "sync_interval_hours" {
  description = "Hours between syncs. Drives BOTH the Function's schedule (SyncSchedule) and the web app's staleness check (Sync__IntervalHours), so they cannot disagree. Use a divisor of 24 so the runs are evenly spaced."
  type        = number
  default     = 4
  validation {
    condition     = contains([1, 2, 3, 4, 6, 8, 12], var.sync_interval_hours)
    error_message = "sync_interval_hours must be one of 1, 2, 3, 4, 6, 8 or 12."
  }
}

variable "web_sku" {
  description = <<-EOT
    App Service plan SKU for the web app. F1 is the free plan (the default and the owner's decision). To move to a paid plan change this one
    value (for example B1): Always On and the /healthz health check are switched on automatically when the SKU is not F1.
  EOT
  type        = string
  default     = "F1"
  validation {
    condition     = contains(["F1", "B1", "B2", "B3", "S1", "S2", "S3", "P0v3", "P1v3", "P2v3"], var.web_sku)
    error_message = "web_sku must be one of F1, B1, B2, B3, S1, S2, S3, P0v3, P1v3, P2v3. The app must run on exactly one instance, so a small SKU is enough."
  }
}

variable "web_public_base_url" {
  description = "Only when the site is reached on a custom domain (paid plans): the origin, for example https://dashboard.contoso.com. Becomes AzureAd__PublicBaseUrl and pins the sign-in redirect URIs. Leave empty to use the host the browser used."
  type        = string
  default     = ""
  validation {
    condition     = var.web_public_base_url == "" || can(regex("^https://[^/]+$", var.web_public_base_url))
    error_message = "web_public_base_url must be empty or an https origin with no path, for example https://dashboard.contoso.com."
  }
}

variable "disable_basic_auth_publishing" {
  description = "Disable basic-auth (username and password) publishing credentials on both apps. The pipeline deploys with its Entra identity, so this should stay true. If a deployment fails with 401 from the Kudu/SCM site, set it to false and re-run the infrastructure pipeline (see docs/DEPLOY.md, troubleshooting)."
  type        = bool
  default     = true
}

variable "enable_monitoring" {
  description = "Create a Log Analytics workspace and workspace-based Application Insights for the Function, so you can see whether it ran. Set false to create neither."
  type        = bool
  default     = true
}

variable "log_daily_cap_gb" {
  description = "Hard daily ingestion cap (GB) on the Log Analytics workspace and on Application Insights. 0.1 GB a day is at most about 3 GB a month, under the 5 GB monthly free allowance. When the cap is hit, logging stops until the next day."
  type        = number
  default     = 0.1
  validation {
    condition     = var.log_daily_cap_gb >= 0.023 && var.log_daily_cap_gb <= 1
    error_message = "log_daily_cap_gb must be between 0.023 and 1 (a larger cap defeats the purpose)."
  }
}

variable "log_retention_days" {
  description = "How long logs are kept. 30 days is the shortest Application Insights accepts and is inside the free retention."
  type        = number
  default     = 30
  validation {
    condition     = contains([30, 60, 90], var.log_retention_days)
    error_message = "log_retention_days must be 30, 60 or 90."
  }
}

variable "function_instance_memory_mb" {
  description = "Memory per Function instance (Flex Consumption). 512 is the smallest and keeps usage far inside the free monthly grant."
  type        = number
  default     = 512
  validation {
    condition     = contains([512, 2048, 4096], var.function_instance_memory_mb)
    error_message = "function_instance_memory_mb must be 512, 2048 or 4096."
  }
}

variable "key_vault_secrets_officer_object_ids" {
  description = "Object IDs (GUIDs) of people or groups who may set the GitHub token secret in Key Vault (role Key Vault Secrets Officer). Granting it here means the person needs no Owner rights to do step 5 of the runbook. Can be empty: then someone with Owner on the vault must grant the role by hand."
  type        = list(string)
  default     = []
  validation {
    condition     = alltrue([for id in var.key_vault_secrets_officer_object_ids : can(regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", id))])
    error_message = "Every entry of key_vault_secrets_officer_object_ids must be a GUID."
  }
}

variable "tags" {
  description = "Extra tags for every resource."
  type        = map(string)
  default     = {}
}
