# Planned: consistent `deployment_environment` label across metrics, logs, traces

**Status:** Not started — queued for next iteration **Scope:**
`k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl`,
`k8s/monitoring/grafana/local/configmap.yaml.tmpl`, `deploy-local.sh` **Delete this file once the
change ships** (working note, not permanent docs — see root `CLAUDE.md`'s docs map).

## Problem

Metrics and logs carry the label `deployment_environment` (underscore) in both `monitoring.mode`s.
Traces do not — they carry `deployment.environment` (dot, OTel semconv) instead, stamped only by the
app's `OTEL_RESOURCE_ATTRIBUTES`. The two are not the same label to any query/dashboard that filters
on `deployment_environment`.

### Root cause

Confirmed by pulling the actual vendored chart
(`helm pull grafana/k8s-monitoring --version 3.8.4 --untar`) and reading
`templates/destinations/_destination_otlp.tpl`:

- `extraLabels` is implemented only by the **prometheus**, **loki**, and **pyroscope** destination
  templates (`_destination_prometheus.tpl`, `_destination_loki.tpl`, `_destination_pyroscope.tpl`
  all range over `.extraLabels`).
- The **otlp** destination template (used for the traces→Tempo destination, `type: otlp`) has no
  `extraLabels` handling anywhere. The chart's `values.schema.json` has no
  `additionalProperties: false` on the destination definition, so `helm upgrade` doesn't reject the
  key — it just silently does nothing.
- Result: `values-cloud.yaml.tmpl`'s `grafana-cloud-traces` destination has an
  `extraLabels: { deployment_environment: ... }` block that has never taken effect.

Local mode has an independent version of the same gap: the bespoke Alloy DaemonSet's `env_label`
transform (`configmap.yaml.tmpl`) stamps `deployment.environment` (dot) on trace resources before
forwarding to Jaeger — Jaeger's OTLP receiver has no dot→underscore sanitization step (unlike the
Prometheus exporter, which does this automatically for metrics), so it never becomes
`deployment_environment`.

### Current state

| Signal  | Cloud mode                                                                        | Local mode                                                                             |
| ------- | --------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Metrics | `deployment_environment` via chart `extraLabels` — works                          | `deployment_environment` — Prometheus exporter auto-sanitizes dots→underscores — works |
| Logs    | `deployment_environment` via chart `extraLabels` — works                          | `deployment_environment` via `loki.write.external_labels` — works                      |
| Traces  | `deployment.environment` (dot) — `extraLabels` on the otlp destination is a no-op | `deployment.environment` (dot) — never renamed before the Jaeger exporter              |

## Changes

### 1. Cloud mode — `k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl`

Replace the no-op `extraLabels` block on the `grafana-cloud-traces` destination (currently lines
74-75) with the mechanism `_destination_otlp.tpl` actually reads:
`processors.transform.traces.resource`, an OTTL resource-context statement.

```yaml
  - name: grafana-cloud-traces
    type: otlp
    protocol: grpc
    url: ${TEMPO_ENDPOINT}
    processors:
      transform:
        traces:
          resource:
            - 'set(attributes["deployment_environment"], "${DEPLOYMENT_ENVIRONMENT}")'
    metrics:
      enabled: false
    logs:
      enabled: false
    traces:
      enabled: true
    auth:
      type: basic
      usernameKey: GRAFANA_CLOUD_TEMPO_USER
      passwordKey: GRAFANA_CLOUD_API_KEY
    secret:
      create: false
      name: ${SECRET_NAME}
      namespace: ${SECRET_NAMESPACE}
```

`${DEPLOYMENT_ENVIRONMENT}` is already substituted into this template by `render_helm_values()` in
`deploy-local.sh` — no new plumbing needed here.

### 2. Local mode — `k8s/monitoring/grafana/local/configmap.yaml.tmpl`

In the `env_label` OTel transform processor, the `trace_statements` block (currently lines 120-123)
only sets the dot key. Add the underscore key alongside it:

```
trace_statements {
  context    = "resource"
  statements = [
    "set(attributes[\"deployment.environment\"], \"${DEPLOYMENT_ENVIRONMENT}\") where attributes[\"deployment.environment\"] == nil",
    "set(attributes[\"deployment_environment\"], \"${DEPLOYMENT_ENVIRONMENT}\") where attributes[\"deployment_environment\"] == nil",
  ]
}
```

(Uses `${DEPLOYMENT_ENVIRONMENT}` rather than the literal `signal-forge-dev` — see item 3.)

### 3. Drift-risk fix, bundle into the same pass — hardcoded value in local mode

`configmap.yaml.tmpl` hardcodes `signal-forge-dev` literally in 4 places (`env_label`'s three
resource-context blocks + `loki.write "local"`'s `external_labels`).
`render_local_alloy_configmap()` in `deploy-local.sh` only substitutes `TRACE_CORRELATION_STAGES` —
`DEPLOYMENT_ENV` (already read at line 102 for other purposes) never reaches this template. So
changing `conf.yml`'s `monitoring.deployment_environment` today updates cloud mode automatically but
silently leaves local mode stamping the old default forever.

Fix:

- Template all 4 literals in `configmap.yaml.tmpl` as `${DEPLOYMENT_ENVIRONMENT}`.
- Update `render_local_alloy_configmap()` to pass `DEPLOYMENT_ENVIRONMENT=$DEPLOYMENT_ENV` into the
  `Template(...).substitute(...)` call alongside `TRACE_CORRELATION_STAGES`.

## Out of scope / optional follow-up

`k8s/monitoring/grafana-helm/values-local.yaml` (used only when `--with-helm` is passed in local
mode) has the identical no-op `extraLabels` issue on its tempo destination, plus the same
hardcoded-value pattern. Lower priority since local mode's primary path is the bespoke Alloy
DaemonSet, not the chart — fix in a later pass if `--with-helm` local usage becomes more common.

## Cardinality impact

None. `deployment_environment` has exactly one value per deployment (`signal-forge-dev` today) — not
a high-churn label, no new series growth on any of the three backends.

## Validation plan

- `helm template grafana/k8s-monitoring -f <rendered values-cloud.yaml> --version 3.8.4` — confirm
  the chart renders without schema errors after adding `processors.transform.traces.resource`.
- `./deploy-local.sh --skip-cluster --skip-build` (cloud mode) — redeploy with the updated template,
  generate a trace, confirm in Grafana Cloud Explore → Tempo that the span/resource attribute
  `deployment_environment` is present and searchable.
- `./deploy-local.sh --skip-cluster --skip-build` (local mode, `monitoring.mode: local`) — confirm
  in Jaeger UI that a trace's resource tags include `deployment_environment` (not just
  `deployment.environment`).
- Query Prometheus/Mimir and Loki for the same `deployment_environment` value across all three
  signal types for a single request to confirm the label now correlates identically end-to-end.
- Change `conf.yml`'s `monitoring.deployment_environment` to a throwaway value, redeploy local mode
  only, and confirm the new value appears in Jaeger/Loki/Prometheus (proves item 3 closed the drift
  gap).
