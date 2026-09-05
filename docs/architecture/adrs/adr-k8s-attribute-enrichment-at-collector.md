---
title: "Signal Forge ADR-009: K8s attribute enrichment at collector (not in SDK)"
description: "Adds Kubernetes pod/namespace/deployment attributes via Alloy's k8sattributes processor at the collector, keeping application SDKs Kubernetes-agnostic."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-7"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-helm-managed-alloy-stack
    kind: related
  - slug: projects/app-signal-forge/architecture/adrs/adr-separate-collector-configmaps-per-mode
    kind: related
  - slug: projects/app-signal-forge/architecture/overview
    kind: related
---

## Signal Forge ADR-009: K8s attribute enrichment at collector (not in SDK)

**Status**: Accepted

**Decision**: K8s pod/namespace/deployment attributes are added by `otelcol.processor.k8sattributes`
in the
[[projects/app-signal-forge/architecture/adrs/adr-helm-managed-alloy-stack|Helm-managed Alloy stack]],
not by the application SDK.

**Rationale**:

- Application code should not know about Kubernetes. K8s attributes are infrastructure metadata.
- Centralised enrichment means adding a new service to the cluster gets K8s attributes automatically
  — no SDK change required.
- The k8sattributes processor uses the OTLP connection source IP to look up the pod in the
  Kubernetes API, which is accurate and requires no application-side configuration.
- The processor requires a ClusterRole with `get/list/watch` on `pods` and `nodes`. This is one
  configuration point for the entire cluster, not per-service.

**Alternative considered**: `OTEL_RESOURCE_ATTRIBUTES` env var per Deployment — rejected because it
requires manual maintenance and is inaccurate (pod name changes on each restart; it would always
show the previous name unless the env var uses the Downward API).
