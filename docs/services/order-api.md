# Service: order-api

**Role**: gRPC Order Service. Handles order CRUD, persists to PostgreSQL, publishes `order.created`
events to RabbitMQ.

**Runtime**: .NET 8 gRPC server (+ minimal API for `/healthz`) **Ports**: 5001 (HTTP/1.1,
`/healthz` only — kubelet's probes can't negotiate HTTP/2), 5002 (gRPC, HTTP/2-only, cluster-internal)
**Replicas**: 2

Two separate Kestrel endpoints, not one shared port — a single cleartext port configured for mixed
HTTP/1.1+HTTP/2 silently downgrades every connection to HTTP/1.1 without TLS (confirmed empirically;
Kestrel logs "HTTP/2 requires TLS application protocol negotiation" and rejects gRPC's prior-knowledge
h2c preface with an HTTP_1_1_REQUIRED error). See `Program.cs`'s "gRPC server" comment for the detail.

---

## gRPC service definition

```protobuf
syntax = "proto3";
package orders;

service OrderService {
  rpc CreateOrder (CreateOrderRequest) returns (CreateOrderResponse);
  rpc GetOrdersByProject (GetOrdersByProjectRequest) returns (stream OrderResponse);
  rpc GetOrder (GetOrderRequest) returns (OrderResponse);
}

message CreateOrderRequest {
  int32  project_id      = 1;
  string description     = 2;
  double amount          = 3;
  string idempotency_key = 4;   // optional; replays the original order on retry
}

message CreateOrderResponse {
  int32  order_id = 1;
  string status   = 2;
}

message GetOrdersByProjectRequest {
  int32 project_id = 1;
}

message GetOrderRequest {
  int32 order_id = 1;
}

message OrderResponse {
  int32  id          = 1;
  int32  project_id  = 2;
  string description = 3;
  double amount      = 4;
  string status      = 5;
  string created_at  = 6;
}
```

### Input validation

Applied in `OrderGrpcService.cs` before any DB or publish operation:

| Field         | Rule                   | Error                                      |
| ------------- | ---------------------- | ------------------------------------------ |
| `project_id`  | > 0                    | `RpcException(StatusCode.InvalidArgument)` |
| `amount`      | > 0 and ≤ 999,999.99   | `RpcException(StatusCode.InvalidArgument)` |
| `description` | Non-empty, ≤ 500 chars | `RpcException(StatusCode.InvalidArgument)` |

---

## Domain model

```csharp
public class Order
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public string Description { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; }  // Created | Processing | Completed | Failed
    public DateTime CreatedAt { get; set; }
}
```

Database: PostgreSQL 16, EF Core 8 (Npgsql provider). Migrations via `dotnet ef database update`.

---

## Configuration

| Variable                               | Source                               | Required | Purpose                      |
| -------------------------------------- | ------------------------------------ | -------- | ---------------------------- |
| `ConnectionStrings__DefaultConnection` | `db-secrets` (`ORDER_DB_CONNECTION`) | **Yes**  | PostgreSQL connection string |
| `RabbitMQ__Host`                       | Deployment env                       | Yes      | RabbitMQ hostname            |
| `RabbitMQ__Username`                   | `db-secrets`                         | Yes      | RabbitMQ user                |
| `RabbitMQ__Password`                   | `db-secrets`                         | Yes      | RabbitMQ password            |
| `OTEL_SERVICE_NAME`                    | Deployment env                       | Yes      | `order-api`                  |
| `OTEL_EXPORTER_OTLP_ENDPOINT`          | Deployment env                       | Yes      | Alloy receiver endpoint      |

Fail-fast: empty `ConnectionStrings__DefaultConnection` throws at startup.

---

## RabbitMQ publishing (`OutboxRelayWorker.cs`, `OrderPublisher.cs`)

Publishing is **not** part of `CreateOrder`. It's the outbox pattern: `CreateOrder` writes the
`Order` and an `OutboxMessage` row in the same `SaveChanges` call, then returns — the caller sees
success as soon as PostgreSQL commits, regardless of RabbitMQ's state.

1. `OutboxRelayWorker` polls `WHERE ProcessedAt IS NULL` every 5s, oldest first, in batches of 100.
2. For each pending row it starts an `INTERNAL` span, `outbox.relay`, then calls
   `OrderPublisher.PublishAsync`.
3. `PublishAsync` starts a `PRODUCER` span, `order.publish` (`ActivityKind.Producer`), as a **child
   of `outbox.relay`** — not of the original request's `order.create`. `order.create` has already
   finished and returned by the time this runs.
4. It writes the `traceparent` **stored on the `OutboxMessage` row** (captured from
   `Activity.Current?.Id` back when `CreateOrder` wrote it) directly into the AMQP message headers,
   bytes-encoded as pika expects:

```csharp
if (!string.IsNullOrEmpty(traceParent))
    props.Headers["traceparent"] = Encoding.UTF8.GetBytes(traceParent);
```

This is deliberately **not** `Propagators.Inject(Activity.Current, ...)` — `Activity.Current` at
this point is `order.publish`/`outbox.relay`'s own (new, disconnected) trace, not the original
request's. Using the stored value is what lets notification-svc's CONSUMER span link back to
`order.create` correctly; see [grpc.md](../api/grpc.md#rpc-createorder) for what this means for
trace continuity on the order-api side.

5. Publish to exchange `orders`, routing key `order.created`:

```json
{
  "order_id": 42,
  "project_id": 7,
  "description": "Server rack provisioning",
  "amount": 4500.00,
  "created_at": "2026-04-14T10:30:00Z"
}
```

6. On success, `OutboxMessage.ProcessedAt` is set and saved. On failure, the exception is recorded
   on the `outbox.relay` span and `ProcessedAt` is left `null` — the row is retried on the next 5s
   poll, with no dedup counter or backoff (see the multi-replica race note below).

**Why bytes?** pika (Python AMQP client) delivers header values as bytes. The Python consumer's
`HeadersGetter.get()` decodes them before extraction.

---

## gRPC streaming: memory-safe cursor pattern

`GetOrdersByProject` streams rows directly from PostgreSQL using `AsAsyncEnumerable()`:

```csharp
await foreach (var order in _db.Orders
    .Where(o => o.ProjectId == request.ProjectId)
    .OrderByDescending(o => o.CreatedAt)
    .AsAsyncEnumerable()
    .WithCancellation(context.CancellationToken))
{
    await responseStream.WriteAsync(MapToResponse(order), context.CancellationToken);
}
```

This uses the database cursor — one row is fetched, written to the gRPC stream, then the next is
fetched. Memory usage is O(1) regardless of result size. `ToListAsync()` was intentionally avoided
(see
[ADR-010](../architecture/decisions.md#adr-010-grpc-server-streaming-via-asasyncenumerable-not-tolistasync)).

---

## OTel instrumentation

### Custom spans and metrics

| Instrument                   | Type             | Labels                                                                  | Description                                                                                     |
| ---------------------------- | ---------------- | ----------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------- |
| `order.create` span          | `INTERNAL`       | `order.id`, `order.project_id`, `order.amount`                          | Wraps validate + DB write (order + outbox row) only — no publish; see below                     |
| `outbox.relay` span          | `INTERNAL`       | `outbox.message_id`, `order.id`                                         | `OutboxRelayWorker`'s per-message span, its own disconnected trace root                         |
| `order.publish` span         | `PRODUCER`       | `order.id`, `messaging.system=rabbitmq`, `messaging.destination=orders` | RabbitMQ publish with W3C header injection; child of `outbox.relay`, not `order.create`         |
| `orders.created.total`       | Counter          | `project_id`                                                            | Increments on each successful `CreateOrder`                                                     |
| `orders.amount.total`        | Counter (double) | `project_id`                                                            | Running sum of order amounts (financial throughput gauge)                                       |
| `orders.processing.duration` | Histogram        | `project_id`                                                            | Time from CreateOrder RPC received to DB write commit (not publish — that's later, out of band) |

### Trace context in custom span attributes

Attributes are set **before** the DB call so they survive if the call throws:

```csharp
activity?.SetTag("order.project_id", request.ProjectId);
activity?.SetTag("order.amount", request.Amount);
// ... then attempt DB write
```

---

## Failure modes

| Scenario                            | Behaviour                                                                                                                                                                                                             | Evidence                                                        |
| ----------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------- |
| PostgreSQL unavailable              | `CrashLoopBackOff`                                                                                                                                                                                                    | Error in pod logs (fail-fast)                                   |
| RabbitMQ publish fails              | Does **not** affect `CreateOrder` — the RPC already returned. `OutboxRelayWorker` logs the error on the `outbox.relay` span and leaves `ProcessedAt` null; retried on the next 5s poll, indefinitely, with no backoff | Error span on `outbox.relay`, not on the original request trace |
| Retried `CreateOrder` (idempotency) | `idempotency_key` unique index + replay: a repeated key returns the original order instead of inserting a duplicate                                                                                                   | `logger.LogInformation("CreateOrder replay detected...")`       |
| Invalid input                       | `RpcException(StatusCode.InvalidArgument)`                                                                                                                                                                            | No error span (client fault)                                    |
| Client disconnects mid-stream       | `CancellationToken` cancels DB cursor                                                                                                                                                                                 | `OperationCanceledException` logged at debug level              |

---

## Health probes

```yaml
livenessProbe:
  httpGet:
    path: /healthz
    port: 5001
  initialDelaySeconds: 30
  periodSeconds: 15
  timeoutSeconds: 5
  failureThreshold: 3
```
