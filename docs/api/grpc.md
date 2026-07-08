# gRPC API Reference

**Service**: `orders.OrderService` **Server**: order-api **Port**: 5001 (cluster-internal)
**Protocol**: HTTP/2 with protobuf encoding **Client**: gateway-api (uses `Grpc.Net.Client`)

Proto definition: `src/order-api/Protos/orders.proto` (also copied to `src/gateway-api/Protos/`)

---

## Service definition

```protobuf
syntax = "proto3";
package orders;

service OrderService {
  rpc CreateOrder (CreateOrderRequest) returns (CreateOrderResponse);
  rpc GetOrdersByProject (GetOrdersByProjectRequest) returns (stream OrderResponse);
  rpc GetOrder (GetOrderRequest) returns (OrderResponse);
}
```

---

## RPC: CreateOrder

Persists the order and an outbox row to PostgreSQL in one transaction, then returns — it does
**not** publish to RabbitMQ itself. `OutboxRelayWorker` picks up the outbox row on its next poll
(≤5s later) and publishes the `order.created` event from there, with W3C `traceparent` (captured at
write time) in the message headers. See
[order-api.md](../services/order-api.md#rabbitmq-publishing-outboxrelayworkercs-orderpublishercs)
for the full outbox flow and its trace-shape implications.

### Request

```protobuf
message CreateOrderRequest {
  int32  project_id      = 1;   // Must be > 0
  string description     = 2;   // Non-empty, ≤ 500 chars
  double amount          = 3;   // > 0 and ≤ 999999.99
  string idempotency_key = 4;   // Optional. Same key on a retried call replays the
                                 // original order instead of duplicating it.
}
```

### Response

```protobuf
message CreateOrderResponse {
  int32  order_id = 1;   // ID of the created order
  string status   = 2;   // "Created"
}
```

### Error codes

| gRPC status        | Condition                                                                |
| ------------------ | ------------------------------------------------------------------------ |
| `INVALID_ARGUMENT` | `project_id` ≤ 0, `amount` out of range, or `description` empty/too long |
| `INTERNAL`         | Database write (order + outbox row) failed                               |
| `UNAVAILABLE`      | PostgreSQL unreachable                                                   |

A RabbitMQ outage does **not** surface as an error here — publish happens later, out-of-band, in
`OutboxRelayWorker`. `CreateOrder` only touches PostgreSQL.

---

## RPC: GetOrdersByProject

Server-streaming RPC. Streams all orders for a given project, ordered by `created_at` descending.
Rows are streamed directly from the PostgreSQL cursor using `AsAsyncEnumerable()` — memory usage is
O(1) regardless of result set size.

### Request

```protobuf
message GetOrdersByProjectRequest {
  int32 project_id = 1;   // Must be > 0
}
```

### Response stream

```protobuf
message OrderResponse {
  int32  id          = 1;
  int32  project_id  = 2;
  string description = 3;
  double amount      = 4;
  string status      = 5;   // Created | Processing | Completed | Failed
  string created_at  = 6;   // ISO 8601 UTC
}
```

### Behaviour

- Returns zero messages if no orders exist for the project (not an error)
- Respects client cancellation: if the gRPC client disconnects, the DB cursor is cancelled via
  `CancellationToken`
- Error codes: `INVALID_ARGUMENT` (project_id ≤ 0), `INTERNAL` (DB error)

---

## RPC: GetOrder

Get a single order by ID.

### Request

```protobuf
message GetOrderRequest {
  int32 order_id = 1;
}
```

### Response

`OrderResponse` (same message as above).

### Error codes

| gRPC status        | Condition             |
| ------------------ | --------------------- |
| `NOT_FOUND`        | No order with that ID |
| `INVALID_ARGUMENT` | `order_id` ≤ 0        |

---

## Trace propagation

gRPC metadata is the carrier for W3C `traceparent`. gateway-api's `AddGrpcClientInstrumentation()`
injects this automatically in outbound calls.

```
gateway-api (gRPC client)
  → Outbound metadata: traceparent=00-4bf92f...-00f067...-01

order-api (gRPC server)
  ← AddAspNetCoreInstrumentation() reads metadata
  ← Creates HTTP server span as child of gateway-api's gRPC client span
```

Custom span attributes set by order-api on the `order.create` internal span:

- `order.id` — after DB insert
- `order.project_id` — from request
- `order.amount` — from request

These attributes appear in Jaeger and can be used as search filters.

---

## OTel span kinds

| RPC                  | Span created by gateway-api                 | Span created by order-api                       |
| -------------------- | ------------------------------------------- | ----------------------------------------------- |
| `CreateOrder`        | gRPC CLIENT span (`rpc.method=CreateOrder`) | gRPC SERVER span + `order.create` INTERNAL span |
| `GetOrdersByProject` | gRPC CLIENT span (streaming)                | gRPC SERVER span (streaming)                    |
| `GetOrder`           | gRPC CLIENT span                            | gRPC SERVER span                                |

The `order.publish` PRODUCER span is **not** a child of `order.create`. `CreateOrder` writes the
order and an outbox row in one transaction and returns immediately; the actual RabbitMQ publish
happens later, out-of-band, inside `OutboxRelayWorker`'s poll loop, as a child of its own
`outbox.relay` span — a separate, disconnected trace from the original request. See the trace-shape
comment at the top of `OrderGrpcService.cs` and `OutboxRelayWorker.cs`/`OrderPublisher.cs` for the
full picture, including why notification-svc's CONSUMER span still correctly links back to
`order.create` even though `order.publish` itself doesn't.

---

## Generating gRPC client code

The proto file is included in each .NET project's `.csproj`:

```xml
<ItemGroup>
  <Protobuf Include="Protos\orders.proto" GrpcServices="Server" />  <!-- order-api -->
  <Protobuf Include="Protos\orders.proto" GrpcServices="Client" />  <!-- gateway-api -->
</ItemGroup>
```

Code generation runs at build time. The generated client (`OrderService.OrderServiceClient`) and
server base class (`OrderService.OrderServiceBase`) are available in the `Orders` namespace.

Any schema change to `orders.proto` requires rebuilding both `order-api` and `gateway-api`,
re-importing images, and redeploying.
