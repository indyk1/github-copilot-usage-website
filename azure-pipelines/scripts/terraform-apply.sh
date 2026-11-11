#!/usr/bin/env bash
# Applies EXACTLY the saved plan from the Plan stage (never a fresh plan), then prints the outputs the manual steps need.
# Run from the downloaded plan artifact's folder inside an AzureCLI@2 task; SCRIPTS_DIR points at this folder.
set -euo pipefail
# shellcheck source=tf-common.sh
source "${SCRIPTS_DIR}/tf-common.sh"

tf_init
terraform apply -input=false -lock-timeout=5m tfplan

echo
echo "================ Outputs for the manual steps (none is a secret) ================"
terraform output
