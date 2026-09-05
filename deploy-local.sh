#!/usr/bin/env bash
# deploy-local.sh — stand up signal-forge on local k3d.
# All knobs live in conf.yml (override with --config <path>).
#
# Usage:
#   ./deploy-local.sh                   # cluster + build + apply
#   ./deploy-local.sh --skip-build      # reuse existing images
#   ./deploy-local.sh --skip-cluster    # reuse existing k3d cluster
#   ./deploy-local.sh --with-helm       # (local mode only) also install grafana/k8s-monitoring
#   ./deploy-local.sh --teardown        # delete the k3d cluster and exit
#   ./deploy-local.sh -c custom.yml     # use a different config file
#
# In monitoring.mode=cloud the Helm release is MANDATORY and auto-installed —
# --with-helm is only meaningful in local mode.

set -euo pipefail

# ── Paths ────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONF="${SCRIPT_DIR}/conf.yml"

# ── Flags ────────────────────────────────────────────────────────────────────
SKIP_BUILD=0
SKIP_CLUSTER=0
WITH_HELM=0
TEARDOWN=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-build)   SKIP_BUILD=1 ;;
    --skip-cluster) SKIP_CLUSTER=1 ;;
    --with-helm)    WITH_HELM=1 ;;
    --teardown)     TEARDOWN=1 ;;
    -c|--config)    CONF="$2"; shift ;;
    -h|--help)      sed -n '2,15p' "$0"; exit 0 ;;
    *) echo "unknown flag: $1" >&2; exit 2 ;;
  esac
  shift
done

[[ -f "$CONF" ]] || { echo "config not found: $CONF" >&2; exit 1; }
CONF_DIR="$(cd "$(dirname "$CONF")" && pwd)"

# ── Logging ──────────────────────────────────────────────────────────────────
log()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n'  "$*"; }
die()  { printf '\033[1;31mxx\033[0m %s\n' "$*" >&2; exit 1; }

# ── YAML access (python3 — same dep the Makefile already uses) ──────────────
# Usage: yq <dotted.path>[.key][[index]]  → prints scalar or newline-joined list.
yq() {
  python3 - "$CONF" "$1" <<'PY'
import sys, yaml, re
path = sys.argv[2]
with open(sys.argv[1]) as f:
    doc = yaml.safe_load(f)
cur = doc
try:
    for part in re.findall(r'[^.\[\]]+|\[\d+\]', path):
        if part.startswith('['):
            cur = cur[int(part[1:-1])]
        else:
            cur = cur[part]
except (KeyError, IndexError, TypeError):
    sys.exit(0)  # missing key → empty output, not an error
if isinstance(cur, list):
    print('\n'.join(str(x) for x in cur))
elif isinstance(cur, dict):
    # Dict: print "key=value" pairs so callers can loop safely.
    for k, v in cur.items():
        print(f"{k}={v}")
elif cur is None:
    pass
else:
    print(cur)
PY
}

command -v python3 >/dev/null || die "python3 is required to parse conf.yml"
python3 -c 'import yaml' 2>/dev/null || die "python3 'pyyaml' module required: pip install pyyaml"

# ── Prereq check ─────────────────────────────────────────────────────────────
for bin in k3d docker kubectl; do
  command -v "$bin" >/dev/null || die "$bin not found on PATH"
done

# ── Resolve core knobs (with back-compat fallbacks) ─────────────────────────
CLUSTER="$(yq cluster.name)"
# Prefer cluster.namespace; fall back to legacy top-level namespace.
NAMESPACE="$(yq cluster.namespace)"
[[ -n "$NAMESPACE" ]] || NAMESPACE="$(yq namespace)"
[[ -n "$NAMESPACE" ]] || die "cluster.namespace is required"

CA_PATH="$(yq corporate_ca.path)"
CA_SETTLE="$(yq corporate_ca.settle_seconds)"
# Name of the ConfigMap carrying the corporate CA into the alloy-logs pod
# (see apply_corporate_ca_configmap() + the alloy-logs: block in
# values-cloud.yaml.tmpl). Not a conf.yml knob — internal wiring constant,
# same treatment as the hardcoded "frontend-env-js" ConfigMap name.
CORPORATE_CA_CONFIGMAP_NAME="corporate-ca-cert"
IMG_TAG="$(yq images.tag)"
MONITORING_MODE="$(yq monitoring.mode)"

SECRET_NAME="$(yq monitoring.secret_name)"
[[ -n "$SECRET_NAME" ]] || SECRET_NAME="grafana-cloud-secrets"

DEPLOYMENT_ENV="$(yq monitoring.deployment_environment)"
[[ -n "$DEPLOYMENT_ENV" ]] || DEPLOYMENT_ENV="signal-forge-dev"

if [[ -n "$CA_PATH" && "$CA_PATH" != /* ]]; then
  CA_PATH="${CONF_DIR}/${CA_PATH}"
fi

# ── Grafana Cloud credential source ─────────────────────────────────────────
# monitoring.grafana_cloud.use_env is a required path to an env file to
# source (relative to CONF_DIR unless absolute), defining GRAFANA_CLOUD_*/
# FARO_* keys — the same ones the legacy Makefile flow
# (`make secrets-fetch-akv`) uses. Populated by
# scripts/fetch-grafana-cloud-conf-from-akv.sh.
USE_ENV="$(yq monitoring.grafana_cloud.use_env)"
[[ -n "$USE_ENV" ]] || die "monitoring.grafana_cloud.use_env is required in ${CONF}"
ENV_FILE="$USE_ENV"
[[ "$ENV_FILE" == /* ]] || ENV_FILE="${CONF_DIR}/${ENV_FILE}"
[[ -f "$ENV_FILE" ]] || die "monitoring.grafana_cloud.use_env=${USE_ENV} but ${ENV_FILE} not found"
set -a
# shellcheck disable=SC1090
source "$ENV_FILE"
set +a
GC_API_KEY="${GRAFANA_CLOUD_API_KEY:-}"
GC_TEMPO_ENDPOINT="${GRAFANA_CLOUD_TEMPO_ENDPOINT:-}"
GC_TEMPO_USER="${GRAFANA_CLOUD_TEMPO_USER:-}"
GC_MIMIR_ENDPOINT="${GRAFANA_CLOUD_MIMIR_ENDPOINT:-}"
GC_MIMIR_USER="${GRAFANA_CLOUD_MIMIR_USER:-}"
GC_LOKI_ENDPOINT="${GRAFANA_CLOUD_LOKI_ENDPOINT:-}"
GC_LOKI_USER="${GRAFANA_CLOUD_LOKI_USER:-}"
GC_FARO_ENDPOINT="${FARO_COLLECTOR_URL:-}"
GC_FARO_API_KEY="${FARO_API_KEY:-}"

# ── Context guard ────────────────────────────────────────────────────────────
# Refuse to touch any cluster other than the k3d cluster named in cluster.name.
# This exists because kubectl contexts are global per-user state: a background
# `az aks get-credentials` / `az login` can silently switch the active context
# to a remote AKS cluster. Without this guard, `--skip-cluster` (which trusts
# whatever context is active) would happily deploy into production.
assert_k3d_context() {
  local expected="k3d-${CLUSTER}"
  local actual
  actual="$(kubectl config current-context 2>/dev/null || true)"
  if [[ "$actual" != "$expected" ]]; then
    die "current kubectl context is '${actual:-<none>}', expected '${expected}'. Switch with: kubectl config use-context ${expected}   (or run without --skip-cluster to recreate the k3d cluster)"
  fi
  # Extra belt-and-braces: ensure the cluster actually exists in k3d's registry.
  if ! k3d cluster list 2>/dev/null | awk 'NR>1 {print $1}' | grep -qx "$CLUSTER"; then
    die "kubectl context says '${expected}' but k3d has no cluster named '${CLUSTER}'. Stale context? Run: kubectl config delete-context ${expected}"
  fi
}

# ── Teardown shortcut ────────────────────────────────────────────────────────
if [[ "$TEARDOWN" -eq 1 ]]; then
  log "deleting k3d cluster: $CLUSTER"
  k3d cluster delete "$CLUSTER" || true
  exit 0
fi

# ── 1. Cluster up ────────────────────────────────────────────────────────────
# Render k3d `-p host:target` pairs from cluster.ports[] entries.
k3d_port_args() {
  python3 - "$CONF" <<'PY'
import sys, yaml
doc = yaml.safe_load(open(sys.argv[1])) or {}
for p in (doc.get("cluster") or {}).get("ports") or []:
    host = p.get("host")
    target = p.get("target")
    if host and target:
        print(f"{host}:{target}")
PY
}

cluster_up() {
  if k3d cluster list | awk 'NR>1 {print $1}' | grep -qx "$CLUSTER"; then
    log "cluster '$CLUSTER' already exists — reusing"
    return
  fi
  log "creating k3d cluster '$CLUSTER'"
  local port_args=()
  while IFS= read -r p; do port_args+=( -p "$p" ); done < <(k3d_port_args)
  k3d cluster create "$CLUSTER" "${port_args[@]}"
}

# Ensure every `nodePort:` in any Service manifest matches a NodePort declared
# in cluster.ports[]. Catches drift between conf.yml and svc YAMLs at deploy time.
check_nodeport_drift() {
  python3 - "$CONF" "$SCRIPT_DIR" <<'PY' || die "NodePort drift detected — update conf.yml cluster.ports or the Service manifest"
import sys, yaml, re, os, glob
conf_path, script_dir = sys.argv[1], sys.argv[2]

doc = yaml.safe_load(open(conf_path)) or {}
declared = set()
for p in (doc.get("cluster") or {}).get("ports") or []:
    target = p.get("target") or ""
    m = re.match(r"^(\d+)@server", target)   # nodeport@server:N
    if m:
        declared.add(int(m.group(1)))

errors = []
for path in glob.glob(os.path.join(script_dir, "k8s/**/*.yaml"), recursive=True):
    try:
        with open(path) as f:
            for obj in yaml.safe_load_all(f):
                if not obj or obj.get("kind") != "Service":
                    continue
                for port in (obj.get("spec") or {}).get("ports") or []:
                    np = port.get("nodePort")
                    if np and int(np) not in declared:
                        rel = os.path.relpath(path, script_dir)
                        errors.append(f"  {rel}: Service '{obj['metadata']['name']}' nodePort={np} is not declared in cluster.ports[]")
    except yaml.YAMLError:
        pass  # templates with ${...} placeholders, skip

if errors:
    print("cluster.ports[].target NodePorts declared:", sorted(declared), file=sys.stderr)
    for e in errors: print(e, file=sys.stderr)
    sys.exit(1)
PY
}

inject_corp_ca() {
  if [[ ! -f "$CA_PATH" ]]; then
    log "no corporate CA at $CA_PATH — skipping injection"
    return
  fi
  log "injecting corporate CA into k3d-${CLUSTER}-server-0"
  docker cp "$CA_PATH" "k3d-${CLUSTER}-server-0:/tmp/zcert.crt"
  docker exec "k3d-${CLUSTER}-server-0" sh -c \
    "mkdir -p /usr/local/share/ca-certificates && cp /tmp/zcert.crt /usr/local/share/ca-certificates/zcert.crt && cat /tmp/zcert.crt >> /etc/ssl/certs/ca-certificates.crt && rm /tmp/zcert.crt"
  log "restarting k3d-${CLUSTER}-server-0 so k3s/containerd picks up the updated CA trust"
  docker restart "k3d-${CLUSTER}-server-0" >/dev/null
  log "waiting ${CA_SETTLE}s for k3s to stabilise"
  sleep "$CA_SETTLE"
  docker exec "k3d-${CLUSTER}-serverlb" nginx -s reload
  log "nginx LB reloaded — kubectl should be responsive"
}

# ── 2. Build + import images ─────────────────────────────────────────────────
# Resolve a build arg: shell env wins over conf.yml path. Emits "NAME=VALUE"
# lines to stdout; empty when both sources are unset.
resolve_build_args() {
  local idx="$1"
  # build_args_from_env: list of shell var names
  while IFS= read -r env_name; do
    [[ -z "$env_name" ]] && continue
    local val="${!env_name-}"
    if [[ -n "$val" ]]; then
      printf '%s=%s\n' "$env_name" "$val"
    fi
  done < <(yq "images.builds[$idx].build_args_from_env")

  # build_args_from_conf: map of arg_name → conf.yml dotted path.
  # Skip any arg_name already emitted via env (shell wins).
  python3 - "$CONF" "$idx" <<'PY'
import sys, yaml, os
conf_path = sys.argv[1]
idx = int(sys.argv[2])
with open(conf_path) as f:
    doc = yaml.safe_load(f) or {}
build = (doc.get("images") or {}).get("builds") or []
if idx >= len(build):
    sys.exit(0)
mapping = (build[idx] or {}).get("build_args_from_conf") or {}
env_list = (build[idx] or {}).get("build_args_from_env") or []
env_set = {name: os.environ.get(name, "") for name in env_list}
for arg_name, path in mapping.items():
    # Shell env wins
    if env_set.get(arg_name, ""):
        continue
    cur = doc
    ok = True
    import re
    for part in re.findall(r'[^.\[\]]+|\[\d+\]', path):
        try:
            if part.startswith('['):
                cur = cur[int(part[1:-1])]
            else:
                cur = cur[part]
        except (KeyError, IndexError, TypeError):
            ok = False
            break
    if ok and cur not in (None, ""):
        print(f"{arg_name}={cur}")
PY
}

build_images() {
  local count
  count="$(python3 -c "import yaml; print(len(yaml.safe_load(open('$CONF'))['images']['builds']))")"
  for (( i=0; i<count; i++ )); do
    local name ctx inject stage_proto
    name="$(yq "images.builds[$i].name")"
    ctx="$(yq "images.builds[$i].context")"
    inject="$(yq "images.builds[$i].inject_ca")"
    stage_proto="$(yq "images.builds[$i].stage_shared_proto")"
    local ctx_abs="${SCRIPT_DIR}/${ctx}"

    log "build: $name:$IMG_TAG  (context: $ctx)"
    # BuildKit mounts this only for dependency restore. Nothing is copied into
    # the context, and non-corporate machines simply omit the optional secret.
    local build_secrets=()
    if [[ ( "$inject" == "True" || "$inject" == "true" ) && -s "$CA_PATH" ]]; then
      build_secrets+=( --secret "id=corporate_ca,src=${CA_PATH}" )
    fi

    # Docker build context is scoped to $ctx_abs — order-api/gateway-api's
    # .csproj references ../proto (src/proto/, the single source of truth
    # for orders.proto + OrderValidation.cs), which is outside that context
    # and invisible to `docker build` no matter what relative path the
    # .csproj uses. Stage a copy at $ctx_abs/proto/ so the Dockerfile's
    # `COPY proto/ /proto/` can put it where the container's /src/../proto
    # (i.e. /proto) resolution expects it. Direct `dotnet build`/`dotnet test`
    # outside Docker are unaffected — they resolve ../proto against the real
    # filesystem, not a build context.
    local staged_proto=""
    if [[ "$stage_proto" == "True" || "$stage_proto" == "true" ]]; then
      staged_proto="${ctx_abs}/proto"
      rm -rf "$staged_proto"
      mkdir -p "$staged_proto"
      cp "${SCRIPT_DIR}/src/proto/orders.proto" "${SCRIPT_DIR}/src/proto/OrderValidation.cs" "$staged_proto/"
    fi

    trap '
      [[ -n "${staged_proto:-}" && -d "$staged_proto" ]] && rm -rf "$staged_proto"
    ' EXIT

    # Collect build args from shell env + conf.yml (env wins on conflict).
    # Warn on unresolved build_args_from_env entries.
    local -A seen=()
    local build_args=()
    while IFS='=' read -r k v; do
      [[ -z "$k" ]] && continue
      build_args+=( --build-arg "${k}=${v}" )
      seen[$k]=1
    done < <(resolve_build_args "$i")

    while IFS= read -r env_name; do
      [[ -z "$env_name" ]] && continue
      if [[ -z "${seen[$env_name]:-}" ]]; then
        warn "$name: $env_name not set in shell and no build_args_from_conf fallback — build arg omitted"
      fi
    done < <(yq "images.builds[$i].build_args_from_env")

    docker build --network=host -t "${name}:${IMG_TAG}" "${build_secrets[@]}" "${build_args[@]}" "$ctx_abs"

    if [[ -n "$staged_proto" && -d "$staged_proto" ]]; then
      rm -rf "$staged_proto"
      staged_proto=""
    fi
  done
}

import_images() {
  local refs=()
  local count
  count="$(python3 -c "import yaml; print(len(yaml.safe_load(open('$CONF'))['images']['builds']))")"
  for (( i=0; i<count; i++ )); do
    refs+=( "$(yq "images.builds[$i].name"):${IMG_TAG}" )
  done
  log "importing images into cluster: ${refs[*]}"
  k3d image import "${refs[@]}" -c "$CLUSTER"
}

# ── 3. Apply manifests ───────────────────────────────────────────────────────
# Per-entry:
#   file              → kubectl apply -f <file>
#   dir w/ kustomization.yaml → kubectl apply -k <dir>   (kustomize-native)
#   dir w/o kustomization.yaml → kubectl apply -f <dir>  (plain recursive-within-dir)
apply_stage() {
  local stage="$1"
  local paths=()
  local p
  while IFS= read -r p; do [[ -n "$p" ]] && paths+=( "${SCRIPT_DIR}/${p}" ); done < <(yq "manifests.${stage}")
  [[ ${#paths[@]} -gt 0 ]] || { warn "no manifests under manifests.${stage}"; return; }
  log "kubectl apply — stage: $stage"
  local target
  for target in "${paths[@]}"; do
    if [[ -d "$target" && -f "${target%/}/kustomization.yaml" ]]; then
      kubectl apply -k "$target"
    else
      kubectl apply -f "$target"
    fi
  done
}

apply_monitoring() {
  local mode="${MONITORING_MODE:-local}"
  local args=()
  local p

  while IFS= read -r p; do
    [[ -n "$p" ]] && args+=( -f "${SCRIPT_DIR}/${p}" )
  done < <(yq "monitoring.manifests.${mode}")

  if [[ "$mode" == "local" ]]; then
    render_local_alloy_configmap
  fi

  [[ ${#args[@]} -gt 0 ]] || { log "no monitoring manifests for mode=${mode} (cloud mode owns the pipeline via Helm)"; return; }
  log "kubectl apply — stage: monitoring (mode: $mode)"
  kubectl apply "${args[@]}"
}

# Render the local-mode Alloy DaemonSet's ConfigMap: splices the shared
# trace-correlation fragment (k8s/monitoring/grafana/shared/) into
# configmap.yaml.tmpl's ${TRACE_CORRELATION_STAGES} placeholder, indented to
# match its nesting inside loki.process, and stamps ${DEPLOYMENT_ENVIRONMENT}
# (env_label's trace/metric/log statements + loki.write's external_labels).
# See render_helm_values() below for the Helm-chart-values counterpart (same
# fragment, Helm-tpl-escaped instead).
render_local_alloy_configmap() {
  local fragment="${SCRIPT_DIR}/k8s/monitoring/grafana/shared/trace-correlation-stages.alloy"
  local tmpl="${SCRIPT_DIR}/k8s/monitoring/grafana/local/configmap.yaml.tmpl"
  [[ -f "$fragment" ]] || die "missing shared fragment: $fragment"
  [[ -f "$tmpl" ]] || die "missing template: $tmpl"
  local rendered; rendered="$(mktemp --suffix=.alloy-configmap.yaml)"
  python3 - "$fragment" "$tmpl" "$rendered" "$DEPLOYMENT_ENV" <<'PY'
import sys
from string import Template
fragment_path, tmpl_path, out_path, deploy_env = sys.argv[1:5]
with open(fragment_path) as f: lines = f.read().rstrip("\n").splitlines()
# Drop the fragment file's own doc-comment header — only splice the actual
# stage.* code, so the deployed ConfigMap doesn't carry documentation meant
# for someone editing the shared source file directly.
code = lines[next(i for i, l in enumerate(lines) if l.lstrip().startswith("stage.")):]
indented = "\n".join(("      " + line if line else line) for line in code)
with open(tmpl_path) as f: tmpl = f.read()
with open(out_path, "w") as f:
    f.write(Template(tmpl).substitute(
        TRACE_CORRELATION_STAGES=indented,
        DEPLOYMENT_ENVIRONMENT=deploy_env,
    ))
PY
  log "kubectl apply — ConfigMap alloy-config (local mode, trace-correlation from shared/) → ns/otel-lab"
  kubectl apply -f "$rendered"
  rm -f "$rendered"
}

# Render and apply the shared ConfigMap that every app Deployment consumes via
# envFrom. Derives the alloy-receiver URL + deployment_environment from conf.yml.
apply_app_env_configmap() {
  local helm_ns helm_release
  helm_ns="$(yq monitoring.helm.namespace)"
  helm_release="$(yq monitoring.helm.release)"
  local tmpl="${SCRIPT_DIR}/k8s/infra/app-env.yaml.tmpl"
  [[ -f "$tmpl" ]] || { warn "template missing: $tmpl (skipping app-env ConfigMap)"; return; }
  local rendered; rendered="$(mktemp --suffix=.app-env.yaml)"
  python3 - "$tmpl" "$rendered" "$NAMESPACE" "$helm_ns" "$helm_release" "$DEPLOYMENT_ENV" <<'PY'
import sys
from string import Template
tmpl_path, out_path, app_ns, helm_ns, helm_release, deploy_env = sys.argv[1:7]
subs = {
    "APP_NAMESPACE":          app_ns,
    "HELM_NAMESPACE":         helm_ns,
    "HELM_RELEASE":           helm_release,
    "DEPLOYMENT_ENVIRONMENT": deploy_env,
}
with open(tmpl_path) as f: tmpl = f.read()
with open(out_path, "w") as f: f.write(Template(tmpl).safe_substitute(subs))
PY
  log "kubectl apply — ConfigMap signal-forge-app-env → ns/$NAMESPACE"
  kubectl apply -f "$rendered"
  rm -f "$rendered"
}

apply_grafana_cloud_secret() {
  if [[ "${MONITORING_MODE:-local}" == "cloud" ]]; then
    local missing=0
    local name value
    for name in GC_API_KEY GC_TEMPO_ENDPOINT GC_TEMPO_USER GC_MIMIR_ENDPOINT GC_MIMIR_USER GC_LOKI_ENDPOINT GC_LOKI_USER; do
      value="${!name}"
      if [[ -z "$value" ]]; then
        warn "monitoring.mode=cloud but $name is empty (source: $([[ "$USE_ENV" == "true" ]] && echo ".env" || echo "conf.yml"))"
        missing=1
      fi
    done
    if [[ "$missing" -eq 1 ]]; then
      warn "Grafana Cloud exporters will stay unauthenticated until the missing values are filled in"
    fi
  fi

  # Mirror the secret into every namespace that needs it:
  #   - $NAMESPACE                for apps / FARO runtime / bespoke Alloy (local mode)
  #   - monitoring.helm.namespace for the k8s-monitoring chart's Alloy agents
  #     (they reference this secret via destinations[*].secret.name)
  local targets=( "$NAMESPACE" )
  if [[ "${MONITORING_MODE:-local}" == "cloud" ]]; then
    local helm_ns; helm_ns="$(yq monitoring.helm.namespace)"
    if [[ -n "$helm_ns" && "$helm_ns" != "$NAMESPACE" ]]; then
      kubectl get ns "$helm_ns" >/dev/null 2>&1 || kubectl create namespace "$helm_ns"
      targets+=( "$helm_ns" )
    fi
  fi

  local ns
  for ns in "${targets[@]}"; do
    log "kubectl apply — ${SECRET_NAME} → ns/$ns (source: $([[ "$USE_ENV" == "true" ]] && echo ".env" || echo "conf.yml"))"
    python3 - "$ns" "$SECRET_NAME" \
      "$GC_API_KEY" "$GC_TEMPO_ENDPOINT" "$GC_TEMPO_USER" \
      "$GC_MIMIR_ENDPOINT" "$GC_MIMIR_USER" "$GC_LOKI_ENDPOINT" "$GC_LOKI_USER" \
      "$GC_FARO_ENDPOINT" "$GC_FARO_API_KEY" <<'PY' | kubectl apply -f -
import sys, yaml

(namespace, secret_name, api_key, tempo_endpoint, tempo_user,
 mimir_endpoint, mimir_user, loki_endpoint, loki_user,
 faro_endpoint, faro_api_key) = sys.argv[1:12]

secret = {
    "apiVersion": "v1",
    "kind": "Secret",
    "metadata": {
        "name": secret_name,
        "namespace": namespace,
    },
    "type": "Opaque",
    "stringData": {
        "GRAFANA_CLOUD_API_KEY": api_key,
        "GRAFANA_CLOUD_TEMPO_ENDPOINT": tempo_endpoint,
        "GRAFANA_CLOUD_TEMPO_USER": tempo_user,
        "GRAFANA_CLOUD_MIMIR_ENDPOINT": mimir_endpoint,
        "GRAFANA_CLOUD_MIMIR_USER": mimir_user,
        "GRAFANA_CLOUD_LOKI_ENDPOINT": loki_endpoint,
        "GRAFANA_CLOUD_LOKI_USER": loki_user,
        "FARO_COLLECTOR_URL": faro_endpoint,
        "FARO_API_KEY": faro_api_key,
    },
}

print(yaml.safe_dump(secret, sort_keys=False), end="")
PY
  done
}

# Corporate CA (Zscaler) trust — alloy-logs ONLY. logs-prod-018.grafana.net is
# intercepted by Zscaler SSL inspection on this network; alloy-metrics/
# alloy-receiver's grafana.net hosts are not, so only alloy-logs needs this.
#
# The chart's Alloy subchart has no direct "trust this CA" knob — it exposes
# controller.volumes.extra / alloy.mounts.extra, which need a ConfigMap to
# reference by name. Must exist before `helm upgrade` runs.
#
# No-op in local mode, and a logged no-op in cloud mode when CA_PATH doesn't
# resolve to a real, non-empty file — zero new K8s objects for a
# non-corporate contributor running this same lab.
apply_corporate_ca_configmap() {
  [[ "${MONITORING_MODE:-local}" == "cloud" ]] || return 0

  if [[ ! -f "$CA_PATH" || ! -s "$CA_PATH" ]]; then
    log "no corporate CA at $CA_PATH — skipping ${CORPORATE_CA_CONFIGMAP_NAME} ConfigMap (alloy-logs will use its stock CA bundle)"
    return 0
  fi

  local helm_ns; helm_ns="$(yq monitoring.helm.namespace)"
  [[ -n "$helm_ns" ]] || die "monitoring.helm.namespace is required"
  kubectl get ns "$helm_ns" >/dev/null 2>&1 || kubectl create namespace "$helm_ns"

  log "kubectl apply — ${CORPORATE_CA_CONFIGMAP_NAME} ConfigMap (zcert.crt) → ns/$helm_ns"
  # data key "zcert.crt" must match the subPath used in values-cloud.yaml.tmpl's
  # alloy-logs.alloy.mounts.extra entry — keep both in sync.
  kubectl create configmap "$CORPORATE_CA_CONFIGMAP_NAME" \
    --from-file="zcert.crt=${CA_PATH}" \
    -n "$helm_ns" \
    --dry-run=client -o yaml | kubectl apply -f -
}

# Frontend runtime config (env.js). Rendered straight from the already-resolved
# GC_FARO_ENDPOINT value — no K8s Secret round-trip needed, this ConfigMap IS
# the delivery mechanism now, mounted read-only via subPath so the frontend
# container can run with readOnlyRootFilesystem: true (see
# k8s/app/frontend/deployment.yaml; the image's baked-in default env.js is
# what a bare `docker run` without this ConfigMap falls back to).
apply_frontend_env_configmap() {
  log "kubectl apply — frontend-env-js ConfigMap → ns/$NAMESPACE"
  python3 - "$NAMESPACE" "$GC_FARO_ENDPOINT" <<'PY' | kubectl apply -f -
import sys, yaml
namespace, faro_url = sys.argv[1], sys.argv[2]
env_js = (
    "window.__ENV = {\n"
    f'  FARO_URL: "{faro_url}",\n'
    '  API_BASE_URL: "/api"\n'
    "};\n"
)
cm = {
    "apiVersion": "v1",
    "kind": "ConfigMap",
    "metadata": {"name": "frontend-env-js", "namespace": namespace},
    "data": {"env.js": env_js},
}
print(yaml.safe_dump(cm, sort_keys=False), end="")
PY
}

wait_datastores() {
  local sel timeout
  sel="$(yq datastore_ready.selector)"
  timeout="$(yq datastore_ready.timeout)"
  log "waiting for datastore pods (-l $sel, timeout $timeout)"
  kubectl -n "$NAMESPACE" wait --for=condition=ready pod -l "$sel" --timeout="$timeout"
}

# ── 4. Helm release: grafana/k8s-monitoring ──────────────────────────────────
# Render a values.yaml template (${...} placeholders) → tmp file, then pass to
# helm. Non-credential substitutions come from conf.yml; the three endpoint
# values come from the resolved GC_* vars (sourced from the use_env file).
render_helm_values() {
  local tmpl="$1"   # abs path to .tmpl
  local out="$2"    # abs path for rendered output
  local fragment="${SCRIPT_DIR}/k8s/monitoring/grafana/shared/trace-correlation-stages.alloy"
  python3 - "$CONF" "$tmpl" "$out" "$fragment" "$CLUSTER" "$NAMESPACE" "$SECRET_NAME" "$DEPLOYMENT_ENV" \
    "$GC_MIMIR_ENDPOINT" "$GC_LOKI_ENDPOINT" "$GC_TEMPO_ENDPOINT" \
    "$CA_PATH" "$CORPORATE_CA_CONFIGMAP_NAME" <<'PY'
import os
import sys, yaml
from string import Template

(conf_path, tmpl_path, out_path, fragment_path, cluster_name, ns, secret_name, deploy_env,
 mimir_url, loki_url, tempo_endpoint, ca_path, ca_configmap_name) = sys.argv[1:14]
with open(conf_path) as f:
    doc = yaml.safe_load(f) or {}

helm = (doc.get("monitoring") or {}).get("helm") or {}

# Same shared fragment render_local_alloy_configmap() splices into local mode's
# configmap, but escaped for Helm's tpl pass (which runs over
# extraLogProcessingStages before Alloy ever sees it): literal Go-template
# delimiters must survive as text, not be evaluated by Helm's own templating.
with open(fragment_path) as f:
    lines = f.read().rstrip("\n").splitlines()
# Drop the fragment file's own doc-comment header — see the matching comment
# in render_local_alloy_configmap() above.
code = lines[next(i for i, l in enumerate(lines) if l.lstrip().startswith("stage.")):]
# Placeholder-swap, not two chained replace() calls: a plain
# .replace("{{",...).replace("}}",...) would re-match the "}}" that the first
# call just inserted (its replacement text itself contains "}}"), corrupting
# the escape. The \x00 placeholder is untouched by the "}}" pass, so each
# delimiter is escaped exactly once.
escaped = [
    l.replace("{{", "\x00").replace("}}", '{{"}}"}}').replace("\x00", '{{"{{"}}')
    for l in code
]
trace_correlation_escaped = "\n".join(
    ("    " + line if line else line) for line in escaped
)

# Corporate CA (Zscaler) mount for alloy-logs only — see
# apply_corporate_ca_configmap() and this template's alloy-logs: header
# comment for the full "why". Emitted as two full-line block substitutions
# (same technique as trace_correlation_escaped above): each fragment either
# contains a complete, correctly-indented YAML key or is the empty string —
# never a half-populated key — so a non-corporate render has zero
# CA-related keys anywhere in the output, not just blank ones.
ca_present = bool(ca_path) and os.path.isfile(ca_path) and os.path.getsize(ca_path) > 0
if ca_present:
    # Sibling of alloy-logs:'s enabled:/alloy:/remoteConfig: keys —
    # controller.volumes.extra lives under the separate top-level
    # controller: block, NOT nested under alloy:.
    ca_controller_block = (
        "  controller:\n"
        "    volumes:\n"
        "      extra:\n"
        "        - name: corporate-ca\n"
        "          configMap:\n"
        f"            name: {ca_configmap_name}\n"
    ).rstrip("\n")
    # Nested inside alloy-logs.alloy: (sibling of extraEnv:) — mounted as a
    # standalone file under /etc/ssl/certs/, NOT overwriting
    # ca-certificates.crt itself, so Go's crypto/x509 SystemCertPool picks it
    # up automatically (it scans certDirectories and appends every PEM file
    # found there) with no Alloy River config change. subPath must match the
    # data key apply_corporate_ca_configmap() writes into the ConfigMap.
    ca_mounts_block = (
        "    mounts:\n"
        "      extra:\n"
        "        - name: corporate-ca\n"
        "          mountPath: /etc/ssl/certs/corporate-ca.crt\n"
        "          subPath: zcert.crt\n"
        "          readOnly: true\n"
    ).rstrip("\n")
else:
    ca_controller_block = ""
    ca_mounts_block = ""

subs = {
    "CLUSTER_NAME":                      cluster_name,
    "DEPLOYMENT_ENVIRONMENT":            deploy_env,
    "SECRET_NAME":                       secret_name,
    "SECRET_NAMESPACE":                  helm.get("namespace") or ns,
    "MIMIR_URL":                         mimir_url,
    "LOKI_URL":                          loki_url,
    "TEMPO_ENDPOINT":                    tempo_endpoint,
    "TRACE_CORRELATION_STAGES_ESCAPED":  trace_correlation_escaped,
    "ALLOY_LOGS_CA_CONTROLLER_BLOCK":    ca_controller_block,
    "ALLOY_LOGS_CA_MOUNTS_BLOCK":        ca_mounts_block,
}

with open(tmpl_path) as f:
    tmpl = f.read()
# safe_substitute leaves un-matched ${FOO} alone instead of raising — tolerant.
rendered = Template(tmpl).safe_substitute(subs)
with open(out_path, "w") as f:
    f.write(rendered)
PY
}

# Assert every usernameKey / passwordKey referenced by the rendered values file
# exists as a stringData key in the Secret deploy-local.sh just wrote. Catches
# silent auth failures from renames on either side of the contract.
validate_secret_keys() {
  local rendered="$1"  # path to rendered values file
  local ns="$2"        # namespace the secret lives in
  python3 - "$rendered" "$ns" "$SECRET_NAME" <<'PY' || die "secret-key contract violation — see above"
import sys, yaml, subprocess, json
rendered_path, ns, secret_name = sys.argv[1], sys.argv[2], sys.argv[3]

with open(rendered_path) as f:
    values = yaml.safe_load(f) or {}
# Collect every key the chart will look up in the Secret.
wanted = set()
for dest in values.get("destinations") or []:
    auth = dest.get("auth") or {}
    for k in ("usernameKey", "passwordKey", "tokenKey"):
        v = auth.get(k)
        if v:
            wanted.add(v)

# Read what actually landed in the Secret.
try:
    out = subprocess.check_output(
        ["kubectl", "-n", ns, "get", "secret", secret_name, "-o", "json"],
        stderr=subprocess.DEVNULL,
    )
except subprocess.CalledProcessError:
    print(f"cannot read secret {ns}/{secret_name}", file=sys.stderr)
    sys.exit(1)
present = set((json.loads(out).get("data") or {}).keys())

missing = sorted(wanted - present)
if missing:
    print(f"Secret {ns}/{secret_name} is missing keys that values-cloud.yaml references:", file=sys.stderr)
    for k in missing:
        print(f"  {k}", file=sys.stderr)
    print(f"Secret currently contains: {sorted(present)}", file=sys.stderr)
    sys.exit(1)
PY
}

# SLO rules — local mode. k8s/monitoring/slo-rules.yaml is a bare Prometheus
# rule file (no CRD dependency); load it straight into the vanilla Prometheus
# Deployment via a generated ConfigMap + rule_files:. Single source of truth —
# see that file's header comment for the full consumption story.
apply_local_slo_rules() {
  # `return 0`, not bare `return`: under `set -e`, a bare `return` here would
  # propagate the exit status of the failed `[[ ]]` test (1) in cloud mode,
  # and since this function is called as a bare top-level statement, that
  # silently kills the whole script right after the datastore wait — every
  # cloud-mode deploy (the default mode) previously exited here without ever
  # reaching apply_monitoring/apply_stage app/apply_stage post/install_helm.
  [[ "${MONITORING_MODE:-local}" == "local" ]] || return 0
  local enabled rel path
  enabled="$(yq observability.slo_rules.enabled)"
  if [[ "$enabled" != "True" && "$enabled" != "true" ]]; then
    return
  fi
  rel="$(yq observability.slo_rules.manifest)"
  [[ -n "$rel" ]] || { warn "observability.slo_rules.enabled=true but manifest path missing"; return; }
  path="${SCRIPT_DIR}/${rel}"
  [[ -f "$path" ]] || die "slo rules manifest not found: $path"

  log "kubectl apply — prometheus-slo-rules ConfigMap (from $rel)"
  kubectl create configmap prometheus-slo-rules \
    --from-file="slo-rules.yml=${path}" \
    -n "$NAMESPACE" \
    --dry-run=client -o yaml | kubectl apply -f -

  # Best-effort reload: a first-ever deploy doesn't need this (the pod mounts
  # the ConfigMap at startup), but a redeploy onto an already-running
  # Prometheus does. Non-fatal — Prometheus may not be up yet on a cold
  # deploy, or the NodePort may not be reachable in every environment.
  if curl --max-time 5 --fail --silent --output /dev/null -X POST "http://localhost:9090/-/reload"; then
    log "prometheus reloaded — SLO rules active"
  fi
}

# SLO rules — Prometheus-Operator CRD path (kube-prometheus-stack), for anyone
# who installs it on top of either mode. Synthesizes the PrometheusRule wrapper
# on the fly from the same bare rule file so there's still only one copy of the
# rule content. When the CRD isn't present (the common case in cloud mode,
# since Grafana Cloud Mimir doesn't consume PrometheusRule CRDs at all), point
# at the real cloud-mode path instead of silently doing nothing.
apply_slo_rules() {
  local enabled rel path
  enabled="$(yq observability.slo_rules.enabled)"
  if [[ "$enabled" != "True" && "$enabled" != "true" ]]; then
    return
  fi
  rel="$(yq observability.slo_rules.manifest)"
  [[ -n "$rel" ]] || { warn "observability.slo_rules.enabled=true but manifest path missing"; return; }
  path="${SCRIPT_DIR}/${rel}"
  [[ -f "$path" ]] || die "slo rules manifest not found: $path"

  if ! kubectl get crd prometheusrules.monitoring.coreos.com >/dev/null 2>&1; then
    if [[ "${MONITORING_MODE:-local}" == "cloud" ]]; then
      log "SLO rules ready in $rel but not yet loaded into Grafana Cloud Mimir — run ./scripts/push-slo-rules-to-mimir.sh to load them"
    fi
    return
  fi

  log "kubectl apply — SLO rules as PrometheusRule ($rel, kube-prometheus-stack detected)"
  python3 - "$path" "$NAMESPACE" <<'PY' | kubectl apply -f -
import sys, yaml
path, namespace = sys.argv[1], sys.argv[2]
with open(path) as f:
    rules = yaml.safe_load(f)
wrapped = {
    "apiVersion": "monitoring.coreos.com/v1",
    "kind": "PrometheusRule",
    "metadata": {
        "name": "signal-forge-slos",
        "namespace": namespace,
        "labels": {
            "app.kubernetes.io/name": "signal-forge",
            "app.kubernetes.io/component": "slo-alerts",
            "prometheus": "signal-forge",
            "role": "alert-rules",
        },
    },
    "spec": {"groups": rules["groups"]},
}
print(yaml.safe_dump(wrapped, sort_keys=False), end="")
PY
}

# cert-manager install + self-signed ClusterIssuer bootstrap.
# Gated by security.tls.enabled in conf.yml.
install_cert_manager() {
  local tls_enabled
  tls_enabled="$(yq security.tls.enabled)"
  if [[ "$tls_enabled" != "True" && "$tls_enabled" != "true" ]]; then
    log "security.tls.enabled=false — skipping cert-manager install"
    return
  fi
  command -v helm >/dev/null || die "helm not found on PATH (required for cert-manager install)"

  local ns rel chart ver repo_name repo_url
  ns="$(yq security.tls.cert_manager.namespace)"
  rel="$(yq security.tls.cert_manager.release)"
  chart="$(yq security.tls.cert_manager.chart)"
  ver="$(yq security.tls.cert_manager.version)"
  repo_name="$(yq security.tls.cert_manager.repo.name)"
  repo_url="$(yq security.tls.cert_manager.repo.url)"

  log "helm repo: $repo_name → $repo_url (cert-manager)"
  helm repo add "$repo_name" "$repo_url" >/dev/null 2>&1 || true
  helm repo update >/dev/null

  log "helm upgrade --install $rel ($chart@$ver) → ns/$ns"
  helm upgrade --install "$rel" "$chart" \
    --version "$ver" \
    -n "$ns" --create-namespace \
    --set crds.enabled=true \
    --wait --timeout 3m

  # Wait for the cert-manager webhook to be ready before applying the Issuer
  # manifest (otherwise the CRD admission webhook rejects the CR).
  kubectl -n "$ns" wait --for=condition=available --timeout=120s deploy/cert-manager-webhook

  log "kubectl apply — cert-manager-issuer.yaml"
  kubectl apply -f "${SCRIPT_DIR}/k8s/infra/cert-manager-issuer.yaml"
}

install_helm() {
  command -v helm >/dev/null || die "helm not found on PATH (required for helm install)"
  local ns rel chart ver vfile_rel repo_name repo_url wait_flag timeout_flag
  ns="$(yq monitoring.helm.namespace)"
  rel="$(yq monitoring.helm.release)"
  chart="$(yq monitoring.helm.chart)"
  ver="$(yq monitoring.helm.version)"
  # Resolve values file: values_file_by_mode.<mode> preferred; fall back to legacy values_file.
  vfile_rel="$(yq "monitoring.helm.values_file_by_mode.${MONITORING_MODE}")"
  [[ -n "$vfile_rel" ]] || vfile_rel="$(yq monitoring.helm.values_file)"
  [[ -n "$vfile_rel" ]] || die "no values file configured for mode=${MONITORING_MODE} (set monitoring.helm.values_file_by_mode.${MONITORING_MODE})"
  local vfile="${SCRIPT_DIR}/${vfile_rel}"
  [[ -f "$vfile" ]] || die "values file not found: $vfile"
  repo_name="$(yq monitoring.helm.repo.name)"
  repo_url="$(yq monitoring.helm.repo.url)"

  # Template files (*.tmpl) get rendered via string-substitution from conf.yml.
  local final_vfile="$vfile"
  if [[ "$vfile" == *.tmpl ]]; then
    final_vfile="$(mktemp --suffix=.values.yaml)"
    log "rendering helm values template: $(basename "$vfile") → $(basename "$final_vfile")"
    render_helm_values "$vfile" "$final_vfile"
    trap '[[ -f "${final_vfile:-}" ]] && rm -f "$final_vfile"' EXIT
  fi

  # Optional wait/timeout flags.
  local wait_val timeout_val
  wait_val="$(yq monitoring.helm.wait)"
  timeout_val="$(yq monitoring.helm.timeout)"
  local helm_args=()
  if [[ "$wait_val" == "True" || "$wait_val" == "true" ]]; then
    helm_args+=( --wait )
    [[ -n "$timeout_val" ]] && helm_args+=( --timeout "$timeout_val" )
  fi

  # If this is the cloud pipeline, assert the Secret contains every key the
  # rendered values file will reference — before helm runs, not after agents
  # start returning 401s.
  if [[ "$MONITORING_MODE" == "cloud" ]]; then
    validate_secret_keys "$final_vfile" "$ns"
    log "secret key contract: OK"
  fi

  log "helm repo: $repo_name → $repo_url"
  helm repo add "$repo_name" "$repo_url" >/dev/null 2>&1 || true
  helm repo update >/dev/null

  log "helm upgrade --install $rel ($chart@$ver) → ns/$ns"
  helm upgrade --install "$rel" "$chart" \
    --version "$ver" \
    -n "$ns" --create-namespace \
    -f "$final_vfile" \
    "${helm_args[@]}"
}

# ── Run ──────────────────────────────────────────────────────────────────────
if [[ "$SKIP_CLUSTER" -eq 0 ]]; then
  cluster_up
  inject_corp_ca
  # `cluster_up` sets kubectl context to k3d-${CLUSTER} as a side effect of
  # `k3d cluster create`, but on a reused cluster the context may still point
  # elsewhere. Make it explicit either way.
  kubectl config use-context "k3d-${CLUSTER}" >/dev/null
else
  log "--skip-cluster — reusing current kube context"
fi

# Absolute guard: from here on, every kubectl/helm call must land on k3d.
assert_k3d_context
log "context verified: k3d-${CLUSTER}"

# NodePort drift: fail fast if a Service manifest references a NodePort not
# declared in cluster.ports[] (would leave the service unreachable via localhost).
check_nodeport_drift
log "nodeport consistency: OK"

if [[ "$SKIP_BUILD" -eq 0 ]]; then
  build_images
  import_images
else
  log "--skip-build — assuming images already in the cluster"
fi

apply_stage infra
apply_app_env_configmap
apply_grafana_cloud_secret
apply_corporate_ca_configmap      # must run before install_helm; needs monitoring.helm.namespace
apply_frontend_env_configmap
install_cert_manager
apply_stage datastores
wait_datastores
# Local-mode SLO rules ConfigMap must exist before the Prometheus Deployment
# (in apply_monitoring's manifest list) mounts it.
apply_local_slo_rules
apply_monitoring
apply_stage app
apply_stage post

# Helm: mandatory in cloud mode, opt-in (--with-helm) in local mode.
if [[ "$MONITORING_MODE" == "cloud" || "$WITH_HELM" -eq 1 ]]; then
  install_helm
fi

# SLO rules — Prometheus-Operator CRD path; skips cleanly (with a cloud-mode
# reminder) if the CRD isn't present.
apply_slo_rules

echo
log "signal-forge is up (monitoring.mode=${MONITORING_MODE})."
# Derive the endpoints banner from cluster.ports[].label / .credentials.
# Filter out entries whose `mode:` doesn't match the active monitoring mode
# (e.g. local-only Grafana/Jaeger/Prometheus are suppressed in cloud mode).
python3 - "$CONF" "$MONITORING_MODE" <<'PY'
import yaml, sys
doc = yaml.safe_load(open(sys.argv[1])) or {}
active_mode = sys.argv[2]
for p in (doc.get("cluster") or {}).get("ports") or []:
    entry_mode = p.get("mode")  # None = always print
    if entry_mode and entry_mode != active_mode:
        continue
    label = p.get("label") or ""
    host = p.get("host")
    creds = p.get("credentials") or ""
    # Allow per-entry url override (e.g. https://signal-forge.local:8443 for the
    # TLS port, where the hostname is not "localhost").
    url = p.get("url") or (f"http://localhost:{host}" if host else "")
    if label and url:
        line = f"    {label:<12} {url}"
        if creds:
            line = f"{line:<48}  ({creds})"
        print(line)
PY
