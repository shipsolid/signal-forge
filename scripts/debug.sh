#!/usr/bin/env bash
# debug.sh — triage why signal-forge metrics are not flowing to Grafana Cloud.
#
# Assumes a k3d cluster created by deploy-local.sh with monitoring.mode=cloud.
# Runs a series of read-only checks; prints "OK" / "WARN" / "FAIL" per section.
#
# Usage:
#   ./scripts/debug.sh                         # run all sections against ../conf.yml
#   CONF_FILE=/path/to/conf.yml ./scripts/debug.sh

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
CONF="${CONF_FILE:-${REPO_DIR}/conf.yml}"
# Prefer cluster.namespace; fall back to legacy top-level namespace.
NS="$(python3 -c "import yaml; d=yaml.safe_load(open('$CONF')); print((d.get('cluster') or {}).get('namespace') or d.get('namespace') or '')")"
MODE="$(python3 -c "import yaml; print(yaml.safe_load(open('$CONF'))['monitoring']['mode'])")"
SECRET_NAME="$(python3 -c "import yaml; print((yaml.safe_load(open('$CONF')).get('monitoring') or {}).get('secret_name') or 'grafana-cloud-secrets')")"

bold()   { printf '\033[1m%s\033[0m\n' "$*"; }
ok()     { printf '  \033[1;32mOK\033[0m   %s\n' "$*"; }
warn()   { printf '  \033[1;33mWARN\033[0m %s\n' "$*"; }
fail()   { printf '  \033[1;31mFAIL\033[0m %s\n' "$*"; }
info()   { printf '       %s\n' "$*"; }
hr()     { printf '\n── %s ──────────────────────────────────────\n' "$*"; }

# ── 1. Config sanity ─────────────────────────────────────────────────────────
hr "1. conf.yml values"
bold "monitoring.mode"
info "$MODE"
[[ "$MODE" == "cloud" ]] || warn "mode is not 'cloud' — grafana-cloud pipeline inactive"

python3 <<PYEOF
import yaml
doc = yaml.safe_load(open("$CONF"))
gc = (doc.get("monitoring") or {}).get("grafana_cloud") or {}
# tempo/mimir/loki use {endpoint, user}; faro uses {endpoint, api_key}
for k, second_key in (("tempo", "user"), ("mimir", "user"), ("loki", "user"), ("faro", "api_key")):
    sub = gc.get(k) or {}
    v2 = sub.get(second_key, "")
    if second_key == "api_key" and v2:
        v2 = (v2[:8] + "..." + v2[-4:]) if len(v2) > 12 else "<set>"
    print(f"  {k:6s}  endpoint={sub.get('endpoint','<missing>')}  {second_key}={v2 or '<missing>'}")
PYEOF

# ── 2. Cluster / pod state ───────────────────────────────────────────────────
hr "2. Pod state"
if ! kubectl cluster-info >/dev/null 2>&1; then
  fail "kubectl cannot reach the cluster"; exit 1
fi
kubectl -n "$NS" get pods 2>/dev/null || warn "ns/$NS not found"
echo
kubectl -n monitoring get pods 2>/dev/null || info "no ns/monitoring (--with-helm not used)"

# ── 3. Credentials secret ────────────────────────────────────────────────────
hr "3. ${SECRET_NAME}"
if ! kubectl -n "$NS" get secret "$SECRET_NAME" >/dev/null 2>&1; then
  fail "secret ${SECRET_NAME} missing in ns/$NS"
else
  kubectl -n "$NS" get secret "$SECRET_NAME" -o json \
    | python3 -c "
import sys, json, base64
d = json.load(sys.stdin).get('data', {})
for k in sorted(d):
    v = base64.b64decode(d[k]).decode(errors='replace')
    if k in ('GRAFANA_CLOUD_API_KEY', 'FARO_API_KEY'):
        v = (v[:8] + '...' + v[-4:]) if len(v) > 12 else '<empty>'
    if not v:
        v = '<empty>'
    print(f'  {k}={v}')
"
fi

# ── 4. Active Alloy agent — mode-dependent ───────────────────────────────────
# cloud mode: Helm chart's alloy-metrics in ns/monitoring is the cloud exporter.
# local mode: bespoke DaemonSet `alloy` in ns/otel-lab exports to in-cluster stores.
hr "4. Active Alloy agent (mode=$MODE)"
ALLOY_NS=""; ALLOY_POD=""
if [[ "$MODE" == "cloud" ]]; then
  ALLOY_NS="$(python3 -c "import yaml; print(yaml.safe_load(open('$CONF'))['monitoring']['helm']['namespace'])")"
  ALLOY_POD="$(kubectl -n "$ALLOY_NS" get pods -l app.kubernetes.io/name=alloy-metrics -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
  if [[ -z "$ALLOY_POD" ]]; then
    fail "no alloy-metrics pod in ns/$ALLOY_NS (helm release not installed?)"
  fi
else
  ALLOY_NS="$NS"
  kubectl -n "$ALLOY_NS" get ds alloy 2>/dev/null || fail "ds/alloy missing in ns/$ALLOY_NS (expected in local mode)"
  ALLOY_POD="$(kubectl -n "$ALLOY_NS" get pods -l app=alloy -o jsonpath='{.items[0].metadata.name}' 2>/dev/null || true)"
fi
info "alloy pod: ${ALLOY_NS}/${ALLOY_POD:-<none>}"

if [[ -n "$ALLOY_POD" ]]; then
  bold "exporter-related log lines (last 1000)"
  kubectl -n "$ALLOY_NS" logs "$ALLOY_POD" -c alloy --tail=1000 2>&1 \
    | grep -v -E 'tailer stopped|client-side throttling|finished node evaluation|now listening|Using pod service account|scheduling loaded|Building spanmetrics|applying non-TLS|peers changed|starting cluster|starting server|finished complete|usage stats|GOMEMLIMIT|deprecated|one or more paths|k8s filtering|register collector with remote|Waited before|starting complete graph|failed to register collector|noop client|start tailing file|stopped tailing file|Done replaying WAL' \
    | grep -iE 'error|fail|refused|denied|unauthori|\b40[134]\b|\b5[0-9][0-9]\b|permanent|otlphttp\.|otlp\.|mimir|tempo|grafana_cloud|loki\.write|prometheus\.remote_write' \
    | tail -40 \
    || info "no exporter errors found in recent logs"
fi

# ── 5. App → Alloy wiring ────────────────────────────────────────────────────
hr "5. App OTEL env wiring"
# Shared env lives in the signal-forge-app-env ConfigMap (envFrom).
if kubectl -n "$NS" get cm signal-forge-app-env >/dev/null 2>&1; then
  bold "signal-forge-app-env ConfigMap (envFrom on every app Deployment)"
  kubectl -n "$NS" get cm signal-forge-app-env -o jsonpath='{range .data}{@}{"\n"}{end}' 2>/dev/null \
    | python3 -c "
import sys, json
d = json.loads(sys.stdin.read()) if False else None
raw = open('/dev/stdin').read() if False else None
"
  kubectl -n "$NS" get cm signal-forge-app-env -o json \
    | python3 -c "
import sys, json
d = json.load(sys.stdin).get('data', {})
for k in sorted(d):
    print(f'  {k}={d[k]}')"
else
  warn "signal-forge-app-env ConfigMap missing — apps will have no OTEL_EXPORTER_OTLP_ENDPOINT"
fi
echo
for dep in gateway-api order-api notification-svc otel-frontend; do
  if ! kubectl -n "$NS" get deploy "$dep" >/dev/null 2>&1; then
    info "$dep: not deployed"; continue
  fi
  bold "$dep (per-deployment env: block only)"
  kubectl -n "$NS" get deploy "$dep" -o json 2>/dev/null | python3 -c "
import sys, json
d = json.load(sys.stdin)
for c in d['spec']['template']['spec']['containers']:
    env = {e['name']: e.get('value', '') for e in c.get('env', []) if e.get('value') is not None}
    keys = [k for k in env if k.startswith('OTEL_') or k.startswith('FARO') or k.startswith('APP_')]
    if not keys:
        print(f'  {c[\"name\"]}: no per-deployment OTEL_/FARO env vars (all shared via ConfigMap)')
    for k in sorted(keys):
        print(f'  {c[\"name\"]}: {k}={env[k]}')"
done

# ── 6. Did data actually leave Alloy? ────────────────────────────────────────
# Scrape the active Alloy agent's self-metrics on :12345 via port-forward.
# Counters shown depend on mode:
#   cloud → prometheus remote_write counters (Mimir push path)
#   local → OTLP exporter counters (bespoke Alloy → in-cluster stores)
hr "6. Alloy self-metrics — did data leave the collector?"
if [[ -n "$ALLOY_POD" ]]; then
  tmp_log="$(mktemp)"
  kubectl -n "$ALLOY_NS" port-forward "pod/$ALLOY_POD" 12345:12345 >"$tmp_log" 2>&1 &
  PF_PID=$!
  for _ in 1 2 3 4 5 6; do
    grep -q "Forwarding from" "$tmp_log" 2>/dev/null && break
    sleep 0.5
  done
  metrics="$(curl -sS --max-time 5 http://127.0.0.1:12345/metrics 2>/dev/null || true)"
  kill "$PF_PID" 2>/dev/null; wait "$PF_PID" 2>/dev/null || true
  rm -f "$tmp_log"
  if [[ -z "$metrics" ]]; then
    warn "could not scrape alloy self-metrics"
  elif [[ "$MODE" == "cloud" ]]; then
    bold "prometheus.remote_write → Grafana Cloud (per-destination)"
    echo "$metrics" | grep -E '^prometheus_remote_storage_(samples_total|samples_failed_total|samples_pending|queue_highest_sent_timestamp_seconds)' \
      | sort | head -20 \
      || info "no remote_storage counters (metrics haven't flowed yet — wait a minute)"
  else
    bold "otelcol_exporter_sent_* (success counters)"
    echo "$metrics" | grep -E '^otelcol_exporter_sent_(metric_points|spans|log_records)_total' | sort | head -20
    echo
    bold "otelcol_exporter_send_failed_* (failure counters)"
    echo "$metrics" | grep -E '^otelcol_exporter_send_failed' | sort | head -20
    echo
    bold "otelcol_receiver_accepted_* (what came in from apps)"
    echo "$metrics" | grep -E '^otelcol_receiver_accepted_(metric_points|spans|log_records)' | sort | head -20
  fi
fi

# ── 7. Destination reachability probe ────────────────────────────────────────
# In cloud mode, probe the exact write URL that the active Alloy is configured
# to use (read from its rendered config), plus the credentials from the secret.
hr "7. Destination reachability"
if [[ "$MODE" == "cloud" && -n "$ALLOY_POD" ]]; then
  ACTUAL_URL="$(kubectl -n "$ALLOY_NS" exec "$ALLOY_POD" -c alloy -- \
    sh -c 'grep -m1 -oE "https://[^\"]*/api/prom/push" /etc/alloy/config.alloy 2>/dev/null' 2>/dev/null)"
  info "alloy-configured write URL: ${ACTUAL_URL:-<unable to read>}"
  MIMIR_USER="$(kubectl -n "$ALLOY_NS" get secret "$SECRET_NAME" -o jsonpath='{.data.GRAFANA_CLOUD_MIMIR_USER}' 2>/dev/null | base64 -d)"
  MIMIR_KEY="$(kubectl -n "$ALLOY_NS" get secret "$SECRET_NAME" -o jsonpath='{.data.GRAFANA_CLOUD_API_KEY}' 2>/dev/null | base64 -d)"
  if [[ -n "$ACTUAL_URL" && -n "$MIMIR_USER" && -n "$MIMIR_KEY" ]]; then
    # Empty POST — expect HTTP 400 ("snappy: corrupt input") from a healthy
    # Prometheus remote_write endpoint with valid creds. 401 = bad auth.
    # 404/405 = wrong path.
    kubectl -n "$ALLOY_NS" run curl-probe-$$ --rm -i --restart=Never --quiet \
      --image=curlimages/curl:8.10.1 --timeout=30s -- \
      curl -sS -o /dev/null -w 'HTTP %{http_code}  url=%{url_effective}\n' \
        -X POST --max-time 10 \
        -H "Content-Type: application/x-protobuf" \
        -u "${MIMIR_USER}:${MIMIR_KEY}" \
        "$ACTUAL_URL" 2>&1 | sed 's/^/       /' || true
    info "(expected: 400 'snappy: corrupt input' — endpoint is up, creds are good)"
  fi
else
  info "probe skipped (non-cloud mode)"
fi

# ── 8. App OTLP → receiver connectivity ─────────────────────────────────────
# In cloud mode, app OTLP goes to the Helm chart's alloy-receiver. Confirm the
# svc exists and endpoint-count matches pod-count.
hr "8. alloy-receiver svc endpoints (app OTLP ingress)"
if [[ "$MODE" == "cloud" ]]; then
  hns="$(python3 -c "import yaml; print(yaml.safe_load(open('$CONF'))['monitoring']['helm']['namespace'])")"
  kubectl -n "$hns" get svc,endpoints -l app.kubernetes.io/name=alloy-receiver 2>/dev/null | sed 's/^/       /' \
    || warn "no alloy-receiver svc in ns/$hns"
else
  info "N/A (not cloud mode)"
fi

hr "Done"
