---
title: "Signal Forge ADR-002: SpanLink for async RabbitMQ propagation (not parent-child)"
description: "Uses a SpanLink, not a parent-child span relationship, to connect RabbitMQ consumer spans back to the producer span across async, retry-prone delivery."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-12"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-dead-letter-queue-for-poison-messages
    kind: related
  - slug: projects/app-signal-forge/architecture/overview
    kind: related
  - slug: projects/app-signal-forge/otel-patterns
    kind: related
---

## Signal Forge ADR-002: SpanLink for async RabbitMQ propagation (not parent-child)

**Status**: Accepted

**Decision**: The notification-svc consumer span uses a `Link` to the order-api producer span
context, not a parent-child relationship. The same consumer also NACKs poison messages to a
[[projects/app-signal-forge/architecture/adrs/adr-dead-letter-queue-for-poison-messages|dead-letter queue]]
— see [[projects/app-signal-forge/otel-patterns|the instrumentation reference]] for the full
producer/consumer code walkthrough.

**Rationale**:

- OTel semantic conventions for messaging specify parent-child for synchronous in-process
  consumption, SpanLink for asynchronous cross-process consumption.
- Messages may be redelivered after NACK; each redelivery produces a separate consumer span. With
  parent-child, multiple consumer spans would all claim the same producer span as parent, creating
  an invalid trace tree. With SpanLink, each consumer span links to the producer span independently.
- In Jaeger, SpanLinks render as dashed arrows — visually distinct from synchronous parent-child
  chains — making the async boundary immediately visible.

**Alternative considered**: Parent-child — rejected because it misrepresents the async relationship
and breaks under retry scenarios.
