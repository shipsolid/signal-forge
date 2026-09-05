---
title: "Signal Forge ADR-007: secretKeyRef for all credentials (no plaintext env vars)"
description: "Stores all database, RabbitMQ, and API credentials in Kubernetes Secrets referenced via secretKeyRef so manifests stay safe to commit."
tags: ["ShipSolid", "Signal Forge", "Architecture"]
updated: 2026-07-10
zettelId: "202607091847-9"
relations:
  - slug: projects/app-signal-forge/architecture/adrs/adr-fail-fast-on-missing-secrets
    kind: related
---

## Signal Forge ADR-007: secretKeyRef for all credentials (no plaintext env vars)

**Status**: Accepted

**Decision**: All database passwords, RabbitMQ credentials, and API keys are stored in Kubernetes
Secrets and referenced via `secretKeyRef` in Deployment env vars. No plaintext credentials in
manifests. See
[[projects/app-signal-forge/architecture/adrs/adr-fail-fast-on-missing-secrets|ADR-006]] for what
happens when a referenced secret is absent.

**Rationale**:

- Kubernetes manifests are typically committed to version control. Plaintext passwords in
  `deployment.yaml` would be exposed to anyone with repo read access and in all git history.
- `secretKeyRef` keeps credential values in the cluster only. Manifests are safe to commit.
- `optional: true` is used on Grafana Cloud secrets only (opt-in feature). All datastore secrets are
  required (no `optional`).

**Alternative considered**: ConfigMap with base64 values — rejected because ConfigMaps are not
access-controlled by default and are not treated as sensitive by cluster operators.
