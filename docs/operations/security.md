# Security

This page is the **threat model and secrets lifecycle** reference. Controls that are heavy enough to
have their own page are linked out:

- [Container hardening](../infrastructure/hardening.md) — securityContext, non-root UIDs,
  readOnlyRootFilesystem, digest-pinned base images, Pod Security Standards
- [Networking & TLS](networking.md) — NetworkPolicy default-deny, Ingress TLS via cert-manager,
  flannel caveat
- [Supply-chain security](supply-chain.md) — CI Trivy scan, Syft SBOM, cosign keyless signing
- [Reliability](reliability.md) — PodDisruptionBudgets, graceful shutdown (defence against
  availability loss during drains)

## Threat model summary

| Threat                             | Control                                                                                                                                            | Page                                                       |
| ---------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------- |
| Credentials in source code         | Kubernetes Secrets sourced from Azure Key Vault via `scripts/fetch-grafana-cloud-conf-from-akv.sh`                                                 | this page, §Secrets management                             |
| Credentials in git history         | `.env` + `conf.yml` tracked as learning-lab scaffolding; **rotate anything real before committing**. `secrets.yaml` uses placeholder base64 values | this page                                                  |
| Container compromise → host escape | `securityContext.runAsNonRoot` + `allowPrivilegeEscalation: false` + `capabilities.drop: [ALL]` + seccomp RuntimeDefault on every workload         | [hardening.md](../infrastructure/hardening.md)             |
| Vulnerable base image              | Digest-pinned `FROM` + monthly refresh; Trivy scan in CI fails on HIGH/CRITICAL with a fix                                                         | [supply-chain.md](supply-chain.md)                         |
| Unauthenticated API access         | `AllowedHosts` restriction; CORS limited to known origins                                                                                          | this page                                                  |
| Injection via user input           | Input validation at API boundary (gateway-api + order-api gRPC layer)                                                                              | this page                                                  |
| Credential leakage in logs         | `logger.exception()` used without string interpolation of sensitive fields                                                                         | this page                                                  |
| Privilege escalation in-cluster    | Alloy ServiceAccount has minimum RBAC; every app pod drops all Linux capabilities                                                                  | this page + [hardening.md](../infrastructure/hardening.md) |
| Poison message amplification       | Dead Letter Queue isolates unprocessable messages                                                                                                  | this page                                                  |
| Unauthorised cross-tier traffic    | `NetworkPolicy` default-deny + tiered allows (apps ↔ datastores, apps → alloy-receiver)                                                            | [networking.md](networking.md)                             |
| MITM on Ingress                    | TLS via cert-manager (self-signed CA → leaf cert on each host)                                                                                     | [networking.md](networking.md)                             |
| Tampered image in production       | cosign keyless signing + CI-side verification on every push; admission-time verification gate at deploy time _not yet wired_ — see supply-chain §Admission enforcement | [supply-chain.md](supply-chain.md)                         |

---

## Secrets management

### Where secrets live

Two independent paths — neither populates the other's Secret:

```
Azure Key Vault (example-org-prd-kv)
  └─ grafana-example-org-* (7 secrets)
        │
        │  make secrets-fetch-akv  (or scripts/fetch-grafana-cloud-conf-from-akv.sh,
        │                            the primary flow — see CLAUDE.md)
        ▼
Kubernetes Secret (grafana-cloud-secrets)
        │
        │  secretKeyRef in Deployment specs
        ▼
Container environment variables (runtime only, not in manifests)

k8s/infra/secrets.yaml (static, hand-rotated base64 values)
        │
        │  kubectl apply -f k8s/infra/secrets.yaml
        ▼
Kubernetes Secret (db-secrets, otel-lab namespace)
        │
        │  secretKeyRef in Deployment/StatefulSet specs
        ▼
Container environment variables (runtime only, not in manifests)
```

`db-secrets` is never AKV-sourced — see §Credential rotation procedure below for how it's actually
rotated by hand.

No plaintext credentials appear in:

- Git-tracked files (`k8s/`, `src/`)
- Docker images
- Pod spec env values (only `secretKeyRef` references)

### Secret keys and consumers

| Secret key              | Consumer               | Required?             |
| ----------------------- | ---------------------- | --------------------- |
| `MYSQL_ROOT_PASSWORD`   | MySQL StatefulSet      | Yes                   |
| `GATEWAY_DB_CONNECTION` | gateway-api Deployment | Yes (fail-fast)       |
| `ORDER_DB_CONNECTION`   | order-api Deployment   | Yes (fail-fast)       |
| `GRAFANA_CLOUD_API_KEY` | Alloy DaemonSet        | No (`optional: true`) |
| `GRAFANA_CLOUD_TEMPO_*` | Alloy DaemonSet        | No                    |
| `GRAFANA_CLOUD_MIMIR_*` | Alloy DaemonSet        | No                    |
| `GRAFANA_CLOUD_LOKI_*`  | Alloy DaemonSet        | No                    |

### Fail-fast on missing required secrets

.NET services throw `InvalidOperationException` at startup if `ConnectionStrings:DefaultConnection`
is empty. This ensures:

- A misconfigured pod fails loudly (CrashLoopBackOff) rather than serving errors silently
- The failure is visible in `kubectl describe pod` with the exact missing variable

```csharp
var connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is required.");
```

### Grafana Cloud secrets — `optional: true`

Cloud credentials use `optional: true` in `secretKeyRef` because cloud export is an opt-in feature.
The service starts without them and degrades gracefully (cloud exporters log errors but local
pipeline continues).

```yaml
env:
  - name: GRAFANA_CLOUD_API_KEY
    valueFrom:
      secretKeyRef:
        name: grafana-cloud-secrets
        key: GRAFANA_CLOUD_API_KEY
        optional: true   # Alloy starts without cloud credentials
```

---

## Input validation

### gateway-api (`OrderEndpoints.cs`)

All inputs are validated at the API boundary before reaching downstream services:

```csharp
if (dto.ProjectId <= 0)
    return Results.ValidationProblem(new Dictionary<string, string[]> {
        { "projectId", ["ProjectId must be a positive integer."] }
    });
if (dto.Amount <= 0 || dto.Amount > 999_999.99)
    return Results.ValidationProblem(...);
if (string.IsNullOrWhiteSpace(dto.Description) || dto.Description.Length > 500)
    return Results.ValidationProblem(...);
```

Returns `HTTP 422 Unprocessable Entity` with a structured error body. No database calls are made for
invalid input.

### order-api (`OrderGrpcService.cs`)

Duplicate validation at the gRPC service boundary:

```csharp
if (request.ProjectId <= 0)
    throw new RpcException(new Status(StatusCode.InvalidArgument, "ProjectId must be positive."));
```

This defence-in-depth means invalid data cannot reach PostgreSQL even if gateway-api validation is
bypassed.

---

## CORS

CORS is restricted to known origins. Wildcard `*` is not used:

```csharp
var corsOrigins = builder.Configuration["Cors:AllowedOrigins"]
    ?? "http://localhost:4200";
builder.Services.AddCors(opts => opts.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins.Split(","))
          .AllowAnyMethod()
          .AllowAnyHeader()));
```

In Kubernetes, `Cors:AllowedOrigins` is set via Deployment env var to the actual frontend hostname.
Faro CORS is `["*"]` — this is intentional because Faro receives RUM data from browsers, not API
calls with sensitive payloads.

---

## AllowedHosts

`AllowedHosts` in `appsettings.json` restricts which `Host` headers are accepted. Wildcard `*` (the
ASP.NET Core default) is replaced with explicit names:

**gateway-api**: `gateway-api,gateway-api.otel-lab,localhost,127.0.0.1` **order-api**:
`order-api,order-api.otel-lab,localhost,127.0.0.1`

This prevents Host header injection attacks in environments where the service is exposed externally.

---

## Credential leakage prevention in logs

Python's `logger.exception()` is used instead of `logger.error("%s", exc)`:

```python
# CORRECT — includes full traceback without interpolating potentially sensitive data
logger.exception("Failed to process order.created event")

# AVOID — if exc contains credential data (e.g., connection string in exception message),
# it could leak to logs
logger.error("Failed to process: %s", exc)
```

.NET's `RecordException(ex)` on spans follows the same principle — exception messages are attached
to span events, not to log messages where they might be processed by log aggregation pipelines.

---

## RBAC (Alloy)

The Alloy ServiceAccount has minimum required permissions:

```yaml
rules:
  - apiGroups: [""]
    resources: ["pods", "nodes", "namespaces", "endpoints", "services"]
    verbs: ["get", "list", "watch"]
  - apiGroups: ["apps"]
    resources: ["replicasets", "deployments", "statefulsets", "daemonsets"]
    verbs: ["get", "list", "watch"]
```

No write permissions. No access to Secrets, ConfigMaps, or service accounts. The ClusterRole is
scoped to read-only operations needed for k8sattributes enrichment and log discovery.

---

## Azure Service Principal

The SP used for `make secrets-fetch-akv` should have:

- **Key Vault Secrets User** role on `example-org-prd-kv` — read-only access to secrets
- No other roles in the subscription

The SP credentials (`ARM_CLIENT_SECRET`) are stored only in `.env` (git-ignored) on the developer's
machine. They are never committed to git or stored in Kubernetes.

---

## Credential rotation procedure

### Grafana Cloud API key

1. Generate a new Access Policy token in Grafana Cloud with scopes:
   `metrics:write logs:write traces:write`
2. Update in AKV:
   `az keyvault secret set --vault-name ... --name grafana-example-org-cloud-api-key --value "glsa_new..."`
3. Re-fetch: `make secrets-fetch-akv`
4. Restart Alloy: `kubectl -n monitoring rollout restart daemonset/grafana-k8s-alloy-receiver`
5. Revoke the old token in Grafana Cloud Access Policies

### Database passwords

1. Update in `k8s/infra/secrets.yaml` (base64 re-encoded)
2. Update the database user password directly (MySQL: `ALTER USER ...; FLUSH PRIVILEGES;`)
3. `kubectl apply -f k8s/infra/secrets.yaml`
4. Restart the consuming service: `kubectl -n otel-lab rollout restart deployment/gateway-api`

> **Note**: Changing the MySQL password requires updating both the Secret and the database before
> restarting the service. Do not restart the service before updating the database — it will fail
> authentication.

---

## Security validation checklist

- [ ] `kubectl -n otel-lab get secret db-secrets -o json | jq '.data'` — all values are base64 (not
      plaintext)
- [ ] `grep -r "password\|Password\|apikey\|api_key" k8s/app/` — no plaintext passwords in manifests
- [ ] `grep -r "AllowedHosts" src/` — no `"*"` values in appsettings.json files
- [ ] `grep -r "WithOrigins" src/` — no `AllowAnyOrigin()` calls in .NET CORS config
- [ ] `.env` is in `.gitignore` — `git status` should never show `.env` as tracked
- [ ] `kubectl auth can-i create secrets --as=system:serviceaccount:otel-lab:alloy` — should return
      `no`
