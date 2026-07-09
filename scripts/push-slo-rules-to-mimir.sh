#!/usr/bin/env bash
# push-slo-rules-to-mimir.sh
#
# Push k8s/monitoring/slo-rules.yaml (bare Prometheus rule groups — see that
# file's header comment) into Grafana Cloud Mimir's Ruler API via `mimirtool`.
# This is the missing step in mode=cloud: Grafana Cloud Mimir doesn't consume
# PrometheusRule CRDs, and deploy-local.sh never pushes rules to it on its own
# (pushing alert rules to a live account isn't something a routine local
# deploy should do silently) — this script is the deliberate, manual way to do
# it, same spirit as scripts/fetch-grafana-cloud-conf-from-akv.sh.
#
# Credentials: resolved the same way deploy-local.sh resolves them —
# monitoring.grafana_cloud.use_env in conf.yml is a required path to an env
# file to source (GRAFANA_CLOUD_*). There is no conf.yml-fields fallback.
#
# Usage:
#   ./scripts/push-slo-rules-to-mimir.sh             # mimirtool rules load (mutates the Ruler)
#   ./scripts/push-slo-rules-to-mimir.sh --dry-run    # mimirtool rules diff (read-only comparison)
#
# Requires: mimirtool (https://github.com/grafana/mimir/tree/main/cmd/mimirtool)
#
# Env override:
#   CONF_FILE=/path/to/conf.yml

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CONF_FILE="${CONF_FILE:-${REPO_DIR}/conf.yml}"
RULES_FILE="${REPO_DIR}/k8s/monitoring/slo-rules.yaml"

DRY_RUN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)  DRY_RUN=1 ;;
    -h|--help)  sed -n '2,20p' "$0"; exit 0 ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
  shift
done

require_bin() { command -v "$1" >/dev/null 2>&1 || { echo "ERROR: required command not found: $1" >&2; exit 1; }; }
require_bin mimirtool
require_bin python3

[[ -f "$CONF_FILE" ]] || { echo "ERROR: conf file not found: $CONF_FILE" >&2; exit 1; }
[[ -f "$RULES_FILE" ]] || { echo "ERROR: rules file not found: $RULES_FILE" >&2; exit 1; }

conf_get() {
  python3 - "$CONF_FILE" "$1" <<'PY'
import sys, yaml, re
conf_path, path = sys.argv[1], sys.argv[2]
doc = yaml.safe_load(open(conf_path)) or {}
cur = doc
try:
    for part in re.findall(r'[^.\[\]]+|\[\d+\]', path):
        cur = cur[int(part[1:-1])] if part.startswith('[') else cur[part]
except (KeyError, IndexError, TypeError):
    sys.exit(0)
if cur is not None:
    print(cur)
PY
}

# ── Resolve Mimir credentials — mirrors deploy-local.sh's use_env handling ──
USE_ENV="$(conf_get monitoring.grafana_cloud.use_env)"
[[ -n "$USE_ENV" ]] || { echo "ERROR: monitoring.grafana_cloud.use_env is required in ${CONF_FILE}" >&2; exit 1; }
ENV_FILE="$USE_ENV"
[[ "$ENV_FILE" == /* ]] || ENV_FILE="${REPO_DIR}/${ENV_FILE}"
[[ -f "$ENV_FILE" ]] || { echo "ERROR: monitoring.grafana_cloud.use_env=${USE_ENV} but ${ENV_FILE} not found" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
MIMIR_ENDPOINT="${GRAFANA_CLOUD_MIMIR_ENDPOINT:-}"
MIMIR_USER="${GRAFANA_CLOUD_MIMIR_USER:-}"
API_KEY="${GRAFANA_CLOUD_API_KEY:-}"

: "${MIMIR_ENDPOINT:?Mimir endpoint is empty (source: $ENV_FILE)}"
: "${MIMIR_USER:?Mimir user is empty (source: $ENV_FILE)}"
: "${API_KEY:?Grafana Cloud API key is empty (source: $ENV_FILE)}"

# monitoring.grafana_cloud.mimir.endpoint is the remote_write push URL
# (".../api/prom/push"); the Ruler API lives at the same host with no suffix.
MIMIR_ADDRESS="${MIMIR_ENDPOINT%/api/prom/push}"

if [[ "$DRY_RUN" -eq 1 ]]; then
  echo "── mimirtool rules diff (read-only) ──"
  echo "address: $MIMIR_ADDRESS"
  echo "id:      $MIMIR_USER"
  mimirtool rules diff "$RULES_FILE" \
    --address="$MIMIR_ADDRESS" \
    --id="$MIMIR_USER" \
    --key="$API_KEY"
else
  echo "── mimirtool rules load ──"
  echo "address: $MIMIR_ADDRESS"
  echo "id:      $MIMIR_USER"
  mimirtool rules load "$RULES_FILE" \
    --address="$MIMIR_ADDRESS" \
    --id="$MIMIR_USER" \
    --key="$API_KEY"
  echo
  echo "Loaded. Verify with:"
  echo "  mimirtool rules list --address=$MIMIR_ADDRESS --id=$MIMIR_USER --key=<api-key>"
fi
