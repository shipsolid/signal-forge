# Resilience patterns

Application-level failure handling: retries, circuit breakers, backoff, and delivery-safety
patterns. This is distinct from [Reliability](reliability.md), which covers **workload-level**
Kubernetes controls (PodDisruptionBudgets, anti-affinity, graceful shutdown) — this page covers
what happens **inside** a service call when a downstream dependency is slow or unavailable.

Retries amplify load on an already-struggling downstream. Every retry policy below is paired with
either a timeout, a circuit breaker, or a bounded attempt count — never an unbounded retry loop
against a live dependency, with one caveat called out explicitly below.

## Pattern inventory

| Pattern | Where applied | Config | Failure it guards | Verified by |
| --- | --- | --- | --- | --- |
| Retry + circuit breaker (`AddStandardResilienceHandler`) | gateway-api → order-api gRPC client ([Program.cs:78-84](../../src/gateway-api/Program.cs#L78-L84)) | Microsoft.Extensions.Http.Resilience standard pipeline (retry with exponential backoff, then circuit-breaks on sustained failure) — library defaults, not tuned in this repo | order-api transient unavailability / restart | No automated fault-injection test exercises this path — see [Known Issues](known-issues.md) |
| Retry + circuit breaker (`AddStandardResilienceHandler`) | gateway-api → notification-svc HTTP client ([Program.cs:94-101](../../src/gateway-api/Program.cs#L94-L101)) | Same standard pipeline, plus an explicit `client.Timeout = 10s` on the `HttpClient` itself | notification-svc transient unavailability; prevents a dead notification-svc from saturating gateway-api's thread pool | Not covered by an automated test |
| DB connection retry (`EnableRetryOnFailure`) | gateway-api → MySQL ([Program.cs:67](../../src/gateway-api/Program.cs#L67)); order-api → PostgreSQL ([Program.cs:67](../../src/order-api/Program.cs#L67)) | EF Core's built-in retrying execution strategy | Transient connection blips mid-operation (does **not** cover the initial `ServerVersion.AutoDetect` connection at boot — that still throws immediately) | Not covered by an automated test |
| Fail-fast on missing config | Both .NET services, at startup | Throws `InvalidOperationException` if the DB connection string is empty — see [ADR-006](decisions.md#adr-006-fail-fast-on-missing-secrets) | Silent misconfiguration masquerading as a healthy pod | Manual — `kubectl describe pod` shows the error |
| Outbox poll-retry (no backoff) | order-api `OutboxRelayWorker` ([OutboxRelayWorker.cs:63-79](../../src/order-api/Messaging/OutboxRelayWorker.cs#L63-L79)) | Fixed 5s poll interval; on any exception, log and retry on the *next* scheduled poll (not an immediate retry loop, but also no exponential backoff — a sustained RabbitMQ outage retries every 5s indefinitely) | RabbitMQ unavailable at publish time, pod crash between DB write and publish | `OutboxRelayWorkerTests`: "publisher failure leaves message unprocessed", "two concurrent replicas only publish each message once" (docs/testing.md) |
| Multi-replica-safe claiming (`FOR UPDATE SKIP LOCKED`) | order-api `OutboxRelayWorker.PublishAndMarkAsync` | Postgres row lock per message, one transaction per message | Two replicas double-publishing the same outbox row | `OutboxRelayWorkerTests` (docs/testing.md) |
| Exponential backoff on consumer crash | notification-svc consumer loop ([main.py](../../src/notification-svc/app/main.py)) | 5s → 10s → 20s → ... → 300s cap, doubling each attempt, reset on clean return | RabbitMQ connection drop — prevents thundering-herd reconnect storms | Not covered by an automated test |
| Per-error-class NACK routing | notification-svc `handle_order_created` ([consumer.py:271-294](../../src/notification-svc/app/consumer.py#L271-L294)) | `redis.RedisError` → `basic_nack(requeue=True)` (immediate redelivery, **no backoff** — see [Known Issues](known-issues.md)); `ValueError`/`KeyError`/generic `Exception` → `basic_nack(requeue=False)` → DLQ, no retry | Distinguishes transient infra failure (worth retrying) from a genuinely poison message (retrying won't help) | Not covered by an automated test |
| Dead Letter Queue | RabbitMQ `notifications` queue → `orders.dlq` → `notifications.dlq` — [ADR-008](decisions.md#adr-008-dead-letter-queue-for-poison-message-handling) | `x-dead-letter-exchange` queue argument | Poison messages starving the queue with infinite redelivery | Manual — inspect via RabbitMQ Management UI |
| Idempotency (delivery-safety net for retries) | order-api `CreateOrder` idempotency key; notification-svc Redis `SET NX` dedup (24h TTL, matched to the notification record's own TTL) | DB-level idempotency key lookup; atomic `SET dedup:{order_id} NX EX 86400`, plus `LREM` before `LPUSH` on the ID list as defense-in-depth | Retries / at-least-once redelivery producing duplicate side effects | `OrderGrpcServiceTests` idempotency-key test group (docs/testing.md) |
| Readiness-gated degradation | notification-svc `/readyz` | Pulls the pod from Service rotation when Redis is unreachable (stops new HTTP traffic; does not stop the consumer thread) | Redis outage — fails closed on the read path while the consumer keeps draining RabbitMQ | Not covered by an automated test |

## What is not implemented

- **Bulkhead / connection-pool isolation.** No per-downstream connection or thread-pool partitioning — a slow order-api and a slow notification-svc share gateway-api's default `HttpClient`/gRPC channel pools rather than isolated pools per dependency.
- **Application-level rate limiting / load shedding.** gRPC's `ResourceExhausted` status maps through to `HTTP 429` in `GrpcErrorMapping.ToProblem()` (see [docs/services/gateway-api.md](../services/gateway-api.md#failure-modes)), but nothing in this repo actually *produces* `ResourceExhausted` — there is no rate limiter installed on either service that would trigger it.
- **Chaos / fault-injection testing.** None of the retry, circuit-breaker, or backoff behavior above is exercised by an automated fault-injection test (e.g. killing RabbitMQ mid-load-test, injecting latency into MySQL). The k6 load test in [k8s/loadtest/](../../k8s/loadtest) generates volume, not faults. Validation today is manual (kill a pod, watch Jaeger/logs) or inferred from the mocked unit tests noted in the table above.
