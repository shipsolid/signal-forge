#!/usr/bin/env bash
# smoke-test-conf-updater.sh
#
# Offline regression test for the in-place env-file updater used by
# fetch-grafana-cloud-conf-from-akv.sh.
#
# Feeds synthetic values through the updater logic against a synthetic env
# file (nothing under version control is touched) and asserts:
#   - every target key is updated (7 pre-existing) or appended (2 missing —
#     exercises the "key not present yet" path)
#   - every comment line in the original file is preserved verbatim
#   - unrelated lines (ARM_*, a custom key) are untouched
#
# Run after any change to the updater logic in
# scripts/fetch-grafana-cloud-conf-from-akv.sh. Exits non-zero on any failure.
#
# Usage:
#   ./scripts/smoke-test-conf-updater.sh

set -euo pipefail

command -v python3 >/dev/null || { echo "ERROR: python3 required" >&2; exit 1; }

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
TMP_ENV="${TMP_DIR}/.env"

# Synthetic fixture: 7 of the 9 target keys pre-populated (to exercise the
# update-in-place path), 2 deliberately absent (to exercise the append path),
# plus comments and unrelated lines that must survive untouched.
cat > "$TMP_ENV" <<'ENV'
# =============================================================================
# Grafana Cloud credentials — sourced from Azure Key Vault.
# =============================================================================

# ── Azure Service Principal ──────────────────────────────────────────────────
ARM_CLIENT_ID=""
ARM_CLIENT_SECRET=""
ARM_TENANT_ID="tenant-123"
ARM_SUBSCRIPTION_ID="sub-456"
Resource_Group="rg-test"
Azure_KeyVault="kv-test"

# ── Grafana Cloud API key ────────────────────────────────────────────────────
GRAFANA_CLOUD_API_KEY=""

# ── Traces ────────────────────────────────────────────────────────────────────
GRAFANA_CLOUD_TEMPO_ENDPOINT=""
GRAFANA_CLOUD_TEMPO_USER=""

# ── Metrics ───────────────────────────────────────────────────────────────────
GRAFANA_CLOUD_MIMIR_ENDPOINT=""
GRAFANA_CLOUD_MIMIR_USER=""

# ── Logs ──────────────────────────────────────────────────────────────────────
GRAFANA_CLOUD_LOKI_ENDPOINT=""
GRAFANA_CLOUD_LOKI_USER=""

# Unrelated custom key that must survive untouched.
CUSTOM_UNRELATED_KEY="do-not-touch"
ENV

echo "==> smoke-testing updater against synthetic env file: $TMP_ENV"

# Fake values — chosen to be easy to eyeball in the diff.
FAKE_API_KEY="glc_TEST_API_KEY"
FAKE_TEMPO_EP="tempo-TEST.grafana.net:443"
FAKE_TEMPO_USER="T111"
FAKE_MIMIR_EP="https://prometheus-TEST.grafana.net/api/prom/push"
FAKE_MIMIR_USER="T222"
FAKE_LOKI_EP="https://logs-TEST.grafana.net/loki/api/v1/push"
FAKE_LOKI_USER="T333"
FAKE_FARO_EP="https://faro-TEST.grafana.net/faro/api/v1"
FAKE_FARO_KEY="glc_TEST_FARO_KEY"

# The Python here is a verbatim copy of the in-place updater in
# fetch-grafana-cloud-conf-from-akv.sh (see its `python3 - "$ENV_FILE" ... <<PY`
# block). When the updater is changed, update this copy too — the whole point
# of this script is to catch regressions in that logic.
python3 - "$TMP_ENV" 0 1 \
  "$FAKE_API_KEY" "$FAKE_TEMPO_EP" "$FAKE_TEMPO_USER" \
  "$FAKE_MIMIR_EP" "$FAKE_MIMIR_USER" \
  "$FAKE_LOKI_EP" "$FAKE_LOKI_USER" \
  "$FAKE_FARO_EP" "$FAKE_FARO_KEY" <<'PY'
import sys, re, difflib

env_path  = sys.argv[1]
dry_run   = sys.argv[2] == "1"
no_backup = sys.argv[3] == "1"
(api_key, tempo_ep, tempo_user,
 mimir_ep, mimir_user, loki_ep, loki_user,
 faro_ep, faro_api_key) = sys.argv[4:13]

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

new_text = "".join(lines)
with open(env_path, "w") as f:
    f.write(new_text)

# ── Assertions ──────────────────────────────────────────────────────────────
failed = []

# 1. All 9 keys updated or appended
changed_keys = {c[0] for c in changed}
still_missing = set(updates) - changed_keys
if still_missing:
    failed.append(f"FAIL: keys not updated/appended: {sorted(still_missing)}")

# 2. The 2 deliberately-absent keys were appended with the right values
for key in ("FARO_COLLECTOR_URL", "FARO_API_KEY"):
    entry = next((c for c in changed if c[0] == key), None)
    if not entry:
        failed.append(f"FAIL: {key} missing from updates")
    elif entry[1] != "":
        failed.append(f"FAIL: {key} expected to be newly-appended (old value empty), got old={entry[1]!r}")
    elif entry[2] != updates[key]:
        failed.append(f"FAIL: {key} got wrong value: {entry[2]!r}")

# 3. The 7 pre-existing keys were updated in place, not appended
for key in ("GRAFANA_CLOUD_API_KEY", "GRAFANA_CLOUD_TEMPO_ENDPOINT", "GRAFANA_CLOUD_TEMPO_USER",
            "GRAFANA_CLOUD_MIMIR_ENDPOINT", "GRAFANA_CLOUD_MIMIR_USER",
            "GRAFANA_CLOUD_LOKI_ENDPOINT", "GRAFANA_CLOUD_LOKI_USER"):
    entry = next((c for c in changed if c[0] == key), None)
    if not entry:
        failed.append(f"FAIL: {key} missing from updates")
    elif entry[2] != updates[key]:
        failed.append(f"FAIL: {key} got wrong value: {entry[2]!r}")

# 4. Every comment line survived verbatim
orig_comments = [l for l in original.splitlines() if l.lstrip().startswith("#")]
new_comments  = [l for l in new_text.splitlines()  if l.lstrip().startswith("#")]
if orig_comments != new_comments[:len(orig_comments)]:
    failed.append(f"FAIL: original comment lines were altered ({len(orig_comments)} → {len(new_comments)})")

# 5. Unrelated lines (ARM_*, custom key) untouched
for unrelated in ('ARM_CLIENT_ID=""', 'ARM_CLIENT_SECRET=""', 'ARM_TENANT_ID="tenant-123"',
                  'ARM_SUBSCRIPTION_ID="sub-456"', 'Resource_Group="rg-test"',
                  'Azure_KeyVault="kv-test"', 'CUSTOM_UNRELATED_KEY="do-not-touch"'):
    if unrelated not in new_text:
        failed.append(f"FAIL: unrelated line was modified: {unrelated!r}")

# Report
print()
print("=== Updates applied ===")
for key, old, new in changed:
    old_disp = (old[:40] + "…") if len(old) > 40 else old
    new_disp = (new[:40] + "…") if len(new) > 40 else new
    print(f"  {key}")
    print(f"    -  {old_disp}")
    print(f"    +  {new_disp}")

print()
print("=== Unified diff ===")
sys.stdout.writelines(difflib.unified_diff(
    original.splitlines(keepends=True),
    new_text.splitlines(keepends=True),
    fromfile=".env (original)", tofile=".env (after)", n=1,
))

print()
if failed:
    print("=== RESULT ===")
    for f in failed:
        print(f"  {f}")
    sys.exit(1)
else:
    print("=== RESULT: all assertions passed ===")
PY
