#!/usr/bin/env bash
# init (against the hand-made state account), validate, plan. Writes the plan to ./tfplan and reports whether it changes anything.
# Run from the infra directory inside an AzureCLI@2 task; SCRIPTS_DIR points at this folder.
set -euo pipefail
# shellcheck source=tf-common.sh
source "${SCRIPTS_DIR}/tf-common.sh"

tf_init
terraform validate

# Required inputs, then optional ones only when the pipeline variable is set (an unset variable keeps the Terraform default).
args=(
  -var "resource_group_name=${TF_RESOURCE_GROUP}"
  -var "name_prefix=${TF_NAME_PREFIX}"
  -var "github_org=${TF_GITHUB_ORG}"
  -var "entra_tenant_id=${TF_ENTRA_TENANT_ID}"
  -var "entra_client_id=${TF_ENTRA_CLIENT_ID}"
)
add_optional() { # add_optional <terraform variable> <environment variable>
  local value="${!2:-}"
  if [ -n "$value" ]; then args+=(-var "$1=$value"); fi
}
add_optional location                     TF_LOCATION
add_optional entra_app_id_uri             TF_ENTRA_APP_ID_URI
add_optional credits_per_seat             TF_CREDITS_PER_SEAT
add_optional sync_interval_hours          TF_SYNC_INTERVAL_HOURS
add_optional web_sku                      TF_WEB_SKU
add_optional web_public_base_url          TF_WEB_PUBLIC_BASE_URL
add_optional enable_monitoring            TF_ENABLE_MONITORING
add_optional disable_basic_auth_publishing TF_DISABLE_BASIC_AUTH_PUBLISHING
if [ -n "${TF_KEY_VAULT_SECRETS_OFFICER_OBJECT_IDS:-}" ]; then
  ids="$(echo "$TF_KEY_VAULT_SECRETS_OFFICER_OBJECT_IDS" | tr -d ' ' | sed 's/,/","/g')"
  args+=(-var "key_vault_secrets_officer_object_ids=[\"${ids}\"]")
fi

# -detailed-exitcode: 0 = nothing to do, 2 = changes, 1 = error.
set +e
terraform plan -input=false -lock-timeout=5m -detailed-exitcode -out=tfplan "${args[@]}"
code=$?
set -e
case "$code" in
  0) echo "No changes. The infrastructure matches the configuration."; echo "##vso[task.setvariable variable=hasChanges;isOutput=true]false" ;;
  2) echo "The plan above has changes. Review it, then approve the Apply stage."; echo "##vso[task.setvariable variable=hasChanges;isOutput=true]true" ;;
  *) echo "##vso[task.logissue type=error]terraform plan failed (exit code $code)."; exit "$code" ;;
esac
