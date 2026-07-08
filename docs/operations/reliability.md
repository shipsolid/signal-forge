# Reliability controls

Workload-level controls that protect availability during planned and unplanned disruption: PodDisruptionBudgets, pod anti-affinity, graceful shutdown.

## PodDisruptionBudgets

All PDBs live in a single manifest for easy inspection: [k8s/infra/pdb.yaml](../../k8s/infra/pdb.yaml).

### App tier — 2 replicas, `minAvailable: 1`

```yaml
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: gateway-api
spec:
  minAvailable: 1
  selector:
    matchLabels:
      app: gateway-api
```

Rolling updates of these deployments respect the PDB automatically — kubelet on a drain will only evict a pod if doing so leaves at least 1 replica Ready. Combined with `replicas: 2` and `maxUnavailable: 25%` (the default), this gives zero-downtime rollouts.

### Datastore tier — single-replica, selector by label

```yaml
apiVersion: policy/v1
kind: PodDisruptionBudget
metadata:
  name: datastores
spec:
  minAvailable: 1
  selector:
    matchLabels:
      tier: datastore
```

With `replicas: 1` on every datastore, `minAvailable: 1` means `kubectl drain` on the node hosting a datastore **will block indefinitely**. That's correct behaviour for the lab's single-replica stores — you lose data if the PVC attaches to a pod that gets replaced during a drain. In prod, this PDB is a forcing function to migrate datastores to the operator-backed HA story (see [datastore-ha.md](../infrastructure/datastore-ha.md)) before you ever need to drain.

If you do need to drain a lab node with a datastore on it: `kubectl drain --force --delete-emptydir-data <node>` bypasses the PDB. Accept the consequences.

## Pod anti-affinity

### Lab / dev — soft preference

Every multi-replica Deployment (gateway-api, order-api, notification-svc) has:

```yaml
affinity:
  podAntiAffinity:
    preferredDuringSchedulingIgnoredDuringExecution:
      - weight: 100
        podAffinityTerm:
          topologyKey: kubernetes.io/hostname
          labelSelector:
            matchLabels:
              app: gateway-api
```

On a single-node k3d cluster this is a no-op — the scheduler only has one node to choose. On any multi-node cluster it ensures the two gateway-api replicas land on distinct nodes whenever possible.

**Soft** (`preferredDuringScheduling...`) means the scheduler prefers the constraint but will violate it if no other placement is feasible (e.g. you have 2 replicas but only 1 schedulable node). This is almost always what you want in dev.

### Prod — hard requirement

The Kustomize prod overlay ([k8s/overlays/prod/kustomization.yaml](../../k8s/overlays/prod/kustomization.yaml)) upgrades anti-affinity from soft to **required**:

```yaml
affinity:
  podAntiAffinity:
    requiredDuringSchedulingIgnoredDuringExecution:
      - topologyKey: kubernetes.io/hostname
        labelSelector:
          matchLabels:
            app: gateway-api
```

With `replicas: 6` on prod gateway-api, this forces six distinct nodes. If the cluster can't provide them (node pool too small), the scheduler leaves surplus pods `Pending` rather than co-scheduling — fail-closed. Keep the node pool at least `replicas × 1` nodes, ideally `replicas × 1.5` for headroom.

## Graceful shutdown

### App tier

```yaml
lifecycle:
  preStop:
    exec:
      command: ["sh", "-c", "sleep 10"]
terminationGracePeriodSeconds: 30
```

The `sleep 10` in preStop is the [kubelet-sig-scheduling best practice][shutdown] for closing the gap between Endpoint removal (instant, via the API server) and kube-proxy propagating it to iptables/IPVS on every node (up to ~5s, often less). During those 5s, a new request arriving at a remote kube-proxy is still routed to the terminating pod. The `sleep 10` keeps the pod serving those stragglers while readiness probes (every 10s) return OK.

After preStop completes, kubelet sends SIGTERM. .NET apps invoke `IHostApplicationLifetime.StopAsync()` which drains in-flight requests; the 30s grace period covers this. Python / uvicorn does similar for FastAPI routes.

[shutdown]: https://learnk8s.io/graceful-shutdown

### Datastore tier

```yaml
terminationGracePeriodSeconds: 60
```

No preStop for datastores — the images' own entrypoints handle SIGTERM (mysqld with `innodb_fast_shutdown=1`, postgres with checkpoint-then-exit, rabbitmq with `rabbitmqctl stop`). The 60s grace is conservative; PostgreSQL can take that long to flush a busy WAL.

Redis has `terminationGracePeriodSeconds: 30` — AOF `appendfsync everysec` means at most 1s of writes is unflushed at SIGTERM, and `SHUTDOWN` from the entrypoint is fast.

### What if preStop sleep is wasted?

It usually is — most requests finish in <100ms and kube-proxy propagates faster than 5s in healthy clusters. The `sleep 10` is insurance for pathological cases (overloaded kube-proxy, high node count, network CNI delays). If you're on a small cluster (≤10 nodes) and you measure sub-100ms iptables update latency via `kubectl get endpointslices`, reduce to `sleep 5`. Don't set it to 0 — the Endpoint-removal race is real.

## Health probes — what each one protects

| Probe            | Triggers        | Protects against                                                                                                                                                                               |
| ---------------- | --------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------- |
| `startupProbe`   | first N seconds | slow-starting container marked `NotReady` by liveness before it finishes init — none of our workloads have one; .NET + Python init is <20s so the `initialDelaySeconds` on readiness covers it |
| `readinessProbe` | all lifetime    | hasn't started / in-process rollout                                                                                                                                                            | routing traffic to a pod that can't serve |
| `livenessProbe`  | after startup   | deadlocked / stuck pod — wedged event loop, deadlocked thread                                                                                                                                  | indefinite availability loss              |

**Frontend** now has both readiness AND liveness (added in the hardening pass). Before, it had only readiness — a wedged nginx worker would stay in the Endpoints list indefinitely until someone noticed.

**Probes don't call `/healthz`** on datastores — they use native clients (`mysqladmin ping`, `pg_isready`, `redis-cli ping`, `rabbitmq-diagnostics check_port_connectivity`). This is more accurate than an HTTP healthz because it exercises the actual client protocol and auth.

## What this doesn't cover

- **VerticalPodAutoscaler.** Fixed resource requests/limits per env. Add if right-sizing becomes a concern.
- **SLO-burn-rate-driven HorizontalPodAutoscaler.** The prod overlay ([hpa.yaml](../../k8s/overlays/prod/hpa.yaml)) has an illustrative CPU-utilization HPA on gateway-api/order-api, but a real SLO-driven one (scaling on the burn-rate recording rules in [slo-rules.yaml](../../k8s/monitoring/slo-rules.yaml) instead of raw CPU) needs a custom-metrics adapter (e.g. `prometheus-adapter`) — not wired up. Requires metrics-server in the target cluster either way; not installed by `deploy-local.sh` since local dev has no autoscaling need.
- **Topology spread constraints.** Pod anti-affinity handles the common "don't co-schedule" case. For AZ-level spread (prod on multi-AZ), add `topologySpreadConstraints` with `topologyKey: topology.kubernetes.io/zone`.
- **Disruption budget for datastores' PVCs.** If you migrate to the operator-backed HA story, the operator handles its own PDBs. Delete the `datastores` PDB when you do.
