---
title: "Signal Forge ADR-005: Separate collector configmaps per deployment mode"
description: "Keeps cloud and local Alloy collector configs in separate files rather than one conditional configmap, so each mode's exporters stay explicit and uncoupled."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-10"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-helm-managed-alloy-stack
    kind: related
  - slug: projects/app-signal-forge/architecture/adrs/adr-k8s-attribute-enrichment-at-collector
    kind: related
  - slug: projects/app-signal-forge/architecture/adrs/adr-log-tailing-not-otlp-export
    kind: related
---

## Signal Forge ADR-005: Separate collector configmaps per deployment mode

**Status**: Accepted

**Decision**: The Alloy collector configuration is split by mode:

- Cloud: `k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl` — Helm values rendered by
  `deploy-local.sh` when `monitoring.mode: cloud`; destinations are Grafana Cloud Tempo/Mimir/Loki.
- Local: `k8s/monitoring/grafana/local/configmap.yaml` — hand-rolled Alloy configmap applied when
  `monitoring.mode: local`; destinations are in-cluster Jaeger, Prometheus, and Loki.

`./deploy-local.sh` selects the correct values file / configmap based on `monitoring.mode` in
`conf.yml`.

**Rationale**:

- A single configmap with conditional blocks or "empty endpoint = no-op" logic obscures intent.
  Operators reading the deployed configmap should see exactly what is running.
- Cloud and local pipelines have structurally different exporters (`otelcol.exporter.otlp` +
  `otelcol.auth.basic` vs `otelcol.exporter.otlp` with `tls.insecure = true` +
  `prometheus.remote_write`). These are not cosmetic differences.
- The split prevents accidental cloud credential exposure in local-only deployments.

**Alternative considered**: Single configmap with empty-endpoint guards — rejected because it
conflates two deployment modes and produces misleading no-op exporter logs.

**Addendum (single-sourced trace correlation)**: The one piece of logic that was genuinely identical
in both configs — the
[[projects/app-signal-forge/architecture/adrs/adr-log-tailing-not-otlp-export|trace-ID/span-ID → Loki structured-metadata correlation stages]]
— is now authored once at `k8s/monitoring/grafana/shared/trace-correlation-stages.alloy` and spliced
into both `configmap.yaml.tmpl` (raw) and `values-cloud.yaml.tmpl` (Helm-`tpl`-escaped) by
`deploy-local.sh` (`render_local_alloy_configmap()` / `render_helm_values()`). This doesn't change
the rationale above — the two pipelines still have separate, structurally different files; it just
stops one specific snippet from silently drifting when only one side gets edited.
