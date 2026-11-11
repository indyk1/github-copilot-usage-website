#!/usr/bin/env bash
# Sourced by terraform-plan.sh and terraform-apply.sh. Runs INSIDE an AzureCLI@2 task, so `az` is already signed in as the
# service connection's identity (workload identity federation: no client secret exists). Terraform reuses that sign-in
# ("Azure CLI" authentication) for both the azurerm provider and the state backend (Entra auth, no storage key).
set -euo pipefail

export ARM_USE_CLI=true
ARM_SUBSCRIPTION_ID="$(az account show --query id --output tsv)"
ARM_TENANT_ID="$(az account show --query tenantId --output tsv)"
export ARM_SUBSCRIPTION_ID ARM_TENANT_ID
export TF_IN_AUTOMATION=1
export TF_INPUT=0

# The backend's account, container and key are never written in the repository: they come from pipeline variables.
tf_init() {
  terraform init -input=false -lockfile=readonly \
    -backend-config="storage_account_name=${TFSTATE_STORAGE_ACCOUNT}" \
    -backend-config="container_name=${TFSTATE_CONTAINER}" \
    -backend-config="key=${TFSTATE_KEY}"
}
