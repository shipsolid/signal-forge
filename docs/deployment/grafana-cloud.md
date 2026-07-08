# Grafana Cloud Deployment

Alloy exports all signals to Grafana Cloud (Tempo, Mimir, Loki) when credentials are configured.
When credentials are absent, cloud exporters are no-ops — local backends are unaffected.

---

## Credential architecture

Grafana Cloud uses **per-signal instance IDs** as Basic Auth usernames. A single shared API key is
the password for all three signals.

```
Grafana Cloud Stack
  ├── Tempo (traces)   → instance ID:  1541184   endpoint: tempo-prod-xx.grafana.net
  ├── Mimir (metrics)  → instance ID:  3102416   endpoint: prometheus-us-central2.grafana.net
  └── Loki  (logs)     → instance ID:  1546883   endpoint: logs-prod-037.grafana.net

Shared Access Policy token (glc_...): grafana-mccaindev-alloy-writer-mccaindev-token
  Scopes: metrics:write  logs:write  traces:write
```

> **Important**: The Grafana Cloud API key (`grafana-mccaindev-cloud-api-key`, prefix `glsa_`) is a
> Grafana **organisation service account token** — it authenticates with the Grafana frontend
> (`mccaindev.grafana.net`) but is **rejected** by Mimir/Loki/Tempo data ingestion endpoints (HTTP
> 401). Always use `grafana-mccaindev-alloy-writer-mccaindev-token` (prefix `glc_`) for data plane
> writes.

---

## Endpoint format requirements

The raw Grafana Cloud endpoint URLs require path/format adjustments before they work with Alloy:

| Signal                            | Raw Grafana Cloud URL                                 | Required format                                                             |
| --------------------------------- | ----------------------------------------------------- | --------------------------------------------------------------------------- |
| Traces (OTLP gRPC)                | `https://tempo-prod-29-....grafana.net`               | `tempo-prod-29-....grafana.net:443` — strip `https://`, append `:443`       |
| Metrics (Prometheus remote_write) | `https://prometheus-us-central2.grafana.net/api/prom` | `https://prometheus-us-central2.grafana.net/api/prom/push` — append `/push` |
| Logs (Loki push)                  | `https://logs-prod-037.grafana.net`                   | `https://logs-prod-037.grafana.net/loki/api/v1/push`                        |

**Why Prometheus remote_write, not OTLP HTTP, for metrics?** The Helm chart's Alloy destinations use
`type: prometheus`, which speaks Prometheus remote_write. App OTLP metrics arriving at
`alloy-receiver` are converted OTLP → Prometheus inside Alloy before shipping. This gives a single
ingestion path for scraped infra metrics and converted app metrics, and matches Grafana Cloud's
dashboard/query UX.

**Why the difference for Tempo?** gRPC uses HTTP/2 transport. Alloy's `otelcol.exporter.otlp`
expects a host:port endpoint without a URL scheme.

`scripts/fetch-grafana-cloud-conf-from-akv.sh` applies these adjustments automatically — append
`/push` to the Mimir URL if missing, append `/loki/api/v1/push` to Loki, append `:443` to Tempo.

---

## Azure Key Vault integration

Credentials are stored in Azure Key Vault (`mf-cc-dt-azrsrp-prd-kv`) under the `grafana-mccaindev-*`
prefix. The AKV coordinates (tenant/subscription/RG/vault name) live in [conf.yml](../../conf.yml)
under `monitoring.grafana_cloud.akv.*` — these are IDs/names and are safe to track in git.

| AKV secret name                                  | conf.yml key     | Secret key (K8s)               | Notes                                                       |
| ------------------------------------------------ | ---------------- | ------------------------------ | ----------------------------------------------------------- |
| `grafana-mccaindev-alloy-writer-mccaindev-token` | `api_key`        | `GRAFANA_CLOUD_API_KEY`        | `glc_` access-policy token — required for data-plane writes |
| `grafana-mccaindev-cloud-tempo-endpoint`         | `tempo.endpoint` | `GRAFANA_CLOUD_TEMPO_ENDPOINT` | fetch script appends `:443`                                 |
| `grafana-mccaindev-cloud-tempo-username`         | `tempo.user`     | `GRAFANA_CLOUD_TEMPO_USER`     |                                                             |
| `grafana-mccaindev-cloud-mimir-endpoint`         | `mimir.endpoint` | `GRAFANA_CLOUD_MIMIR_ENDPOINT` | fetch script appends `/push` if missing                     |
| `grafana-mccaindev-cloud-mimir-username`         | `mimir.user`     | `GRAFANA_CLOUD_MIMIR_USER`     |                                                             |
| `grafana-mccaindev-cloud-loki-endpoint`          | `loki.endpoint`  | `GRAFANA_CLOUD_LOKI_ENDPOINT`  | fetch script appends `/loki/api/v1/push` if missing         |
| `grafana-mccaindev-cloud-loki-username`          | `loki.user`      | `GRAFANA_CLOUD_LOKI_USER`      |                                                             |
| `grafana-mccaindev-faro-api-endpoint`            | `faro.endpoint`  | `FARO_COLLECTOR_URL`           | frontend runtime env                                        |
| `grafana-mccaindev-faro-sourcemap-token`         | `faro.api_key`   | `FARO_API_KEY`                 | webpack build arg                                           |

---

## Setup

### 1. Azure auth

The fetch script uses whatever `az` session is active. Either:

```bash
# Interactive login (your user credentials):
az login
```

Or export a service-principal before running the script (no .env file loading — export them in the
shell):

```bash
export ARM_CLIENT_ID=<sp-app-id>
export ARM_CLIENT_SECRET=<sp-password>
# TENANT_ID is read from conf.yml monitoring.grafana_cloud.akv.tenant_id
```

### 2. Fetch into conf.yml (in place)

```bash
# Preview the changes:
./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run

# Apply in place (creates conf.yml.bak):
./scripts/fetch-grafana-cloud-conf-from-akv.sh
```

The script updates **only** the nine leaf fields in
`monitoring.grafana_cloud.{api_key, tempo.*, mimir.*, loki.*, faro.*}`. Comments, ordering, and
every other field in conf.yml are preserved — see
[smoke-test-conf-updater.sh](../../scripts/smoke-test-conf-updater.sh) for the regression test that
enforces this.

### 3. Deploy (re-materialises the Secret)

```bash
./deploy-local.sh --skip-cluster --skip-build
```

`deploy-local.sh` writes the `grafana-cloud-secrets` K8s Secret into both `otel-lab` (apps/FARO
consumers) and `monitoring` (Helm chart's Alloy). Before `helm upgrade`, a contract validator
asserts every key referenced by the rendered values file is present in the Secret — rename on either
side fails fast.

Where the nine leaf values come from is gated by `monitoring.grafana_cloud.use_env`:

- `false` (default) — read straight from
  `monitoring.grafana_cloud.{api_key, tempo.*, mimir.*, loki.*, faro.*}` in conf.yml, i.e. whatever
  step 2 last wrote.
- `true` — `deploy-local.sh` sources `.env` (repo root) instead and ignores the conf.yml fields
  above, reading `GRAFANA_CLOUD_API_KEY` / `GRAFANA_CLOUD_TEMPO_ENDPOINT` /
  `GRAFANA_CLOUD_TEMPO_USER` / `GRAFANA_CLOUD_MIMIR_ENDPOINT` / `GRAFANA_CLOUD_MIMIR_USER` /
  `GRAFANA_CLOUD_LOKI_ENDPOINT` / `GRAFANA_CLOUD_LOKI_USER` / `FARO_COLLECTOR_URL` / `FARO_API_KEY`
  — the same keys the legacy Makefile flow uses. Useful if you already keep `.env` current and want
  to skip step 2 entirely.

---

## Verifying cloud export is working

```bash
# Check Alloy receiver logs for successful exports
kubectl -n monitoring logs daemonset/grafana-k8s-alloy-receiver --tail=100 \
  | grep -E "grafana_cloud|export|error" | head -30

# Generate a trace
curl -s http://localhost:8080/api/projects

# Then check Grafana Cloud:
# → Explore → Tempo → search by service.name=gateway-api
# → Explore → Mimir → query: traces_spanmetrics_calls_total
# → Explore → Loki → query: {namespace="otel-lab"}
```

Expected log output when working:

```
level=debug component=otelcol.exporter.otlp.grafana_cloud_traces msg="successfully exported"
level=debug component=otelcol.exporter.otlphttp.grafana_cloud_metrics msg="successfully exported"
```

---

## Troubleshooting

### "endpoint is empty" in Alloy logs

The Secret was not applied or Alloy was not restarted after applying it.

```bash
# Verify the secret exists
kubectl -n otel-lab get secret grafana-cloud-secrets -o json | jq '.data | keys'

# Verify Alloy reads the env var
kubectl -n monitoring exec daemonset/grafana-k8s-alloy-receiver -- env | grep GRAFANA

# If env var is missing, Alloy needs a restart to pick up the new secret
kubectl -n monitoring rollout restart daemonset/grafana-k8s-alloy-receiver
```

### "401 Unauthorized"

Wrong API key or wrong instance ID for that signal type.

```bash
make secrets-show   # verify all 7 values are non-empty and correct
```

Each signal type has its own instance ID. Using the Tempo ID for Mimir (or vice versa) causes 401
errors on that signal only.

### "connection refused" for Tempo

Tempo endpoint must be `host:443` without `https://`. If it includes `https://` the gRPC transport
fails.

```bash
make secrets-show
# GRAFANA_CLOUD_TEMPO_ENDPOINT should be:  tempo-prod-xx....grafana.net:443
# NOT: https://tempo-prod-xx....grafana.net
```

Re-run `make secrets-fetch-akv` to re-apply the adjusted format.

### AKV authentication failing

```bash
source .env
az login --service-principal \
  --username  "$ARM_CLIENT_ID" \
  --password  "$ARM_CLIENT_SECRET" \
  --tenant    "$ARM_TENANT_ID"

# Verify SP has Key Vault Secrets User role
az keyvault show --name mf-cc-dt-azrsrp-prd-kv \
  --query "properties.accessPolicies[?objectId=='<SP_OBJECT_ID>']"

# List available secrets
az keyvault secret list --vault-name mf-cc-dt-azrsrp-prd-kv \
  --query "[?starts_with(name,'grafana-mccaindev')].name" -o tsv
```

---

## Graceful degradation

When cloud credentials are absent:

- `optional: true` on every `secretKeyRef` means Alloy pods start normally
- Cloud exporters log: `level=error msg="failed to export" err="endpoint is empty"`
- Local backends (Jaeger, Prometheus, Loki if deployed) receive all signals normally
- No reconfiguration needed to switch modes — just apply or remove the Secret

---

## Credential rotation

When the Grafana Cloud API key is rotated:

1. Update the secret in AKV:

   ```bash
   az keyvault secret set --vault-name mf-cc-dt-azrsrp-prd-kv \
     --name grafana-mccaindev-cloud-api-key --value "glsa_newtoken..."
   ```

2. Re-fetch and apply:

   ```bash
   make secrets-fetch-akv
   ```

3. Alloy picks up the new env vars automatically on the next pod restart (or force it):

   ```bash
   kubectl -n monitoring rollout restart daemonset/grafana-k8s-alloy-receiver
   ```

The old key remains valid until explicitly revoked in the Grafana Cloud Access Policies UI.
