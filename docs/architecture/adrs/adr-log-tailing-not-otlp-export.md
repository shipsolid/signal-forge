---
title: "Signal Forge ADR-001: Log tailing instead of OTLP log export"
description: "Ships logs via node-level Alloy tailing of stdout instead of OTLP SDK log export, to keep log delivery decoupled from application health."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-8"
relations:
  - slug: patterns/04-microservice-patterns/05-backpressure/05-backpressure
    kind: related
  - slug: projects/app-signal-forge/architecture/adrs/adr-separate-collector-configmaps-per-mode
    kind: related
---

## Signal Forge ADR-001: Log tailing instead of OTLP log export

**Status**: Accepted

**Decision**: Set `OTEL_LOGS_EXPORTER=none` on all services. Ship logs via node-level Alloy tailing
(`alloy-logs` DaemonSet), not via the OTLP SDK.

**Rationale**:

- At production scale, log volume spikes must not consume SDK/process memory or CPU. Node-level
  agents absorb [[patterns/04-microservice-patterns/05-backpressure/05-backpressure|backpressure]]
  independently.
- Applications write structured JSON to stdout — the simplest possible contract. No log SDK
  configuration in service code.
- The tailing pattern explicitly validates log-to-trace correlation via metadata extraction
  (`stage.json` → `stage.structured_metadata`), which is a distinct OTel pattern from OTLP log push.
  This correlation stage is authored once and shared across both collector modes — see
  [[projects/app-signal-forge/architecture/adrs/adr-separate-collector-configmaps-per-mode|ADR-005]].
- Log tailing survives SDK crashes and OOM kills; OTLP log export does not.

**Trade-off**: A small delay (seconds) between log emission and Loki ingestion. Acceptable for all
known use cases.

**Alternative considered**: Direct OTLP log export — rejected because it couples log delivery
reliability to application health and adds SDK complexity.
