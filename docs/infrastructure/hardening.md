---
title: "Container & Pod Hardening"
description: "Reference for signal-forge's Kubernetes Pod Security Standards hardening — per-image UIDs, Dockerfile conventions, and the security controls on every workload."
tags: ["ShipSolid", "Signal Forge", "Infrastructure"]
updated: 2026-07-10
zettelId: "202607091847-19"
relations:
  - slug: projects/app-signal-forge/operations/supply-chain
    kind: depends_on
  - slug: projects/app-signal-forge/infrastructure/datastores
    kind: related
  - slug: projects/app-signal-forge/infrastructure/kubernetes
    kind: related
---

## Container & Pod Hardening

Every workload in signal-forge runs under the [[kubernetes/readme|Kubernetes]] [Pod Security
Standards "restricted" profile][pss] — no exceptions. This page is the reference for _what_ is
hardened, _where_ it's set, and _why_ each control is there.

[pss]: https://kubernetes.io/docs/concepts/security/pod-security-standards/#restricted

## Baseline applied to every workload

| Control                           | Pod-level | Container-level | Rationale                                                                                                       |
| --------------------------------- | --------- | --------------- | --------------------------------------------------------------------------------------------------------------- |
| `runAsNonRoot: true`              | ✓         |                 | Refuses to schedule if the image's `USER` is root or UID 0                                                      |
| `runAsUser` / `runAsGroup`        | ✓         |                 | Explicit, per-image UID. Documented in the manifest comment next to each value                                  |
| `fsGroup`                         | ✓         |                 | PV / emptyDir ownership. On RabbitMQ paired with `fsGroupChangePolicy: OnRootMismatch`                          |
| `seccompProfile: RuntimeDefault`  | ✓         |                 | Default seccomp filter rejects unusual syscalls                                                                 |
| `allowPrivilegeEscalation: false` |           | ✓               | Blocks `setuid` / `fcaps` escalation                                                                            |
| `capabilities.drop: [ALL]`        |           | ✓               | No Linux capabilities granted                                                                                   |
| `readOnlyRootFilesystem: true`    |           | ✓               | Enforced everywhere (see §Datastores + §Frontend) — emptyDir provided wherever a workload needs a writable path |

## Per-image UID mapping

The `runAsUser` values match the UID the base image already uses, so `chown` in Dockerfile is
unnecessary.

| Workload         | Image                                 | UID  | Source of UID                                     |
| ---------------- | ------------------------------------- | ---- | ------------------------------------------------- |
| gateway-api      | `mcr.microsoft.com/dotnet/aspnet:8.0` | 1654 | Microsoft-provided `app` user (since .NET 8 GA)   |
| order-api        | `mcr.microsoft.com/dotnet/aspnet:8.0` | 1654 | same                                              |
| notification-svc | `python:3.12-slim`                    | 1000 | `app` user we create in the Dockerfile            |
| otel-frontend    | `nginxinc/nginx-unprivileged:alpine`  | 101  | nginx user, image listens on 8080 (see §Frontend) |
| mysql            | `mysql:8.0`                           | 999  | `mysql` user shipped in image                     |
| postgres         | `postgres:16.4`                       | 999  | `postgres` user                                   |
| redis            | `redis:7.4-alpine`                    | 999  | `redis` user                                      |
| rabbitmq         | `rabbitmq:3.13.7-management`          | 999  | `rabbitmq` user                                   |

Changing any of these UIDs is a two-file change: the Dockerfile's `USER` directive (if we own the
Dockerfile) **and** the Deployment / StatefulSet's `securityContext.runAsUser`. `kubectl` refuses to
schedule pods if the two disagree and `runAsNonRoot: true` is set.

## Dockerfile conventions

- **Base images pinned by digest** (not tag). The `:8.0@sha256:...` form means a rebuild on any
  other machine pulls the same bytes. Refresh digests with:

  ```
  docker buildx imagetools inspect <image>:<tag> --format '{{.Manifest.Digest}}'
  ```

- **Multi-stage**. Build-stage tools (SDKs, node_modules) are never in the runtime image.
- **No secrets in layers**. `FARO_API_KEY` is the only `ARG` we accept, and webpack inlines it at
  build time into the bundle — it's a user-side source-map upload token, not a server-side secret.
- **USER as the last instruction before ENTRYPOINT**. Everything after `USER` runs as non-root,
  including the entrypoint.

## Frontend: no longer an exception

`readOnlyRootFilesystem: true` **is** set on the frontend. It used to be omitted because
`docker-entrypoint.sh` wrote `/usr/share/nginx/html/assets/env.js` into the container's own root
filesystem at startup to inject runtime env vars (`FARO_URL`, `API_BASE_URL`) — neither an
initContainer nor an extra `emptyDir` for the whole `assets/` directory was actually needed once
volume-mount semantics are used correctly: `env.js` is now a single-key `frontend-env-js` ConfigMap
(rendered by `deploy-local.sh`'s `apply_frontend_env_configmap()`, straight from the same Grafana
Cloud credentials already resolved for `grafana-cloud-secrets` — no Secret round-trip) mounted with
`subPath` directly over that one file. Volume mounts are exempt from `readOnlyRootFilesystem`, so
the entrypoint script is gone entirely — the image's `ENTRYPOINT` now just runs `nginx` directly,
and the Dockerfile bakes in a default `env.js` (used as-is for a bare `docker run` outside K8s) that
the ConfigMap mount shadows in-cluster.

nginx's own writable paths (`/var/cache/nginx`, `/var/run`, and `/tmp` for client-body temp files)
are covered by three `emptyDir` mounts — the same idiom already used for Redis's `/data` below.

All five restricted-profile controls (non-root, drop ALL caps, no priv-esc, seccomp,
`readOnlyRootFilesystem`) are now enforced on every workload in this repo, no exceptions.

## Datastores: what's different

- `readOnlyRootFilesystem` is **not** set on mysql/postgres/rabbitmq. All three write to non-volume
  paths (tmp, sockets, entrypoint-generated configs). Redis is the one datastore where it _is_ set —
  its runtime touches `/data` (mounted emptyDir) and nowhere else.
- `fsGroupChangePolicy: OnRootMismatch` on rabbitmq. Without this, every pod mount rewrites group
  perms on `.erlang.cookie`, which the Erlang auth module rejects ("cookie file must be accessible
  by owner only").
- `terminationGracePeriodSeconds: 60` on all stateful stores. MySQL and Postgres need time to flush,
  RabbitMQ needs to drain in-flight deliveries.

## preStop + terminationGracePeriodSeconds on the app tier

Every app Deployment has:

```yaml
lifecycle:
  preStop:
    exec:
      command: ["sh", "-c", "sleep 10"]
terminationGracePeriodSeconds: 30
```

Why `sleep 10`: when kubelet sends SIGTERM, the Service endpoint for that pod is simultaneously
removed from the Endpoints object — **but** kube-proxy takes up to ~5s to propagate that to
iptables/IPVS on every node. Without the `sleep`, in-flight requests from other pods race against
that propagation and see connection resets. 10s is a conservative upper bound; the pod is still
serving health checks during this window so readiness probes correctly return green for the grace
period.

The 30s grace period gives .NET's `IHostApplicationLifetime` time to complete in-flight HTTP
requests (its default shutdown timeout is 30s).

## Verifying the baseline

From a deployed cluster:

```bash
# Every app pod runs as non-root:
kubectl -n otel-lab get pods -l tier=app -o jsonpath='{range .items[*]}{.metadata.name}{"  uid="}{.spec.securityContext.runAsUser}{"\n"}{end}'

# No container holds any capabilities:
kubectl -n otel-lab get pods -l tier=app -o jsonpath='{range .items[*]}{.metadata.name}{"  caps="}{.spec.containers[0].securityContext.capabilities}{"\n"}{end}'

# Root filesystem is read-only (expect true for every pod, no exceptions):
kubectl -n otel-lab get pods -o jsonpath='{range .items[*]}{.metadata.name}{"  readOnly="}{.spec.containers[0].securityContext.readOnlyRootFilesystem}{"\n"}{end}'
```

## What this doesn't cover

- **PSP / PSA enforcement.** The manifests are restricted-profile compliant, but there is no
  namespace-level `pod-security.kubernetes.io/enforce: restricted` label. Add it when promoting to a
  real cluster — the manifests will continue to schedule; any regression will fail-closed.
- **Image signing at admission.** Signed images land in GHCR ([[supply-chain|supply-chain.md]]) but
  no admission webhook verifies signatures at deploy time. Consider [sigstore/policy-controller] or
  [connaisseur] in front of prod.
- **AppArmor / SELinux profiles.** Default RuntimeDefault is what we set; no per-app profile
  authoring.

[sigstore/policy-controller]: https://docs.sigstore.dev/policy-controller/overview/
[connaisseur]: https://github.com/sse-secure-systems/connaisseur
