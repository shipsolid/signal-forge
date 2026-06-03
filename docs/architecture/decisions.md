# Architecture Decision Records

Key design choices, their rationale, and the alternatives considered.

---

## ADR-001: Log tailing instead of OTLP log export

**Decision**: Set `OTEL_LOGS_EXPORTER=none` on all services. Ship logs via node-level Alloy tailing (`alloy-logs` DaemonSet), not via the OTLP SDK.

**Rationale**:

- At production scale, log volume spikes must not consume SDK/process memory or CPU. Node-level agents absorb backpressure independently.
- Applications write structured JSON to stdout — the simplest possible contract. No log SDK configuration in service code.
- The tailing pattern explicitly validates log-to-trace correlation via metadata extraction (`stage.json` → `stage.structured_metadata`), which is a distinct OTel pattern from OTLP log push.
- Log tailing survives SDK crashes and OOM kills; OTLP log export does not.

**Trade-off**: A small delay (seconds) between log emission and Loki ingestion. Acceptable for all known use cases.

**Alternative considered**: Direct OTLP log export — rejected because it couples log delivery reliability to application health and adds SDK complexity.

---

## ADR-002: SpanLink for async RabbitMQ propagation (not parent-child)

**Decision**: The notification-svc consumer span uses a `Link` to the order-api producer span context, not a parent-child relationship.

**Rationale**:

- OTel semantic conventions for messaging specify parent-child for synchronous in-process consumption, SpanLink for asynchronous cross-process consumption.
- Messages may be redelivered after NACK; each redelivery produces a separate consumer span. With parent-child, multiple consumer spans would all claim the same producer span as parent, creating an invalid trace tree. With SpanLink, each consumer span links to the producer span independently.
- In Jaeger, SpanLinks render as dashed arrows — visually distinct from synchronous parent-child chains — making the async boundary immediately visible.

**Alternative considered**: Parent-child — rejected because it misrepresents the async relationship and breaks under retry scenarios.

---

## ADR-003: Span metrics generated before tail sampling

**Decision**: The `spanmetrics` connector is placed in the pipeline **before** `tail_sampling`.

**Rationale**:

- If span metrics were generated after sampling, only ~25% of traces would contribute to rate and error counters. A "request rate" metric reading 25% of actual traffic would be operationally useless.
- Placing `spanmetrics` before sampling means every span contributes to RED metrics, regardless of whether the trace is kept. The sampled traces are for debugging; the span metrics are for SLO dashboards.

**Pipeline order**:

```text
filter(healthz) → spanmetrics (ALL spans)
                ↘
                  tail_sampling (25% + errors + slow)
                              ↓
                            batch
```

**Alternative considered**: After sampling — rejected because it produces misleading metrics.

---

## ADR-004: Helm-managed Alloy stack (grafana/k8s-monitoring)

**Decision**: The production collector stack uses the `grafana/k8s-monitoring` v3.8.4 Helm chart (five specialised Alloy roles). The hand-rolled DaemonSet in `k8s/monitoring/grafana/` is kept as a reference artifact but is not deployed.

**Rationale**:

- Running two Alloy instances receiving the same OTLP traffic caused duplicate spans, duplicate metric samples, version mismatches, and CrashLoopBackOff.
- The Helm chart manages RBAC, ServiceAccounts, and River configs with versioned upgrades. The hand-rolled version required manual maintenance of all these.
- The five-role split (metrics, logs, singleton, receiver, profiles) mirrors production AKS configuration, providing parity for validation.

**The five roles**:

| Role              | Kind        | Purpose                                   |
| ----------------- | ----------- | ----------------------------------------- |
| `alloy-receiver`  | DaemonSet   | OTLP push receiver — app telemetry        |
| `alloy-logs`      | DaemonSet   | Pod + node log tailing → Loki             |
| `alloy-metrics`   | StatefulSet | kubelet, cAdvisor, KSM → Prometheus       |
| `alloy-singleton` | Deployment  | Cluster events, KSM API → Loki/Prometheus |
| `alloy-profiles`  | DaemonSet   | Continuous profiling (disabled locally)   |

**Alternative considered**: Single hand-rolled DaemonSet — rejected due to operational complexity and the duplicate-collector problem.

---

## ADR-005: Separate collector configmaps per deployment mode

**Decision**: The Alloy collector configuration is split by mode:

- Cloud: `k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl` — Helm values rendered by `deploy-local.sh` when `monitoring.mode: cloud`; destinations are Grafana Cloud Tempo/Mimir/Loki.
- Local: `k8s/monitoring/grafana/local/configmap.yaml` — hand-rolled Alloy configmap applied when `monitoring.mode: local`; destinations are in-cluster Jaeger, Prometheus, and Loki.

`./deploy-local.sh` selects the correct values file / configmap based on `monitoring.mode` in `conf.yml`.

**Rationale**:

- A single configmap with conditional blocks or "empty endpoint = no-op" logic obscures intent. Operators reading the deployed configmap should see exactly what is running.
- Cloud and local pipelines have structurally different exporters (`otelcol.exporter.otlp` + `otelcol.auth.basic` vs `otelcol.exporter.otlp` with `tls.insecure = true` + `prometheus.remote_write`). These are not cosmetic differences.
- The split prevents accidental cloud credential exposure in local-only deployments.

**Alternative considered**: Single configmap with empty-endpoint guards — rejected because it conflates two deployment modes and produces misleading no-op exporter logs.

---

## ADR-006: Fail-fast on missing secrets

**Decision**: Services throw at startup if required connection strings are absent or empty. No fallback to defaults.

**Code pattern (.NET)**:

```csharp
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is required. Set the environment variable.");
```

**Rationale**:

- A service that starts without a database connection appears healthy to liveness probes but fails all requests. This is worse than failing loudly at startup — it makes root cause harder to find.
- Fail-fast produces a clear error in pod logs immediately, the pod enters `CrashLoopBackOff`, and the operator can read the exact missing variable from `kubectl describe pod`.
- Silent defaults (e.g. connecting to `localhost:3306`) work in developer machines but break in Kubernetes where there is no local database — this class of environment-specific bugs is eliminated.

**Alternative considered**: Fallback defaults — rejected because they hide misconfiguration.

---

## ADR-007: secretKeyRef for all credentials (no plaintext env vars)

**Decision**: All database passwords, RabbitMQ credentials, and API keys are stored in Kubernetes Secrets and referenced via `secretKeyRef` in Deployment env vars. No plaintext credentials in manifests.

**Rationale**:

- Kubernetes manifests are typically committed to version control. Plaintext passwords in `deployment.yaml` would be exposed to anyone with repo read access and in all git history.
- `secretKeyRef` keeps credential values in the cluster only. Manifests are safe to commit.
- `optional: true` is used on Grafana Cloud secrets only (opt-in feature). All datastore secrets are required (no `optional`).

**Alternative considered**: ConfigMap with base64 values — rejected because ConfigMaps are not access-controlled by default and are not treated as sensitive by cluster operators.

---

## ADR-008: Dead Letter Queue for poison message handling

**Decision**: The RabbitMQ `notifications` queue is declared with `x-dead-letter-exchange` pointing to `orders.dlq` (fanout exchange). Messages that exceed `x-max-retries` or are explicitly NACKed without requeue are routed to a `notifications.dlq` queue.

**Rationale**:

- Without a DLQ, a consistently failing message causes an infinite retry loop that starves processing of other messages and spikes CPU.
- The dead-letter pattern is built into RabbitMQ — no additional application code is needed in the NACK path. The consumer NACKs with `requeue=False`; the broker handles routing.
- DLQ messages can be inspected via the RabbitMQ Management UI and reprocessed manually or via a separate consumer once the underlying bug is fixed.

**Alternative considered**: Manual retry counter in Redis with re-publish — rejected as unnecessary complexity when RabbitMQ provides this natively.

---

## ADR-009: K8s attribute enrichment at collector (not in SDK)

**Decision**: K8s pod/namespace/deployment attributes are added by `otelcol.processor.k8sattributes` in Alloy, not by the application SDK.

**Rationale**:

- Application code should not know about Kubernetes. K8s attributes are infrastructure metadata.
- Centralised enrichment means adding a new service to the cluster gets K8s attributes automatically — no SDK change required.
- The k8sattributes processor uses the OTLP connection source IP to look up the pod in the Kubernetes API, which is accurate and requires no application-side configuration.
- The processor requires a ClusterRole with `get/list/watch` on `pods` and `nodes`. This is one configuration point for the entire cluster, not per-service.

**Alternative considered**: `OTEL_RESOURCE_ATTRIBUTES` env var per Deployment — rejected because it requires manual maintenance and is inaccurate (pod name changes on each restart; it would always show the previous name unless the env var uses the Downward API).

---

## ADR-010: gRPC server-streaming via AsAsyncEnumerable (not ToListAsync)

**Decision**: `GetOrdersByProject` streams rows directly from the PostgreSQL cursor using `AsAsyncEnumerable()`, writing each row to the gRPC stream as it is fetched.

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

- `ToListAsync()` loads all matching rows into a `List<Order>` in application memory before streaming begins. For large result sets this causes OOM.
- `AsAsyncEnumerable()` uses the database cursor: one row is fetched, sent over gRPC, then the next is fetched. Memory usage is O(1) regardless of result set size.
- CancellationToken is threaded through so the DB query is cancelled if the gRPC client disconnects.

**Alternative considered**: `ToListAsync()` — rejected due to unbounded memory growth.
