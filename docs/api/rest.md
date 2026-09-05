---
title: "REST API Reference"
description: "Reference for gateway-api's REST endpoints covering projects, orders, notifications, and OTel trace context propagation."
tags: ["ShipSolid", "Signal Forge", "API"]
updated: 2026-07-10
zettelId: "202607091847-2"
relations:
  - slug: projects/app-signal-forge/api/grpc
    kind: related
  - slug: networks/05-http-ecosystem/05-grpc/05-grpc
    kind: related
---

## REST API Reference

Base URL: `http://localhost:8080/api` (via Traefik ingress) Internal cluster URL:
`http://gateway-api.otel-lab.svc.cluster.local:5000`

All endpoints accept and return `application/json`. Error responses follow the RFC 7807 Problem
Details format.

---

## Projects

### GET /api/projects

List all projects.

**Response 200**:

```json
[
  {
    "id": 1,
    "name": "Infrastructure Migration",
    "owner": "alice",
    "createdAt": "2026-04-14T10:00:00Z"
  }
]
```

---

### GET /api/projects/{id}

Get a single project.

**Response 200**:

```json
{
  "id": 1,
  "name": "Infrastructure Migration",
  "owner": "alice",
  "createdAt": "2026-04-14T10:00:00Z"
}
```

**Response 404**: Project not found.

---

### POST /api/projects

Create a project.

**Request body**:

```json
{
  "name": "Infrastructure Migration",
  "owner": "alice"
}
```

**Response 201**:

```json
{
  "id": 1,
  "name": "Infrastructure Migration",
  "owner": "alice",
  "createdAt": "2026-04-14T10:00:00Z"
}
```

---

### DELETE /api/projects/{id}

Delete a project.

**Response 204**: Deleted. **Response 404**: Not found.

---

## Orders

### POST /api/orders

Create an order. Triggers a [[projects/app-signal-forge/api/grpc|gRPC]] call to order-api, which
persists the order and publishes an `order.created` event to RabbitMQ.

**Request body**:

```json
{
  "projectId": 1,
  "description": "Server rack provisioning",
  "amount": 4500.00
}
```

**Validation rules**:

- `projectId` must be > 0
- `amount` must be > 0 and ≤ 999,999.99
- `description` must be non-empty and ≤ 500 characters

**Response 201**:

```json
{
  "id": 42,
  "status": "Created"
}
```

**Response 422 (validation failure)**:

```json
{
  "type": "https://tools.ietf.org/html/rfc7807",
  "title": "One or more validation errors occurred.",
  "status": 422,
  "errors": {
    "amount": ["Amount must be between 0 and 999999.99."]
  }
}
```

---

### GET /api/projects/{id}/orders

List all orders for a project. Uses gRPC server-streaming from order-api — rows are streamed from
PostgreSQL cursor.

**Response 200**:

```json
[
  {
    "id": 42,
    "projectId": 1,
    "description": "Server rack provisioning",
    "amount": 4500.00,
    "status": "Created",
    "createdAt": "2026-04-14T10:30:00Z"
  }
]
```

---

## Notifications

### GET /api/notifications

List recent notifications (proxied from notification-svc via HTTP).

**Response 200**:

```json
[
  {
    "orderId": "42",
    "projectId": "1",
    "description": "Server rack provisioning",
    "amount": "4500.0",
    "processedAt": "2026-04-14T10:30:01.234Z",
    "status": "processed"
  }
]
```

---

## Observability test endpoints

### GET /api/slow

Artificial delay of 2–5 seconds. Always retained by tail sampling (latency > 2s policy). Useful for
validating exemplars (the span is always sampled).

**Response 200**:

```json
{ "message": "slow response", "delayMs": 3247 }
```

---

### GET /api/error

Always returns HTTP 500 with an unhandled exception. Always retained by tail sampling (error
policy).

**Response 500**:

```json
{
  "type": "https://tools.ietf.org/html/rfc7231#section-6.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500
}
```

The trace for this request will have `otel.status_code=ERROR` and an `exception.stacktrace` span
event.

---

### GET /healthz

Liveness and readiness probe endpoint. Returns immediately with no downstream calls.

**Response 200**: `"Healthy"`

---

## OTel context propagation

All API endpoints accept and propagate the W3C `traceparent` header. When the Angular SPA sends a
request with this header, the gateway-api HTTP server span becomes a child of the browser span.

```
Request:
traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01

Response headers (propagated through nginx proxy):
traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-<new-span-id>-01
```

The trace ID (`4bf92f...`) is preserved end-to-end from browser to database.
