---
title: "Replication Guides: Instrumenting Your Own Project"
description: "Step-by-step, copy-paste guides for replicating SignalForge's OpenTelemetry instrumentation pattern in a new .NET/Python/Angular/RabbitMQ/K8s project."
tags: ["ShipSolid", "Signal Forge", "Observability", "Guides"]
updated: 2026-07-30
zettelId: "202607301400-01"
noteType: moc
relations:
  - slug: projects/app-signal-forge/otel-patterns
    kind: depends_on
  - slug: projects/app-signal-forge/observability/otel-contracts
    kind: depends_on
  - slug: projects/app-signal-forge/README
    kind: related
---

## Replication Guides: Instrumenting Your Own Project

These four guides are a hand-off package: they let another team stand up the same OpenTelemetry
instrumentation pattern used in SignalForge, in their own repository, without first learning this
lab's full architecture.

**They differ from the rest of this site's documentation on purpose.** Pages like
[[otel-patterns|otel-patterns.md]] and [[otel-contracts|observability/otel-contracts.md]] explain
_what SignalForge does and why_, for people already working in this codebase. These guides are
ordered, numbered, copy-paste instructions for _doing the same thing in a different repository_ —
**_install this, write this, set this env var, verify this_**. Where a step needs the "why," it
links out to the reference docs rather than re-explaining it.

### The one assumption that makes this concrete instead of generic

**Same stack.** .NET 8 (ASP.NET Core minimal APIs + gRPC), Python 3.12 (FastAPI), Angular, RabbitMQ,
Grafana Cloud (or a self-hosted Tempo/Mimir/Loki/Jaeger/Prometheus stack), Kubernetes + Helm. Every
package version, environment variable, and code snippet below is exactly what this project runs
today — verified against live source, not against older documentation, which in a few places had
drifted from the actual code (noted inline where it matters). If your target stack differs, use
these as a concrete worked example rather than a literal transcript.

### Guides

| Guide                            | Covers                                                                                                                     |
| -------------------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| [[collector-pipeline-setup]]     | Stand up the Grafana Alloy + `grafana/k8s-monitoring` Helm chart pipeline every app below sends signals to — do this first |
| [[dotnet-instrumentation]]       | ASP.NET Core / gRPC services: SDK wiring, custom spans/metrics, RabbitMQ producer-side async propagation (outbox pattern)  |
| [[python-instrumentation]]       | FastAPI service: SDK wiring, RabbitMQ consumer-side async propagation (SpanLink), log correlation                          |
| [[frontend-rum-instrumentation]] | Angular + Grafana Faro: browser telemetry, runtime config injection, browser-to-backend trace linkage                      |

### Recommended order

1. **[[collector-pipeline-setup|Collector & Pipeline Setup]] first.** The app-side guides all assume
   there's a live OTLP endpoint to send signals to and a Grafana instance to see them in. Standing
   this up first means every later step has something to verify against immediately.
2. **The three app-side guides, in any order** (or in parallel across teams) — .NET, Python,
   Frontend RUM don't depend on each other's completion, only on step 1.
3. **End-to-end verification** — each guide ends with its own verify step; once all four are done,
   confirm a single request's trace spans all the way from browser → backend → async consumer, per
   the [[otel-contracts#Cross-Service Trace Topology|Cross-Service Trace Topology]] pattern this
   project validates with a real integration test.

### For the "why" behind any step

- [[otel-contracts|OTel Signal Contracts]] — exact span/metric/log names this project emits, per
  service
- [[otel-patterns|OTel Patterns Reference]] — architecture, propagation, sampling, exemplars
- [[pipeline|Observability Pipeline]] — the Alloy collector's local vs. cloud mode pipelines, stage
  by stage
- [[grafana-cloud|Grafana Cloud Deployment]] — credential model, endpoint format requirements
- [[adr-spanlink-for-async-rabbitmq|ADR-002: SpanLink for async RabbitMQ]] — why the async hop uses
  a link, not a parent-child span
- [[adr-log-tailing-not-otlp-export|ADR-001: Log tailing, not OTLP export]] — why logs go via
  stdout + node-level tailing instead of an OTLP log exporter

See the [[projects/app-signal-forge/readme|documentation hub]] for the complete site map.
