#!/usr/bin/env bash
# smoke-test-conf-updater.sh
#
# Offline regression test for the in-place conf.yml updater used by
# fetch-grafana-cloud-conf-from-akv.sh.
#
# Feeds synthetic values through the updater logic against a *copy* of
# conf.yml (the real file is never touched) and asserts:
#   - every target leaf path is updated (9 fields)
#   - the top-level grafana_cloud.api_key and the nested faro.api_key are
#     disambiguated correctly (they share a key name but different paths)
#   - every comment line in the original file is preserved verbatim
#   - every field outside monitoring.grafana_cloud.{api_key,tempo,mimir,loki,faro}
#     is untouched (e.g. the monitoring.grafana_cloud.akv.* block must survive)
#
# Run after any change to the updater logic in
# scripts/fetch-grafana-cloud-conf-from-akv.sh. Exits non-zero on any failure.
#
# Usage:
#   ./scripts/smoke-test-conf-updater.sh                       # against ../conf.yml
#   CONF_FILE=/path/to/other.yml ./scripts/smoke-test-conf-updater.sh

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CONF_FILE="${CONF_FILE:-${REPO_DIR}/conf.yml}"

[[ -f "$CONF_FILE" ]] || { echo "ERROR: conf file not found: $CONF_FILE" >&2; exit 1; }
command -v python3 >/dev/null || { echo "ERROR: python3 required" >&2; exit 1; }

TMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TMP_DIR"' EXIT
TMP_CONF="${TMP_DIR}/conf.yml"
cp "$CONF_FILE" "$TMP_CONF"

echo "==> smoke-testing updater against copy: $TMP_CONF"

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
# fetch-grafana-cloud-conf-from-akv.sh (see its `python3 - "$CONF_FILE" ... <<PY`
# block). When the updater is changed, update this copy too — the whole point
# of this script is to catch regressions in that logic.
python3 - "$TMP_CONF" 0 1 \
  "$FAKE_API_KEY" "$FAKE_TEMPO_EP" "$FAKE_TEMPO_USER" \
  "$FAKE_MIMIR_EP" "$FAKE_MIMIR_USER" \
  "$FAKE_LOKI_EP" "$FAKE_LOKI_USER" \
  "$FAKE_FARO_EP" "$FAKE_FARO_KEY" <<'PY'
import sys, re, shutil, difflib

conf_path = sys.argv[1]
dry_run   = sys.argv[2] == "1"
no_backup = sys.argv[3] == "1"
(api_key, tempo_ep, tempo_user,
 mimir_ep, mimir_user, loki_ep, loki_user,
 faro_ep, faro_api_key) = sys.argv[4:13]

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

key_re = re.compile(r'^(?P<indent>\s*)(?P<key>[A-Za-z0-9_]+)\s*:(?P<rest>.*)$')

def parse_current_value(rest: str):
    s = rest.lstrip()
    in_str = None
    value_part, comment_part = s, ""
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

stack = []
changed = []
for idx, raw in enumerate(lines):
    line = raw.rstrip("\n")
    m = key_re.match(line)
    if not m: continue
    indent = len(m.group("indent"))
    key = m.group("key")
    rest = m.group("rest")
    while stack and stack[-1][0] >= indent: stack.pop()
    full_path = ".".join([s[1] for s in stack] + [key])
    value_part, comment_part = parse_current_value(rest)
    if full_path in updates and value_part != "":
        new_val = updates[full_path]
        new_val_escaped = new_val.replace("\\", "\\\\").replace('"', '\\"')
        new_rest = f' "{new_val_escaped}"'
        if comment_part:
            new_rest = f'{new_rest}  {comment_part}'
        new_line = f'{m.group("indent")}{key}:{new_rest}'
        if new_line != line:
            changed.append((full_path, value_part.strip(), new_val))
            lines[idx] = new_line + ("\n" if raw.endswith("\n") else "")
    if value_part == "":
        stack.append((indent, key))

new_text = "".join(lines)
with open(conf_path, "w") as f:
    f.write(new_text)

# ── Assertions ──────────────────────────────────────────────────────────────
failed = []

# 1. All 9 paths updated
changed_paths = {c[0] for c in changed}
missing = set(updates) - changed_paths
if missing:
    failed.append(f"FAIL: paths not updated: {sorted(missing)}")

# 2. Top-level api_key and faro.api_key disambiguated — both present, different values
top_api = next((c for c in changed if c[0] == "monitoring.grafana_cloud.api_key"), None)
faro_api = next((c for c in changed if c[0] == "monitoring.grafana_cloud.faro.api_key"), None)
if not top_api or not faro_api:
    failed.append("FAIL: one of api_key / faro.api_key missing from updates")
elif top_api[2] == faro_api[2]:
    failed.append("FAIL: api_key and faro.api_key got the same value (disambiguation broken)")
elif top_api[2] != updates["monitoring.grafana_cloud.api_key"]:
    failed.append(f"FAIL: top api_key got wrong value: {top_api[2]!r}")
elif faro_api[2] != updates["monitoring.grafana_cloud.faro.api_key"]:
    failed.append(f"FAIL: faro.api_key got wrong value: {faro_api[2]!r}")

# 3. Every comment line survived verbatim
orig_comments = [l for l in original.splitlines() if l.lstrip().startswith("#")]
new_comments  = [l for l in new_text.splitlines()  if l.lstrip().startswith("#")]
if orig_comments != new_comments:
    failed.append(f"FAIL: comment lines changed ({len(orig_comments)} → {len(new_comments)})")

# 4. AKV block survived untouched
orig_akv = re.search(r'^\s{4}akv:\n(?:\s{6}.+\n)+', original, re.M)
new_akv  = re.search(r'^\s{4}akv:\n(?:\s{6}.+\n)+', new_text,  re.M)
if orig_akv and new_akv:
    if orig_akv.group(0) != new_akv.group(0):
        failed.append("FAIL: monitoring.grafana_cloud.akv block was modified")
else:
    failed.append("FAIL: could not locate akv block in one of the files")

# 5. Nothing outside grafana_cloud.{api_key,tempo,mimir,loki,faro} changed.
# Strategy: remove every updated line from both sides and compare the residuals.
updated_line_nums = set()
for idx, (raw_new, raw_old) in enumerate(zip(new_text.splitlines(), original.splitlines())):
    if raw_new != raw_old:
        updated_line_nums.add(idx)
# Each of the 9 updates should have changed exactly 1 line.
if len(updated_line_nums) != 9:
    failed.append(f"FAIL: expected 9 lines changed, got {len(updated_line_nums)}")

# Report
print()
print("=== Updates applied ===")
for path, old, new in changed:
    old_disp = (old[:40] + "…") if len(old) > 40 else old
    new_disp = (new[:40] + "…") if len(new) > 40 else new
    print(f"  {path}")
    print(f"    -  {old_disp}")
    print(f"    +  {new_disp}")

print()
print("=== Unified diff ===")
sys.stdout.writelines(difflib.unified_diff(
    original.splitlines(keepends=True),
    new_text.splitlines(keepends=True),
    fromfile="conf.yml (original)", tofile="conf.yml (after)", n=1,
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
