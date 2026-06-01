# Service: order-api

**Role**: gRPC Order Service. Handles order CRUD, persists to PostgreSQL, publishes `order.created` events to RabbitMQ.

**Runtime**: .NET 8 gRPC server (+ minimal API for `/healthz`)
**Port**: 5001 (gRPC, cluster-internal)
**Replicas**: 2

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
  int32  project_id  = 1;
  string description = 2;
  double amount      = 3;
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

## RabbitMQ publishing (`OrderPublisher.cs`)

On successful `CreateOrder`:

1. Start a `PRODUCER` span: `order.publish` (`ActivityKind.Producer`)
2. Inject W3C `traceparent` into AMQP message headers (bytes-encoded, as pika delivers them):

```csharp
var propagator = Propagators.DefaultTextMapPropagator;
propagator.Inject(
    new PropagationContext(Activity.Current?.Context ?? default, Baggage.Current),
    props.Headers,
    (headers, key, value) => headers[key] = Encoding.UTF8.GetBytes(value));
```

3. Publish to exchange `orders`, routing key `order.created`:

```json
{
  "order_id": 42,
  "project_id": 7,
  "description": "Server rack provisioning",
  "amount": 4500.00,
  "created_at": "2026-04-14T10:30:00Z"
}
```

**Why bytes?** pika (Python AMQP client) delivers header values as bytes. The Python consumer's `HeadersGetter.get()` decodes them before extraction.

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

This uses the database cursor — one row is fetched, written to the gRPC stream, then the next is fetched. Memory usage is O(1) regardless of result size. `ToListAsync()` was intentionally avoided (see [ADR-010](../architecture/decisions.md#adr-010-grpc-server-streaming-via-asasyncenumerable-not-tolistasync)).

---

## OTel instrumentation

### Custom spans and metrics

| Instrument                   | Type             | Labels                                                                  | Description                                               |
| ---------------------------- | ---------------- | ----------------------------------------------------------------------- | --------------------------------------------------------- |
| `order.create` span          | `INTERNAL`       | `order.id`, `order.project_id`, `order.amount`                          | Wraps full create flow (validate → DB → publish)          |
| `order.publish` span         | `PRODUCER`       | `order.id`, `messaging.system=rabbitmq`, `messaging.destination=orders` | RabbitMQ publish with W3C header injection                |
| `orders.created.total`       | Counter          | `project_id`                                                            | Increments on each successful `CreateOrder`               |
| `orders.amount.total`        | Counter (double) | `project_id`                                                            | Running sum of order amounts (financial throughput gauge) |
| `orders.processing.duration` | Histogram        | `project_id`                                                            | Time from CreateOrder RPC received to publish complete    |

### Trace context in custom span attributes

Attributes are set **before** the DB call so they survive if the call throws:

```csharp
activity?.SetTag("order.project_id", request.ProjectId);
activity?.SetTag("order.amount", request.Amount);
// ... then attempt DB write
```

---

## Failure modes

| Scenario                      | Behaviour                                    | Evidence                                           |
| ----------------------------- | -------------------------------------------- | -------------------------------------------------- |
| PostgreSQL unavailable        | `CrashLoopBackOff`                           | Error in pod logs (fail-fast)                      |
| RabbitMQ publish fails        | `RpcException(StatusCode.Internal)`          | Error span, `exception.type=IOException`           |
| Duplicate order (idempotency) | No built-in dedup — order-api always inserts | See notification-svc for consumer-side dedup       |
| Invalid input                 | `RpcException(StatusCode.InvalidArgument)`   | No error span (client fault)                       |
| Client disconnects mid-stream | `CancellationToken` cancels DB cursor        | `OperationCanceledException` logged at debug level |

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
