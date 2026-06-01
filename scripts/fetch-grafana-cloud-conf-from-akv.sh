#!/usr/bin/env bash
# fetch-grafana-cloud-conf-from-akv.sh
#
# Pull every Grafana Cloud secret from the Key Vault named in conf.yml's
# monitoring.grafana_cloud.akv.*, and write the fetched values IN PLACE back
# into conf.yml's monitoring.grafana_cloud.{api_key, tempo.*, mimir.*, loki.*,
# faro.*} fields.
#
# Comments, ordering, and unrelated fields in conf.yml are preserved — the
# updater edits only the specific leaf lines it owns.
#
# Authentication:
#   Uses the az CLI's existing login context. Run `az login` once before this.
#   Service-principal auth is also honored if the shell has ARM_CLIENT_ID and
#   ARM_CLIENT_SECRET exported (no .env loading — export them yourself).
#
# Usage:
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh             # fetch + apply in place
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run   # fetch + show diff, don't write
#   ./scripts/fetch-grafana-cloud-conf-from-akv.sh --print     # fetch + print YAML block (legacy)
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

# ── Read AKV metadata from conf.yml ──────────────────────────────────────────
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

TENANT_ID="$(conf_get monitoring.grafana_cloud.akv.tenant_id)"
SUBSCRIPTION_ID="$(conf_get monitoring.grafana_cloud.akv.subscription_id)"
RESOURCE_GROUP="$(conf_get monitoring.grafana_cloud.akv.resource_group)"
AZURE_KEYVAULT="$(conf_get monitoring.grafana_cloud.akv.vault_name)"

: "${TENANT_ID:?monitoring.grafana_cloud.akv.tenant_id must be set in ${CONF_FILE}}"
: "${SUBSCRIPTION_ID:?monitoring.grafana_cloud.akv.subscription_id must be set in ${CONF_FILE}}"
: "${RESOURCE_GROUP:?monitoring.grafana_cloud.akv.resource_group must be set in ${CONF_FILE}}"
: "${AZURE_KEYVAULT:?monitoring.grafana_cloud.akv.vault_name must be set in ${CONF_FILE}}"

# ── Azure auth ───────────────────────────────────────────────────────────────
# Two paths:
#   1. Shell exports ARM_CLIENT_ID + ARM_CLIENT_SECRET → log in as service principal.
#   2. Otherwise → use the caller's existing `az login` session. Fail fast if
#      no session exists (az account show returns non-zero).
if [[ -n "${ARM_CLIENT_ID:-}" && -n "${ARM_CLIENT_SECRET:-}" ]]; then
  echo "==> az login (service principal from shell env, tenant=${TENANT_ID})"
  az login \
    --service-principal \
    --username "$ARM_CLIENT_ID" \
    --password "$ARM_CLIENT_SECRET" \
    --tenant "$TENANT_ID" \
    --output none >/dev/null
else
  if ! az account show --output none 2>/dev/null; then
    echo "ERROR: no active az session. Run 'az login' first, or export ARM_CLIENT_ID + ARM_CLIENT_SECRET for service-principal auth." >&2
    exit 1
  fi
  echo "==> using existing az session ($(az account show --query user.name -o tsv 2>/dev/null || echo unknown))"
fi

az account set --subscription "$SUBSCRIPTION_ID" >/dev/null

kv_get() {
  az keyvault secret show --vault-name "$AZURE_KEYVAULT" --name "$1" --query value -o tsv
}
kv_get_optional() {
  az keyvault secret show --vault-name "$AZURE_KEYVAULT" --name "$1" --query value -o tsv 2>/dev/null || true
}

echo "==> fetching secrets from Key Vault: ${AZURE_KEYVAULT}"
api_key="$(kv_get grafana-mccaindev-alloy-writer-mccaindev-token)"
tempo_host="$(kv_get grafana-mccaindev-cloud-tempo-endpoint)"
tempo_user="$(kv_get grafana-mccaindev-cloud-tempo-username)"
mimir_base="$(kv_get grafana-mccaindev-cloud-mimir-endpoint)"
mimir_user="$(kv_get grafana-mccaindev-cloud-mimir-username)"
loki_base="$(kv_get grafana-mccaindev-cloud-loki-endpoint)"
loki_user="$(kv_get grafana-mccaindev-cloud-loki-username)"
faro_endpoint="$(kv_get_optional grafana-mccaindev-faro-api-endpoint)"
faro_api_key="$(kv_get_optional grafana-mccaindev-faro-sourcemap-token)"

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

# ── Print-only mode: emit a YAML block and stop ──────────────────────────────
if [[ "$PRINT_ONLY" -eq 1 ]]; then
  cat <<EOF
monitoring:
  grafana_cloud:
    api_key: "${api_key}"
    tempo:
      endpoint: "${tempo_endpoint}"
      user: "${tempo_user}"
    mimir:
      endpoint: "${mimir_endpoint}"
      user: "${mimir_user}"
    loki:
      endpoint: "${loki_endpoint}"
      user: "${loki_user}"
    faro:
      endpoint: "${faro_endpoint}"
      api_key: "${faro_api_key}"
EOF
  exit 0
fi

# ── In-place update of conf.yml ──────────────────────────────────────────────
# Preserves comments, ordering, and unrelated fields by line-level editing.
python3 - "$CONF_FILE" "$DRY_RUN" "$NO_BACKUP" \
  "$api_key" "$tempo_endpoint" "$tempo_user" \
  "$mimir_endpoint" "$mimir_user" \
  "$loki_endpoint" "$loki_user" \
  "$faro_endpoint" "$faro_api_key" <<'PY'
import sys, re, os, shutil, difflib

conf_path = sys.argv[1]
dry_run   = sys.argv[2] == "1"
no_backup = sys.argv[3] == "1"
(api_key, tempo_ep, tempo_user,
 mimir_ep, mimir_user, loki_ep, loki_user,
 faro_ep, faro_api_key) = sys.argv[4:13]

# path → new value. Paths are dotted under conf.yml root.
updates = {
    "monitoring.grafana_cloud.api_key":         api_key,
    "monitoring.grafana_cloud.tempo.endpoint":  tempo_ep,
    "monitoring.grafana_cloud.tempo.user":      tempo_user,
    "monitoring.grafana_cloud.mimir.endpoint":  mimir_ep,
    "monitoring.grafana_cloud.mimir.user":      mimir_user,
    "monitoring.grafana_cloud.loki.endpoint":   loki_ep,
    "monitoring.grafana_cloud.loki.user":       loki_user,
    "monitoring.grafana_cloud.faro.endpoint":   faro_ep,
    "monitoring.grafana_cloud.faro.api_key":    faro_api_key,
}

with open(conf_path) as f:
    original = f.read()
lines = original.splitlines(keepends=True)

key_re = re.compile(
    r'^(?P<indent>\s*)(?P<key>[A-Za-z0-9_]+)\s*:(?P<rest>.*)$'
)

def parse_current_value(rest: str):
    """Return (value_part, trailing_comment) given the text after 'key:'."""
    s = rest.lstrip()
    comment = ""
    # Scan for a '#' that is outside any quoted section.
    value_part, comment_part = s, ""
    in_str = None
    for i, ch in enumerate(s):
        if in_str:
            if ch == in_str and s[i-1] != '\\':
                in_str = None
        elif ch in ('"', "'"):
            in_str = ch
        elif ch == '#':
            value_part, comment_part = s[:i].rstrip(), s[i:]
            break
    return value_part, comment_part

# Walk the lines tracking nesting via indentation. When the leaf key in one of
# the target paths matches at the right nested location, rewrite its value.
stack = []  # [(indent, key)]
changed = []
for idx, raw in enumerate(lines):
    line = raw.rstrip("\n")
    m = key_re.match(line)
    if not m:
        continue
    indent = len(m.group("indent"))
    key = m.group("key")
    rest = m.group("rest")
    # Pop stack entries at the same or deeper level.
    while stack and stack[-1][0] >= indent:
        stack.pop()

    full_path = ".".join([s[1] for s in stack] + [key])
    value_part, comment_part = parse_current_value(rest)

    if full_path in updates and value_part != "":
        new_val = updates[full_path]
        # Always emit double-quoted — keys in this block are string scalars.
        new_val_escaped = new_val.replace("\\", "\\\\").replace('"', '\\"')
        new_rest = f' "{new_val_escaped}"'
        if comment_part:
            new_rest = f'{new_rest}  {comment_part}'
        new_line = f'{m.group("indent")}{key}:{new_rest}'
        if new_line != line:
            changed.append((full_path, value_part.strip(), new_val))
            lines[idx] = new_line + ("\n" if raw.endswith("\n") else "")
    # Push only when this line opens a mapping (no value on this line).
    if value_part == "":
        stack.append((indent, key))

# Report
missing = [p for p in updates if p not in {c[0] for c in changed} and p not in {c[0] for c in changed}]
# Fix: missing = paths never encountered
encountered = {c[0] for c in changed}
# Still-missing paths either had a value that already matches, or were absent.
still_needed = set(updates) - encountered
# Distinguish "already correct" from "absent": re-scan.
absent = []
for path in still_needed:
    present = False
    stack2 = []
    for raw in lines:
        line = raw.rstrip("\n")
        m = key_re.match(line)
        if not m: continue
        ind = len(m.group("indent"))
        while stack2 and stack2[-1][0] >= ind: stack2.pop()
        fp = ".".join([s[1] for s in stack2] + [m.group("key")])
        vp, _ = parse_current_value(m.group("rest"))
        if vp == "":
            stack2.append((ind, m.group("key")))
        if fp == path:
            present = True; break
    if not present:
        absent.append(path)

if changed:
    print(f"==> {len(changed)} field(s) to update:")
    for path, old, new in changed:
        old_disp = old[:40] + ("…" if len(old) > 40 else "")
        new_disp = new[:40] + ("…" if len(new) > 40 else "")
        print(f"    {path}")
        print(f"      -  {old_disp}")
        print(f"      +  {new_disp}")
else:
    print("==> no changes (fetched values already match conf.yml)")

if absent:
    print("WARN: the following conf.yml paths were not found — the fetch script cannot populate them:", file=sys.stderr)
    for p in absent:
        print(f"      {p}", file=sys.stderr)

new_text = "".join(lines)
if dry_run:
    if new_text != original:
        print("\n==> DRY-RUN diff (no file written):")
        diff = difflib.unified_diff(
            original.splitlines(keepends=True),
            new_text.splitlines(keepends=True),
            fromfile=conf_path, tofile=conf_path + " (proposed)",
        )
        sys.stdout.writelines(diff)
    sys.exit(0)

if new_text == original:
    sys.exit(0)

if not no_backup:
    shutil.copy2(conf_path, conf_path + ".bak")
    print(f"==> backed up: {conf_path}.bak")

with open(conf_path, "w") as f:
    f.write(new_text)
print(f"==> updated: {conf_path}")
PY
