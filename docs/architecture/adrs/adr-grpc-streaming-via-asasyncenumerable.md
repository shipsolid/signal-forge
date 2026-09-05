---
title: "Signal Forge ADR-010: gRPC server-streaming via AsAsyncEnumerable (not ToListAsync)"
description: "Streams order rows to the gRPC client directly from the PostgreSQL cursor via AsAsyncEnumerable, keeping memory usage O(1) regardless of result set size."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-5"
relations:
  - slug: projects/app-signal-forge/api/grpc
    kind: related
  - slug: projects/app-signal-forge/architecture/overview
    kind: related
---

## Signal Forge ADR-010: gRPC server-streaming via AsAsyncEnumerable (not ToListAsync)

**Status**: Accepted

**Decision**: [[projects/app-signal-forge/api/grpc|`GetOrdersByProject`]] streams rows directly from
the PostgreSQL cursor using `AsAsyncEnumerable()`, writing each row to the gRPC stream as it is
fetched.

**Code pattern**:

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

**Rationale**:

- `ToListAsync()` loads all matching rows into a `List<Order>` in application memory before
  streaming begins. For large result sets this causes OOM.
- `AsAsyncEnumerable()` uses the database cursor: one row is fetched, sent over gRPC, then the next
  is fetched. Memory usage is O(1) regardless of result set size.
- CancellationToken is threaded through so the DB query is cancelled if the gRPC client disconnects.

**Alternative considered**: `ToListAsync()` — rejected due to unbounded memory growth.
