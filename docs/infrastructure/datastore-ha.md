---
title: "Datastore HA — production migration notes"
description: "Migration paths from signal-forge's single-replica lab datastores to production HA via CloudNativePG, MySQL/Percona, RabbitMQ, and Redis operators."
tags: ["ShipSolid", "Signal Forge", "Infrastructure"]
updated: 2026-07-10
zettelId: "202607091847-17"
relations:
  - slug: projects/app-signal-forge/infrastructure/datastores
    kind: related
  - slug: projects/app-signal-forge/infrastructure/kubernetes
    kind: related
  - slug: projects/app-signal-forge/infrastructure/hardening
    kind: related
---

## Datastore HA — production migration notes

**Status of the signal-forge lab stack:** every datastore is a single-replica StatefulSet on k3d.
This is intentional for the lab — it's not a gap to "fix" in-place, because a 3-replica cluster
cannot schedule on a one-node k3d cluster. This document describes what you would swap in to get
real HA on a multi-node [[kubernetes/readme|Kubernetes]] cluster, and the blast-radius decisions
that come with each option.

## Why the lab manifests aren't safe for production

- `replicas: 1` on every `StatefulSet`. A pod restart + PVC reattach is a complete outage window.
- No replication. If the PVC corrupts, the store is lost.
- No backups scheduled anywhere in-repo. `velero`, `pg_dump` CronJobs, `mysqldump` CronJobs — pick
  at least one before promoting.
- Schema init runs once from a ConfigMap mount (`docker-entrypoint-initdb.d`). There is no migration
  tooling wired in. Changes to the schema in a committed SQL file do NOT re-run on existing PVCs.

## Migration targets

### PostgreSQL → CloudNativePG

Operator: [cloudnative-pg.io](https://cloudnative-pg.io/), production-grade, active upstream.
Manages a primary + N replicas with streaming replication, automated failover, WAL archiving to
object storage, and scheduled base backups.

Swap plan:

1. Install the operator once per cluster:

   ```
   helm repo add cnpg https://cloudnative-pg.github.io/charts
   helm install cnpg cnpg/cloudnative-pg -n cnpg-system --create-namespace
   ```

2. Replace `k8s/datastores/postgres/statefulset.yaml` with a `Cluster` CR:

   ```yaml
   apiVersion: postgresql.cnpg.io/v1
   kind: Cluster
   metadata:
     name: postgres
     namespace: otel-lab
   spec:
     instances: 3
     bootstrap:
       initdb:
         database: orderdb
         owner: orderuser
         secret:
           name: db-secrets    # reuse POSTGRES_PASSWORD key
     storage:
       size: 10Gi
     backup:
       barmanObjectStore:
         destinationPath: "s3://sigforge-pg-backups/"
         s3Credentials:
           accessKeyId:     { name: s3-creds, key: ACCESS_KEY }
           secretAccessKey: { name: s3-creds, key: SECRET_KEY }
   ```

3. Apps keep pointing at `postgres.otel-lab.svc.cluster.local:5432` — the operator creates a
   read-write Service with that exact name.
4. Schema init moves from the ConfigMap to an `initdb.postInitApplicationSQL` block on the Cluster
   CR OR to a Flyway/Liquibase migration CronJob.

**Why not manually run 3 Pods with streaming replication?** Because failover requires consensus.
Without an operator, the primary election is manual and races. Don't hand-roll this.

### MySQL → MySQL Operator (Oracle) or Percona XtraDB Cluster Operator

Both are production-viable. The Oracle operator ships InnoDB Cluster (group-replication, 3-node).
Percona's operator ships Galera (synchronous multi-master).

For signal-forge's read-skewed workload (gateway_db has one hot table), the Oracle InnoDB Cluster is
the simpler match:

```
helm repo add mysql-operator https://mysql.github.io/mysql-operator/
helm install mysql-operator mysql-operator/mysql-operator -n mysql-operator --create-namespace
```

Then replace `k8s/datastores/mysql/statefulset.yaml` with an `InnoDBCluster` CR targeting 3
replicas. Expect ~40% memory overhead per replica vs. the single-node image.

### RabbitMQ → RabbitMQ Cluster Operator

Operator:
[rabbitmq.com/kubernetes/operator](https://www.rabbitmq.com/kubernetes/operator/operator-overview).

Swap the StatefulSet for a `RabbitmqCluster` CR with `replicas: 3` and a `QueuesDefaultType: quorum`
override so queues are Raft-replicated (the default classic queues are NOT safe for cluster
failover).

Critically: every queue the order-api / notification-svc declares needs to be a quorum queue, not a
classic queue. This means changing either the AMQP declaration in application code or setting a
policy on the cluster:

```
rabbitmqctl set_policy ha-all ".*" '{"queue-type": "quorum"}' --apply-to queues
```

### Redis → Redis Sentinel or Redis Cluster

For signal-forge's usage (cache of processed notifications), Sentinel is the simpler swap:

- [bitnami/redis](https://artifacthub.io/packages/helm/bitnami/redis) chart with
  `architecture: replication` and `sentinel.enabled: true`.
- App connection string stays single-endpoint, but must use a Sentinel-aware client.
  `StackExchange.Redis` (for .NET) and `redis-py` (for Python) both support it — configure with
  `"sentinel=mymaster,sentinelHosts=..."`.

For true HA with sharding, use Redis Cluster instead — but then notification-svc needs cluster-aware
client configuration and multi-key operations get constrained to hash-slot groups.

## What isn't solved by any operator

Operators give you replication and failover. They do **not** give you:

- **Backups off-cluster.** Every operator above has a backup story but you must configure the
  object-storage target. No operator ships with that pre-wired.
- **Restore drills.** An unverified backup is an unverified restore. Run a scheduled job that
  restores to a sandbox cluster and asserts row counts.
- **Schema migration.** Flyway, Liquibase, or EF Core migrations are still your responsibility. The
  operator won't run your SQL.
- **Cross-region DR.** If you need to survive a region failure, the operator's in-region HA is not
  sufficient. Add async replication to a second region via the operator's cross-cluster options
  (CNPG, Percona) or an external tool (Barman for Postgres, mysqldump+restore for MySQL).

## Where this repo stops

The signal-forge lab is deliberately scoped to single-replica datastores on single-node k3d. If
you're reading this because you're promoting the stack, you're past the lab's design point — stop
using these manifests directly and treat them as starting examples. Every operator migration above
is a multi-day project, not a find/replace.
