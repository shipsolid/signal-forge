#!/usr/bin/env bash
# fetch-grafana-cloud-conf-from-akv.sh
#
# Pull every Grafana Cloud secret from Azure Key Vault and write the fetched
# values IN PLACE into the env file named by conf.yml's
# monitoring.grafana_cloud.use_env (GRAFANA_CLOUD_*/FARO_* keys — the same
# ones deploy-local.sh and the legacy Makefile flow read).
#
# Vault coordinates (ARM_TENANT_ID / ARM_SUBSCRIPTION_ID / Resource_Group /
# Azure_KeyVault) are read FROM that same env file — they're already defined
# there for the legacy Makefile flow (`make secrets-fetch-akv`).
#
# Comments and unrelated lines in the env file are preserved — the updater
# rewrites only the nine leaf lines it owns (appending any that don't exist
# yet).
#
# Authentication:
#   Uses ARM_CLIENT_ID / ARM_CLIENT_SECRET from the env file if both are set
#   (service-principal login). Otherwise falls back to the caller's existing
#   `az login` session.
#
# Usage:
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh             # fetch + apply in place
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run   # fetch + show diff, don't write
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh --print     # fetch + print env lines (legacy)
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh --no-backup # skip .bak
#
# Env override:
#   CONF_FILE=/path/to/conf.yml

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CONF_FILE="${CONF_FILE:-${REPO_DIR}/conf.yml}"

DRY_RUN=0
PRINT_ONLY=0
NO_BACKUP=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --dry-run)    DRY_RUN=1 ;;
    --print)      PRINT_ONLY=1 ;;
    --no-backup)  NO_BACKUP=1 ;;
    -h|--help)    sed -n '2,23p' "$0"; exit 0 ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
  shift
done

require_bin() { command -v "$1" >/dev/null 2>&1 || { echo "ERROR: required command not found: $1" >&2; exit 1; }; }
require_bin az
require_bin python3

[[ -f "$CONF_FILE" ]] || { echo "ERROR: conf file not found: $CONF_FILE" >&2; exit 1; }
CONF_DIR="$(cd "$(dirname "$CONF_FILE")" && pwd)"

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

# ── Resolve the target env file from conf.yml ────────────────────────────────
USE_ENV="$(conf_get monitoring.grafana_cloud.use_env)"
[[ -n "$USE_ENV" ]] || { echo "ERROR: monitoring.grafana_cloud.use_env is required in ${CONF_FILE}" >&2; exit 1; }
ENV_FILE="$USE_ENV"
[[ "$ENV_FILE" == /* ]] || ENV_FILE="${CONF_DIR}/${ENV_FILE}"
[[ -f "$ENV_FILE" ]] || { echo "ERROR: monitoring.grafana_cloud.use_env=${USE_ENV} but ${ENV_FILE} not found" >&2; exit 1; }

# ── Read AKV coordinates from the env file ───────────────────────────────────
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a

: "${ARM_TENANT_ID:?ARM_TENANT_ID must be set in ${ENV_FILE}}"
: "${ARM_SUBSCRIPTION_ID:?ARM_SUBSCRIPTION_ID must be set in ${ENV_FILE}}"
: "${Resource_Group:?Resource_Group must be set in ${ENV_FILE}}"
: "${Azure_KeyVault:?Azure_KeyVault must be set in ${ENV_FILE}}"

# ── Azure auth ───────────────────────────────────────────────────────────────
# Two paths:
#   1. ARM_CLIENT_ID + ARM_CLIENT_SECRET set (from the env file) → log in as
#      service principal.
#   2. Otherwise → use the caller's existing `az login` session. Fail fast if
#      no session exists (az account show returns non-zero).
if [[ -n "${ARM_CLIENT_ID:-}" && -n "${ARM_CLIENT_SECRET:-}" ]]; then
  echo "==> az login (service principal from ${ENV_FILE}, tenant=${ARM_TENANT_ID})"
  az login \
    --service-principal \
    --username "$ARM_CLIENT_ID" \
    --password "$ARM_CLIENT_SECRET" \
    --tenant "$ARM_TENANT_ID" \
    --output none >/dev/null
else
  if ! az account show --output none 2>/dev/null; then
    echo "ERROR: no active az session. Run 'az login' first, or set ARM_CLIENT_ID + ARM_CLIENT_SECRET in ${ENV_FILE} for service-principal auth." >&2
    exit 1
  fi
  echo "==> using existing az session ($(az account show --query user.name -o tsv 2>/dev/null || echo unknown))"
fi

az account set --subscription "$ARM_SUBSCRIPTION_ID" >/dev/null

kv_get() {
  az keyvault secret show --vault-name "$Azure_KeyVault" --name "$1" --query value -o tsv
}
kv_get_optional() {
  az keyvault secret show --vault-name "$Azure_KeyVault" --name "$1" --query value -o tsv 2>/dev/null || true
}

echo "==> fetching secrets from Key Vault: ${Azure_KeyVault}"
api_key="$(kv_get grafana-example-org-alloy-writer-example-org-token)"
tempo_host="$(kv_get grafana-example-org-cloud-tempo-endpoint)"
tempo_user="$(kv_get grafana-example-org-cloud-tempo-username)"
mimir_base="$(kv_get grafana-example-org-cloud-mimir-endpoint)"
mimir_user="$(kv_get grafana-example-org-cloud-mimir-username)"
loki_base="$(kv_get grafana-example-org-cloud-loki-endpoint)"
loki_user="$(kv_get grafana-example-org-cloud-loki-username)"
faro_endpoint="$(kv_get_optional grafana-example-org-faro-api-endpoint)"
faro_api_key="$(kv_get_optional grafana-example-org-faro-sourcemap-token)"

trim_slash() { local v="$1"; printf '%s' "${v%/}"; }

# ── Normalise to the formats the rest of the stack expects ───────────────────
# Tempo: host only → append :443 (OTLP gRPC uses h2, no https:// prefix).
tempo_host="${tempo_host#https://}"
tempo_host="${tempo_host#http://}"
tempo_host="$(trim_slash "$tempo_host")"
tempo_endpoint="${tempo_host}:443"

# Mimir: Prometheus remote_write requires .../api/prom/push.
# AKV typically stores `.../api/prom` (the base); append /push if missing.
mimir_base="$(trim_slash "$mimir_base")"
case "$mimir_base" in
  */push) mimir_endpoint="$mimir_base" ;;
  *)      mimir_endpoint="${mimir_base}/push" ;;
esac

# Loki: requires /loki/api/v1/push suffix; AKV typically stores just the host.
loki_base="$(trim_slash "$loki_base")"
case "$loki_base" in
  *"/loki/api/v1/push") loki_endpoint="$loki_base" ;;
  *)                    loki_endpoint="${loki_base}/loki/api/v1/push" ;;
esac

faro_endpoint="$(trim_slash "${faro_endpoint:-}")"

# ── Print-only mode: emit env-file lines and stop ────────────────────────────
if [[ "$PRINT_ONLY" -eq 1 ]]; then
  cat <<EOF
GRAFANA_CLOUD_API_KEY="${api_key}"
GRAFANA_CLOUD_TEMPO_ENDPOINT="${tempo_endpoint}"
GRAFANA_CLOUD_TEMPO_USER="${tempo_user}"
GRAFANA_CLOUD_MIMIR_ENDPOINT="${mimir_endpoint}"
GRAFANA_CLOUD_MIMIR_USER="${mimir_user}"
GRAFANA_CLOUD_LOKI_ENDPOINT="${loki_endpoint}"
GRAFANA_CLOUD_LOKI_USER="${loki_user}"
FARO_COLLECTOR_URL="${faro_endpoint}"
FARO_API_KEY="${faro_api_key}"
EOF
  exit 0
fi

# ── In-place update of the env file ──────────────────────────────────────────
# Preserves comments, ordering, and unrelated lines by line-level editing.
# Missing keys are appended rather than skipped.
python3 - "$ENV_FILE" "$DRY_RUN" "$NO_BACKUP" \
  "$api_key" "$tempo_endpoint" "$tempo_user" \
  "$mimir_endpoint" "$mimir_user" \
  "$loki_endpoint" "$loki_user" \
  "$faro_endpoint" "$faro_api_key" <<'PY'
import sys, re, shutil, difflib

env_path  = sys.argv[1]
dry_run   = sys.argv[2] == "1"
no_backup = sys.argv[3] == "1"
(api_key, tempo_ep, tempo_user,
 mimir_ep, mimir_user, loki_ep, loki_user,
 faro_ep, faro_api_key) = sys.argv[4:13]

# key → new value, in the order they should be appended if missing.
updates = {
    "GRAFANA_CLOUD_API_KEY":        api_key,
    "GRAFANA_CLOUD_TEMPO_ENDPOINT": tempo_ep,
    "GRAFANA_CLOUD_TEMPO_USER":     tempo_user,
    "GRAFANA_CLOUD_MIMIR_ENDPOINT": mimir_ep,
    "GRAFANA_CLOUD_MIMIR_USER":     mimir_user,
    "GRAFANA_CLOUD_LOKI_ENDPOINT":  loki_ep,
    "GRAFANA_CLOUD_LOKI_USER":      loki_user,
    "FARO_COLLECTOR_URL":           faro_ep,
    "FARO_API_KEY":                 faro_api_key,
}

with open(env_path) as f:
    original = f.read()
lines = original.splitlines(keepends=True)

key_re = re.compile(r'^(?P<key>[A-Za-z_][A-Za-z0-9_]*)=(?P<rest>.*)$')

def quote(value: str) -> str:
    return '"' + value.replace("\\", "\\\\").replace('"', '\\"') + '"'

changed = []
seen = set()
for idx, raw in enumerate(lines):
    line = raw.rstrip("\n")
    m = key_re.match(line)
    if not m:
        continue
    key = m.group("key")
    if key not in updates:
        continue
    seen.add(key)
    old_val = m.group("rest")
    new_line = f"{key}={quote(updates[key])}"
    if new_line != line:
        changed.append((key, old_val, updates[key]))
        lines[idx] = new_line + ("\n" if raw.endswith("\n") else "\n")

missing = [k for k in updates if k not in seen]
if missing:
    if lines and not lines[-1].endswith("\n"):
        lines[-1] += "\n"
    lines.append("\n# --- appended by fetch-grafana-cloud-conf-from-akv.sh ---\n")
    for k in missing:
        lines.append(f"{k}={quote(updates[k])}\n")
        changed.append((k, "", updates[k]))

if changed:
    print(f"==> {len(changed)} field(s) to update:")
    for key, old, new in changed:
        old_disp = old[:40] + ("…" if len(old) > 40 else "")
        new_disp = new[:40] + ("…" if len(new) > 40 else "")
        print(f"    {key}")
        print(f"      -  {old_disp}")
        print(f"      +  {new_disp}")
else:
    print("==> no changes (fetched values already match the env file)")

new_text = "".join(lines)
if dry_run:
    if new_text != original:
        print("\n==> DRY-RUN diff (no file written):")
        diff = difflib.unified_diff(
            original.splitlines(keepends=True),
            new_text.splitlines(keepends=True),
            fromfile=env_path, tofile=env_path + " (proposed)",
        )
        sys.stdout.writelines(diff)
    sys.exit(0)

if new_text == original:
    sys.exit(0)

if not no_backup:
    shutil.copy2(env_path, env_path + ".bak")
    print(f"==> backed up: {env_path}.bak")

with open(env_path, "w") as f:
    f.write(new_text)
print(f"==> updated: {env_path}")
PY
