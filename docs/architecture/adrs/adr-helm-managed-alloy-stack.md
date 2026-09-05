---
title: "Signal Forge ADR-004: Helm-managed Alloy stack (grafana/k8s-monitoring)"
description: "Standardizes on the grafana/k8s-monitoring Helm chart's five-role Alloy topology, keeping the hand-rolled DaemonSet only as a non-deployed reference."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-6"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-separate-collector-configmaps-per-mode
    kind: related
  - slug: projects/app-signal-forge/architecture/adrs/adr-k8s-attribute-enrichment-at-collector
    kind: related
  - slug: projects/app-signal-forge/architecture/overview
    kind: related
---

## Signal Forge ADR-004: Helm-managed Alloy stack (grafana/k8s-monitoring)

**Status**: Accepted

**Decision**: The production collector stack uses the `grafana/k8s-monitoring` v3.8.4 Helm chart
(five specialised Alloy roles). The hand-rolled DaemonSet in `k8s/monitoring/grafana/` is kept as a
reference artifact but is not deployed. The split between cloud and local collector configs within
this stack is covered separately in
[[projects/app-signal-forge/architecture/adrs/adr-separate-collector-configmaps-per-mode|ADR-005]].

**Rationale**:

- Running two Alloy instances receiving the same OTLP traffic caused duplicate spans, duplicate
  metric samples, version mismatches, and CrashLoopBackOff.
- The Helm chart manages RBAC, ServiceAccounts, and River configs with versioned upgrades. The
  hand-rolled version required manual maintenance of all these.
- The five-role split (metrics, logs, singleton, receiver, profiles) mirrors production AKS
  configuration, providing parity for validation.

**The five roles**:

| Role              | Kind        | Purpose                                   |
| ----------------- | ----------- | ----------------------------------------- |
| `alloy-receiver`  | DaemonSet   | OTLP push receiver — app telemetry        |
| `alloy-logs`      | DaemonSet   | Pod + node log tailing → Loki             |
| `alloy-metrics`   | StatefulSet | kubelet, cAdvisor, KSM → Prometheus       |
| `alloy-singleton` | Deployment  | Cluster events, KSM API → Loki/Prometheus |
| `alloy-profiles`  | DaemonSet   | Continuous profiling (disabled locally)   |

**Alternative considered**: Single hand-rolled DaemonSet — rejected due to operational complexity
and the duplicate-collector problem.
