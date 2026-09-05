---
title: "Signal Forge ADR-008: Dead Letter Queue for poison message handling"
description: "Routes messages that exceed retry limits or are NACKed without requeue to a RabbitMQ DLQ, preventing infinite retry loops from starving the consumer."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-3"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-spanlink-for-async-rabbitmq
    kind: related
  - slug: projects/app-signal-forge/architecture/overview
    kind: related
---

## Signal Forge ADR-008: Dead Letter Queue for poison message handling

**Status**: Accepted

**Decision**: The RabbitMQ `notifications` queue is declared with `x-dead-letter-exchange` pointing
to `orders.dlq` (fanout exchange). Messages that exceed `x-max-retries` or are explicitly NACKed
without requeue are routed to a `notifications.dlq` queue.

**Rationale**:

- Without a DLQ, a consistently failing message causes an infinite retry loop that starves
  processing of other messages and spikes CPU.
- The dead-letter pattern is built into RabbitMQ — no additional application code is needed in the
  NACK path. The same notification-svc consumer that carries the
  [[projects/app-signal-forge/architecture/adrs/adr-spanlink-for-async-rabbitmq|SpanLink from the RabbitMQ producer]]
  NACKs with `requeue=False`; the broker handles routing.
- DLQ messages can be inspected via the RabbitMQ Management UI and reprocessed manually or via a
  separate consumer once the underlying bug is fixed.

**Alternative considered**: Manual retry counter in Redis with re-publish — rejected as unnecessary
complexity when RabbitMQ provides this natively.
