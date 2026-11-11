# Terraform and provider versions are pinned. .terraform.lock.hcl (committed) pins the exact provider build.
# Keep required_version in step with TERRAFORM_VERSION in azure-pipelines/templates/terraform-setup.yml.

terraform {
  required_version = "~> 1.17.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.81"
    }
  }

  # Remote state lives in a storage account that is created BY HAND before the first pipeline run (docs/DEPLOY.md, step 2).
  # Nothing environment-specific is written here: the account, container and key are passed at `terraform init` time:
  #   terraform init -backend-config="storage_account_name=..." -backend-config="container_name=..." -backend-config="key=..."
  # Access uses Microsoft Entra ID (the pipeline's identity needs "Storage Blob Data Contributor" on the container), not a storage key.
  backend "azurerm" {
    use_azuread_auth = true
  }
}

provider "azurerm" {
  features {}

  # The subscription comes from the environment (ARM_SUBSCRIPTION_ID), which the pipeline sets from its service connection.
  # Do not let Terraform try to register resource providers: that needs subscription-level rights. The runbook registers the
  # handful of providers once, by hand.
  resource_provider_registrations = "none"

  # Only affects data-plane calls to storage; this configuration makes none that need a key (shared keys are off on the Function's account).
  storage_use_azuread = true
}
