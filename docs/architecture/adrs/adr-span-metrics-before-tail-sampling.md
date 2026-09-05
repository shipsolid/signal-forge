---
title: "Signal Forge ADR-003: Span metrics generated before tail sampling"
description: "Places the spanmetrics connector ahead of tail_sampling so RED metrics reflect all traffic instead of only the ~25% of traces that survive sampling."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-11"
relations:
  - slug: projects/app-signal-forge/architecture/overview
    kind: related
  - slug: projects/app-signal-forge/otel-patterns
    kind: related
---

## Signal Forge ADR-003: Span metrics generated before tail sampling

**Status**: Accepted

**Decision**: The `spanmetrics` connector is placed in the pipeline **before** `tail_sampling`. See
[[projects/app-signal-forge/otel-patterns|the instrumentation reference]] for the full pipeline
walkthrough and PromQL examples built on this ordering.

**Rationale**:

- If span metrics were generated after sampling, only ~25% of traces would contribute to rate and
  error counters. A "request rate" metric reading 25% of actual traffic would be operationally
  useless.
- Placing `spanmetrics` before sampling means every span contributes to RED metrics, regardless of
  whether the trace is kept. The sampled traces are for debugging; the span metrics are for SLO
  dashboards.

**Pipeline order**:

```text
filter(healthz) → spanmetrics (ALL spans)
                ↘
                  tail_sampling (25% + errors + slow)
                              ↓
                            batch
```

**Alternative considered**: After sampling — rejected because it produces misleading metrics.
