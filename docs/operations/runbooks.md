---
title: "Runbooks"
description: "Troubleshooting playbooks for every known Signal Forge failure mode, from missing traces to Grafana Cloud export errors."
tags: ["ShipSolid", "Signal Forge", "Operations", "Incident Response"]
updated: 2026-09-06
zettelId: "202607091847-32"
relations:
  - slug: projects/app-signal-forge/deployment/grafana-cloud
    kind: depends_on
  - slug: projects/app-signal-forge/operations/resilience-patterns
    kind: related
  - slug: projects/app-signal-forge/operations/known-issues
    kind: related
---

## Runbooks

Troubleshooting playbooks for every known failure mode.

---

## Immutable CD promotion blocked or rolled back

### Scope

This runbook applies only when a GitHub Environment has `DEPLOY_ENABLED=true`. Without that flag,
CD is intentionally render-only and no cluster rollback is expected. The promotion input is a
successful CI run ID and its immutable release manifest — never a tag, a source branch, or a locally
rebuilt image.

### First checks

1. Open the CD run summary and record the selected CI run ID, release commit, target environment,
   and the four `repository@sha256:...` references. Do not copy these from `latest`.
2. Determine the failed boundary: pre-deploy evidence verification, server-side apply/rollout,
   exact-digest health gate, smoke test, observability gate, or DEV-only ZAP baseline.
3. Download the `deployment-plan-<environment>-<commit>` artifact. It is intentionally secret-free
   and shows the rendered digest references, runtime ConfigMaps, ingress host, and labels used for
   the attempted deployment.
4. If the failure occurred before `Apply immutable release`, no cluster mutation occurred. Fix the
   environment configuration or the release, then start a new promotion from a successful CI run.

### Verify the currently running release

```bash
namespace=otel-lab  # replace only if the protected Environment uses another namespace
for service in otel-frontend gateway-api order-api notification-svc; do
  kubectl -n "$namespace" get deployment "$service" \
    -o jsonpath='{.spec.template.spec.containers[0].image}{"\n"}'
done
```

Every returned application image must be a `ghcr.io/...@sha256:...` reference. A mutable tag is not
a valid known-good rollback target. Confirm workload availability and restart counts before declaring
recovery:

```bash
kubectl -n "$namespace" get deployments
kubectl -n "$namespace" get pods -l tier=app
```

### Automatic rollback behavior

After an apply, any failed health, smoke, observability, or DEV DAST gate triggers CD to restore the
complete previous four-image immutable set and the previous `signal-forge-app-env` and
`frontend-env-js` ConfigMaps. It refuses partial rollback because mixing independent service
versions creates a release that was never tested together.

If the run reports **"No complete previous immutable release is available for rollback"**, stop.
Do not run `kubectl apply -k k8s/overlays/prod`, use `latest`, or rebuild a prior commit. Recover
only with an operator-approved complete digest manifest after investigating why the target had no
captured known-good state.

### Observability-gate failure

The external gate receives the environment, release commit, CI run, required backend services,
metrics/logs/traces, and `service.name`/`service.version`/`deployment.environment` requirements.
It fails closed for an unknown response or `block`; a `warn` is visible in DEV/QA but blocks PROD.

Use the gate response summary and the configured observability platform to determine whether the
candidate has missing telemetry, a deployment identity mismatch, an error/latency regression, or an
approved-policy breach. This repository does not contain the external query endpoint or credentials,
so do not substitute a green `/healthz` result for telemetry evidence.

See [Immutable CI/CD Promotion](../deployment/ci-cd.md) for the complete gate order and environment
contract.

---

## No traces in Jaeger

### Symptoms

- Jaeger UI shows no services
- `make validate` passes but traces don't appear

### Diagnosis

**Step 1: Is alloy-receiver running?**

```bash
kubectl -n monitoring get pods -l app.kubernetes.io/component=alloy-receiver
# Expected: Running
```

**Step 2: Is the app sending to the right endpoint?**

```bash
kubectl -n otel-lab exec deploy/gateway-api -- env | grep OTEL
# OTEL_EXPORTER_OTLP_ENDPOINT should be:
# http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
```

**Step 3: Can the app reach Alloy?**

```bash
kubectl -n otel-lab exec deploy/gateway-api -- \
  wget -qO- http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
# gRPC will return an HTTP 400 (expected — it's not HTTP/1.1) — this confirms connectivity
```

**Step 4: Check Alloy receiver logs**

```bash
kubectl -n monitoring logs daemonset/grafana-k8s-alloy-receiver --tail=100 \
  | grep -E "error|warn|export"
```

**Step 5: Check Alloy pipeline UI**

```bash
kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring
open http://localhost:12345
# Navigate to Components → otelcol.receiver.otlp.default → check "Data received" counter
```

**Step 6: Is Jaeger accessible?**

```bash
curl -s http://localhost:16686/api/services
# Should return {"data":["gateway-api","order-api",...]}
```

---

## Metrics missing from Prometheus

### Symptoms

- Prometheus has no metrics from app services
- `traces_spanmetrics_calls_total` query returns nothing

### Diagnosis

**Step 1: Check Prometheus is up**

```bash
curl -s http://localhost:9090/-/ready
```

**Step 2: Prometheus has remote-write receiver enabled?**

```bash
kubectl -n otel-lab describe deploy/prometheus | grep -A5 "Command"
# Should include: --web.enable-remote-write-receiver
# and: --enable-feature=exemplar-storage
```

**Step 3: Check Alloy is writing to Prometheus**

```bash
kubectl port-forward svc/grafana-k8s-alloy-receiver 12345 -n monitoring
# Navigate to Components → prometheus.remote_write.local → check "Samples sent" counter
```

**Step 4: Query Prometheus directly**

```bash
curl "http://localhost:9090/api/v1/query?query=up" | jq '.data.result'
```

---

## Async propagation not working

### Symptoms

- `notification.process` span in Jaeger has a different `traceId` than `order.publish`
- SpanLink is missing (no dashed arrow in Jaeger)

### Diagnosis

**Step 1: Verify traceparent is in the RabbitMQ message**

In RabbitMQ Management (`http://localhost:15672`):

1. Go to Queues → `notifications`
2. Click "Get Message(s)"
3. Inspect the Properties → Headers
4. Should contain key `traceparent` with value `00-<32 hex chars>-<16 hex chars>-01`

If missing: the order-api publisher is not injecting the header. Check `OrderPublisher.cs` —
`Propagators.DefaultTextMapPropagator.Inject()` must run while `Activity.Current` is non-null
(inside an active span).

**Step 2: Verify the consumer extracts it correctly**

```bash
kubectl -n otel-lab logs deploy/notification-svc --tail=50 | grep -i trace
```

Add temporary debug logging to `consumer.py`:

```python
logger.debug("headers: %s", properties.headers)
```

**Step 3: Check for pika instrumentation conflict**

If `opentelemetry-instrumentation-pika` is also running, it may overwrite the extracted context.
Verify `requirements.txt` — `opentelemetry-instrumentation-pika` should not be present (we use
manual extraction).

---

## Logs not appearing in Loki with trace correlation

### Symptoms

- "Logs for this span" in Grafana returns no results
- Loki has logs but they lack `trace_id` structured metadata

### Diagnosis

**Step 1: Confirm apps write JSON**

```bash
kubectl -n otel-lab logs deploy/gateway-api --tail=3
# Should be JSON: {"Timestamp":"...","Level":"Information","TraceId":"4bf..."}
# NOT plain text: info: Processing request
```

**Step 2: Check alloy-logs is running**

```bash
kubectl -n monitoring get pods -l app.kubernetes.io/component=alloy-logs
kubectl -n monitoring logs daemonset/grafana-k8s-alloy-logs --tail=50
```

**Step 3: Query Loki directly**

```bash
kubectl port-forward svc/loki 3100 -n otel-lab
curl -G "http://localhost:3100/loki/api/v1/query_range" \
  --data-urlencode 'query={namespace="otel-lab"}' \
  --data-urlencode 'limit=5'
# If logs arrive but lack trace_id, the stage.json field names don't match
```

**Step 4: Check field name mismatch**

Alloy's `stage.json` extracts:

- `.TraceId` for .NET
- `.otelTraceID` for Python

If a service uses different field names, `trace_id` will be empty. Check the raw log JSON.

**Step 5: Verify structured metadata is enabled in Loki**

```bash
kubectl -n otel-lab exec statefulset/loki -- cat /etc/loki/config.yaml | grep allow_structured
# Should show: allow_structured_metadata: true
```

---

## Exemplar dots not showing in Grafana

### Symptoms

- Histogram panels show time series but no scatter dots

### Diagnosis checklist (must ALL be true)

- [ ] Panel → Edit → Query → "Exemplars" toggle is ON
- [ ] Panel → Options → Data links has an entry pointing to Jaeger datasource, URL field =
      `${__value.raw}`
- [ ] Prometheus has `--enable-feature=exemplar-storage`:

  ```bash
  kubectl -n otel-lab describe deploy/prometheus | grep exemplar-storage
  ```

- [ ] App has `OTEL_METRICS_EXEMPLAR_FILTER=trace_based` in Deployment env:

  ```bash
  kubectl -n otel-lab exec deploy/gateway-api -- env | grep EXEMPLAR
  ```

- [ ] The histogram observation happens inside a sampled span. Use `/api/slow` (always sampled) to
      test.

**Force an exemplar-generating request:**

```bash
curl http://localhost:8080/api/slow
# Wait ~5s, then check Grafana panel for new exemplar dot
```

---

## K8s attributes missing from spans

### Symptoms

- Spans in Jaeger lack `k8s.pod.name`, `k8s.namespace.name` etc.

### Diagnosis

**Step 1: Check RBAC**

```bash
kubectl get clusterrolebinding alloy -o yaml
# Should reference ServiceAccount alloy in otel-lab namespace
kubectl auth can-i list pods --as=system:serviceaccount:otel-lab:alloy
# Should return: yes
```

**Step 2: Check k8sattributes processor logs**

```bash
kubectl -n monitoring logs daemonset/grafana-k8s-alloy-receiver --tail=100 \
  | grep -i "k8sattr\|k8s.pod"
```

**Step 3: Verify pod association mode**

The configmap uses `source { from = "connection" }` — it resolves the pod from the OTLP connection
source IP. This works when pods have their own network namespace (standard in k3d). If pods share
the node network namespace, use `source { from = "resource_attribute" }` instead and set
`k8s.pod.name` in the app's `OTEL_RESOURCE_ATTRIBUTES`.

---

## Grafana Cloud export not working

### Symptoms

- Alloy logs show export errors
- Traces/metrics/logs missing in Grafana Cloud

### Diagnosis

```bash
# Mode-aware triage: conf.yml values, pod state, Alloy exporter counters,
# remote-write reachability probe, alloy-receiver endpoint check — start here.
./scripts/debug.sh

# Check the secret exists and is populated
kubectl -n monitoring get secret grafana-cloud-secrets -o json \
  | python3 -c 'import json,sys,base64; d=json.load(sys.stdin)["data"]; [print(f"{k}: {base64.b64decode(v).decode()[:4]}****") for k,v in d.items()]'

# Check Alloy is reading the env vars
kubectl -n monitoring exec daemonset/grafana-k8s-alloy-receiver -- env | grep GRAFANA

# Check Alloy logs for export errors
kubectl -n monitoring logs daemonset/grafana-k8s-alloy-receiver --tail=100 \
  | grep -E "grafana_cloud|export.*fail|endpoint.*empty|401|403"
```

| Error                | Cause                                                                                           | Fix                                                                                                                 |
| -------------------- | ----------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------- |
| `endpoint is empty`  | `conf.yml`'s `monitoring.grafana_cloud.*` is unset, or Alloy wasn't redeployed after it changed | Populate via `./scripts/fetch-grafana-cloud-conf-from-akv.sh`, then `./deploy-local.sh --skip-cluster --skip-build` |
| `401 Unauthorized`   | Wrong API key or wrong instance ID                                                              | Re-check with `./scripts/fetch-grafana-cloud-conf-from-akv.sh --dry-run`; verify Grafana Cloud Access Policies      |
| `connection refused` | Wrong endpoint format                                                                           | Tempo must be `host:443` (no `https://`) in `conf.yml`; the fetch script applies this adjustment automatically      |
| `403 Forbidden`      | API key lacks scope                                                                             | Ensure scopes: `metrics:write logs:write traces:write`                                                              |

> **Prefer `./scripts/fetch-grafana-cloud-conf-from-akv.sh` + `./deploy-local.sh` over
> `make secrets-fetch-akv` / `make secrets-apply` for this.** The Makefile targets are legacy — they
> write the K8s Secret directly and drive their own `helm upgrade`, bypassing `deploy-local.sh`
> entirely, and `secrets-apply` in particular is only as correct as whatever you put in `.env`
> manually. `secrets-fetch-akv` writes the correct Mimir endpoint format (`.../api/prom/push`,
> matching
> [values-cloud.yaml.tmpl](https://github.com/shipsolid/signal-forge/blob/main/k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl)'s
> Prometheus remote_write destination) as of this fix, but the script-based flow remains the
> canonical path — see [[grafana-cloud|docs/deployment/grafana-cloud.md]] for the full credential
> model.

---

## Consumer not processing messages

### Symptoms

- Messages accumulate in RabbitMQ `notifications` queue
- Notification-svc pods appear Running but notifications don't appear

### Diagnosis

**Step 1: Check consumer thread is alive**

```bash
kubectl -n otel-lab logs deploy/notification-svc --tail=50 | grep -i "consumer\|rabbit"
```

**Step 2: Check for backoff**

```bash
kubectl -n otel-lab logs deploy/notification-svc --tail=100 | grep "Consumer crashed"
# If present, the consumer is in exponential backoff — check the delay and underlying error
```

**Step 3: Check RabbitMQ connectivity**

```bash
kubectl -n otel-lab exec deploy/notification-svc -- python3 -c \
  "import pika; pika.BlockingConnection(pika.ConnectionParameters('rabbitmq.otel-lab'))"
# Should succeed with no output
```

**Step 4: Check DLQ**

In RabbitMQ Management → Queues → `notifications.dlq`:

- If messages are here, they were NACKed with `requeue=False` (unrecoverable errors)
- Inspect the message body and headers to understand the failure

---

## Redis connection errors

### Symptoms

- Notification-svc logs: `Redis connection lost, reconnecting`
- Notifications API returns 500

### Diagnosis

```bash
kubectl -n otel-lab get pod -l app=redis
kubectl -n otel-lab exec deploy/notification-svc -- python3 -c \
  "import redis; r=redis.Redis(host='redis.otel-lab'); print(r.ping())"
```

If Redis has restarted, all notification state is lost (ephemeral Deployment, no PVC). Consumer will
re-process messages from RabbitMQ on the next delivery, and new notifications will be stored
correctly.

---

## App pods in CrashLoopBackOff

Most common causes and fixes:

| App              | Likely cause                                                 | Fix                                                                   |
| ---------------- | ------------------------------------------------------------ | --------------------------------------------------------------------- |
| gateway-api      | Missing `GATEWAY_DB_CONNECTION` secret                       | `kubectl -n otel-lab get secret db-secrets` + verify key exists       |
| order-api        | Missing `ORDER_DB_CONNECTION` secret or PostgreSQL not ready | Check datastore pod status                                            |
| notification-svc | RabbitMQ not ready                                           | Consumer has backoff — pod stays Running, consumer retries internally |
| Any              | Image not imported into k3d                                  | `make import`                                                         |

```bash
# Get detailed startup error
kubectl -n otel-lab describe pod <pod-name>
kubectl -n otel-lab logs <pod-name> --previous
```
