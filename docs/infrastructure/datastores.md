# Datastores

## Overview

| Datastore     | Version                    | Owner                                               | K8s Kind            | Storage   | Port                            |
| ------------- | -------------------------- | --------------------------------------------------- | ------------------- | --------- | ------------------------------- |
| MySQL 8.0     | `mysql:8.0`                | gateway-api                                         | StatefulSet + PVC   | 1Gi       | 3306 (ClusterIP)                |
| PostgreSQL 16 | `postgres:16`              | order-api                                           | StatefulSet + PVC   | 1Gi       | 5432 (ClusterIP)                |
| Redis 7       | `redis:7-alpine`           | notification-svc                                    | Deployment (no PVC) | ephemeral | 6379 (ClusterIP)                |
| RabbitMQ 3.13 | `rabbitmq:3.13-management` | order-api (publisher) / notification-svc (consumer) | StatefulSet + PVC   | 1Gi       | 5672 (AMQP), 15672 (Management) |

---

## MySQL 8

**Owner**: gateway-api **Database**: `gatewaydb` **Tables**: `Projects`

### Kubernetes resources

- `k8s/datastores/mysql/statefulset.yaml` — single-replica StatefulSet, PVC 1Gi
- `k8s/datastores/mysql/service.yaml` — ClusterIP service `mysql.otel-lab:3306`
- `k8s/datastores/mysql/init-configmap.yaml` — init SQL run on first start:

```sql
CREATE DATABASE IF NOT EXISTS gatewaydb;
CREATE USER IF NOT EXISTS 'gateway'@'%' IDENTIFIED BY 'gateway_pw';
GRANT ALL PRIVILEGES ON gatewaydb.* TO 'gateway'@'%';
```

### Credentials

`MYSQL_ROOT_PASSWORD` is injected from `db-secrets` Secret via `secretKeyRef`. No plaintext
passwords in manifests.

The full connection string is stored as a base64-encoded value in `db-secrets`:

```
GATEWAY_DB_CONNECTION: Server=mysql;Database=gatewaydb;User=gateway;Password=gateway_pw;
```

### OTel instrumentation

`OpenTelemetry.Instrumentation.MySqlData` instruments all MySQL queries via
`AddMySqlDataInstrumentation()`. Spans include:

- `db.system = mysql`
- `db.name = gatewaydb`
- `db.statement` (the SQL query, sanitised)
- `net.peer.name = mysql`

---

## PostgreSQL 16

**Owner**: order-api **Database**: `orderdb` **Tables**: `Orders`

### Kubernetes resources

- `k8s/datastores/postgres/statefulset.yaml` — single-replica StatefulSet, PVC 1Gi
- `k8s/datastores/postgres/service.yaml` — ClusterIP `postgres.otel-lab:5432`
- `k8s/datastores/postgres/init-configmap.yaml`:

```sql
CREATE USER orderuser WITH PASSWORD 'order_pw';
CREATE DATABASE orderdb OWNER orderuser;
```

### Credentials

Connection string stored in `db-secrets` as `ORDER_DB_CONNECTION`:

```
Host=postgres;Database=orderdb;Username=orderuser;Password=order_pw;
```

### OTel instrumentation

`Npgsql.OpenTelemetry` instruments PostgreSQL queries via
`UseNpgsql(connStr, opts => opts.UseOpenTelemetry())`. Spans include:

- `db.system = postgresql`
- `db.name = orderdb`
- `db.statement`

---

## Redis 7

**Owner**: notification-svc **Use**: notification state (`notifications:{order_id}` hash),
idempotency dedup

Redis is deployed without a PVC because notification state is ephemeral for the lab — losing Redis
state means notifications are re-processed from RabbitMQ history, which is acceptable. In
production, use a Redis PVC or managed Redis with persistence.

### Kubernetes resources

- `k8s/datastores/redis/deployment.yaml` — single-replica Deployment
- `k8s/datastores/redis/service.yaml` — ClusterIP `redis.otel-lab:6379`

### Key structure

```
notifications:{order_id}  (Hash)
  ├── order_id:       "42"
  ├── project_id:     "7"
  ├── description:    "Server rack provisioning"
  ├── amount:         "4500.0"
  ├── processed_at:   "2026-04-14T10:30:01.234Z"
  └── status:         "processed"

TTL: 86400s (24 hours)
```

### OTel instrumentation

`opentelemetry-instrumentation-redis` instruments all Redis commands:

- `db.system = redis`
- `db.operation` (SET, HSET, HSETNX, etc.)
- `net.peer.name = redis`

### Connection resilience

The Redis client (`redis_client.py`) pings before use and reconnects automatically on
`ConnectionError`. Connection parameters include `socket_connect_timeout=5`,
`socket_keepalive=True`, `health_check_interval=30` for reliability in Kubernetes where pod IPs
change on restarts.

---

## RabbitMQ 3.13

**Publisher**: order-api **Consumer**: notification-svc

### Kubernetes resources

- `k8s/datastores/rabbitmq/statefulset.yaml` — single-replica StatefulSet, PVC 1Gi
- `k8s/datastores/rabbitmq/service.yaml` — two ports:
  - `5672` (AMQP, ClusterIP) — used by order-api and notification-svc
  - `15672` (Management UI, NodePort 30672) — exposed at `http://localhost:15672`

### Exchange and queue topology

```
Exchange: orders          (topic, durable)
  Binding: order.created → Queue: notifications (durable)
                                    │
                                    x-dead-letter-exchange: orders.dlq

Exchange: orders.dlq      (fanout, durable)
  → Queue: notifications.dlq (durable)
```

The DLQ is declared by notification-svc at consumer startup. Messages NACKed with `requeue=False`
are routed there automatically by RabbitMQ.

### Message format

Published by order-api on `orders` exchange, routing key `order.created`:

```json
{
  "order_id": 42,
  "project_id": 7,
  "description": "Server rack provisioning",
  "amount": 4500.00,
  "created_at": "2026-04-14T10:30:00Z"
}
```

W3C `traceparent` is injected into AMQP `BasicProperties.Headers` as bytes:

```
traceparent: 00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01
```

### Credentials

Default credentials: `guest/guest`. In production, change these and store in the `db-secrets`
Secret.

### Inspecting message flow

Management UI at `http://localhost:15672` (guest/guest):

- Queues tab → `notifications` → Get Message: inspect headers to verify `traceparent` is present
- Queues tab → `notifications.dlq`: inspect dead-lettered messages for debugging

---

## Deploy order

Datastores must be ready before application services are started:

```bash
kubectl apply -f k8s/datastores/mysql/
kubectl apply -f k8s/datastores/postgres/
kubectl apply -f k8s/datastores/redis/
kubectl apply -f k8s/datastores/rabbitmq/

# Wait for all datastore pods to be ready
kubectl -n otel-lab wait --for=condition=ready pod -l tier=datastore --timeout=180s

# Then deploy application services
kubectl apply -f k8s/app/gateway/
...
```

`./deploy-local.sh` enforces this order automatically (`apply_stage datastores` waits for readiness
before `apply_stage app` runs).
