# SignalForge — Principal/Staff Engineering Review

**Repo:** shipsolid/app-signal-forge **Date:** 2026-07-08 (findings) / trimmed 2026-07-08 **Reviewer
lens:** interview/portfolio signal _and_ real production sign-off

SignalForge is a four-service, three-language distributed system (Angular → gateway-api
[.NET/gRPC-BFF] → order-api [.NET/gRPC] → RabbitMQ → notification-svc [Python]) built to demonstrate
cross-language OpenTelemetry propagation under a dual-mode observability pipeline (in-cluster vs.
Grafana Cloud). Five parallel deep-dives covered the backend services, messaging & frontend, the
observability pipeline, Kubernetes/Helm/infra, and testing/documentation integrity.

> This is the original five-domain review with every closed item removed (verified against current
> source, not just against the fix-list's own claims — see note at the bottom). What remains below
> is open work only. The credential-exposure incident and 15 other items were found, fixed, and
> verified closed on 2026-07-08; that record isn't reproduced here.

**Open findings: 1 Critical · 9 High · 22 Medium · ~30 Low/Nit**

## Contents

- [SignalForge — Principal/Staff Engineering Review](#signalforge--principalstaff-engineering-review)
  - [Contents](#contents)
  - [1. What's genuinely strong](#1-whats-genuinely-strong)
  - [2. Domain findings](#2-domain-findings)
    - [2.1 Backend services \& gRPC contracts (.NET)](#21-backend-services--grpc-contracts-net)
    - [2.2 Messaging (notification-svc) \& frontend](#22-messaging-notification-svc--frontend)
    - [2.3 Observability pipeline](#23-observability-pipeline)
    - [2.4 Kubernetes / Helm / infra \& security](#24-kubernetes--helm--infra--security)
    - [2.5 Testing \& documentation integrity](#25-testing--documentation-integrity)
  - [3. Verdict matrix](#3-verdict-matrix)
  - [4. Closing assessment](#4-closing-assessment)

---

## 1. What's genuinely strong

A fairness check before the findings — several of these are the kind of judgment that's hard to fake
in an interview.

- **Outbox pattern done correctly** — the order write and its outbox row commit in one transaction,
  giving real at-least-once event delivery instead of "publish inline and hope."
- **Cursor-streamed gRPC** (`AsAsyncEnumerable`, ADR-010) — O(1) memory on large result sets, with
  cancellation threaded through, instead of the naive `ToListAsync()` that OOMs at scale.
- **Correct async trace semantics** — the RabbitMQ hop uses a `SpanLink`, not a fabricated
  parent-child edge, matching OTel messaging semantic conventions exactly, with an ADR that reasons
  through the NACK/redelivery case most teams miss (ADR-002).
- **spanmetrics before tail-sampling** (ADR-003) — RED metrics are computed on 100% of traffic
  before the 25% sample is taken, so dashboards don't silently read 4x low. This ordering detail is
  one most engineers get backwards on the first pass.
- **Real multi-window, multi-burn-rate SLO alerts** — correct 14.4×/6× burn-rate math with a
  `clamp_min` divide-by-zero guard, not naive threshold alerts wearing an "SLO" label.
- **Fail-fast on missing secrets** (ADR-006) and **`secretKeyRef`-everywhere** (ADR-007) — no silent
  defaults, no plaintext credentials in manifests.
- **Dead-letter queue offloaded to RabbitMQ's native mechanism** (ADR-008) instead of a hand-rolled
  Redis retry counter — the simpler, more correct choice.
- **Substantive test suites** — 119+ tests across the stack with genuine boundary-value and
  failure-path coverage (Moq-based gRPC status mapping, Redis-down → DLQ, invalid JSON → DLQ), not
  scaffolding.
- **A real CI supply chain** — Trivy + Syft CycloneDX SBOM + cosign keyless OIDC signing +
  `pip-audit` + `dotnet list package --vulnerable` + `gitleaks`, correctly gated to `main`.
- **`deploy-local.sh`'s defensive guards** — k3d context assertion, NodePort-drift check parsed
  straight from Service manifests, secret-key contract validation before `helm upgrade` — read as
  scars from real incidents, not checklist theater.
- **Docs that name their own gaps** — `datastore-ha.md`, `reliability.md`, and `supply-chain.md`
  each have a "what this doesn't cover" section that actually matches the manifests, instead of
  implying false completeness.
- **Genuine ADRs** — real trade-offs, rejected alternatives with reasons, code patterns included.
  Not templated filler.

---

## 2. Domain findings

Grouped by subsystem. Critical/High/Medium findings get full detail; Low/Nit items are compacted
into a list at the end of each subsection rather than dropped.

### 2.1 Backend services & gRPC contracts (.NET)

**🟠 HIGH · both** — The outbox refactor broke the documented trace shape, and the docs never caught
up `docs/api/grpc.md:150`, `docs/services/order-api.md:97-125` vs.
`src/order-api/Services/OrderGrpcService.cs:85-99`, `Messaging/OutboxRelayWorker.cs:101-109` Docs
describe `order.publish` as a synchronous child span of `order.create`. The real code writes an
outbox row and returns; publishing happens later in a background worker that starts a disconnected
root span. The documented failure mode ("RabbitMQ publish fails → `RpcException(Internal)`") is
simply wrong for current behavior — a broker outage no longer fails `CreateOrder` at all. A better
outcome, shipped with stale documentation of the old, less-resilient behavior. _Why it matters:_
narrating a design you haven't re-verified against your own implementation after a refactor is
exactly the kind of thing that surfaces under "walk me through this trace" in an interview.

**🟠 HIGH · production-readiness** — The outbox relay has a multi-replica race, undocumented
`src/order-api/Messaging/OutboxRelayWorker.cs:77-99` order-api runs 2 replicas per its own docs. The
relay polls `WHERE ProcessedAt IS NULL` with no row locking (`FOR UPDATE SKIP LOCKED`) and no leader
election. Two pods can select and publish the same batch in the same window — silently tolerated
only because notification-svc happens to dedupe downstream, which is incidental, not designed-in.

**🟡 MEDIUM · interview-signal** — `plant.id` enrichment is dead code end-to-end
`src/order-api/Program.cs:91-94` vs. `src/gateway-api/Endpoints/OrderEndpoints.cs:45-50` order-api's
comment says `X-Plant-Id` is "forwarded by gateway-api from the original browser request," but
nothing in gateway-api attaches it to outbound gRPC metadata — HTTP headers don't auto-forward into
gRPC calls. Every order-api span's `plant.id` attribute is empty for gateway-originated traffic,
verifiably, not speculatively.

**🟡 MEDIUM · production-readiness** — `AllowedHosts` is wildcarded in code; docs claim it's locked
down `src/gateway-api/appsettings.json:18`, `src/order-api/appsettings.json:18` Both are
`"AllowedHosts": "*"`, with no k8s manifest override, contradicting the specific allow-list
`docs/services/gateway-api.md:69` describes. Low impact behind an ingress today, but it's a
documented control that doesn't exist.

**🟡 MEDIUM · interview-signal** — Proto contract exists in three copies with no enforced behavioral
sync `src/proto/orders.proto`, order-api's and gateway-api's local `Protos/orders.proto` Neither
service builds from the shared `src/proto/` copy — each has its own, already textually diverged in
comments/formatting. CI's `proto-sync` job does structurally diff the three copies (a real, if
shallow, protection against copy-paste drift), but nothing verifies the _implementations_ on both
sides still agree behaviorally — see the `GetOrdersByProject` validation-contract gap in §2.5.

**🟡 MEDIUM · production-readiness** — Blanket exception→502 mapping erases gRPC status semantics
for most failure modes `src/gateway-api/Endpoints/OrderEndpoints.cs:75` (`CreateOrder`), `:164`
(`GetNotifications`), `ProjectEndpoints.cs:178` `CreateOrder` and the project/notification endpoints
still catch `Exception` generically and always return 502, regardless of whether the underlying
`RpcException` was `InvalidArgument`, `Unavailable`, or `Internal`. (`GetOrder`, wired up since this
review, does correctly special-case `NotFound` → 404 at `OrderEndpoints.cs:109` before falling
through to the same generic 502 for anything else — proof the pattern is known, just not applied
everywhere.) Still a latent bug for the next failure mode that isn't `NotFound`.

**🟡 MEDIUM · production-readiness** — gRPC streaming's memory benefit is discarded one hop later,
with no size cap `src/gateway-api/Endpoints/ProjectEndpoints.cs:148-164` order-api correctly streams
via `AsAsyncEnumerable()`, but gateway-api drains the entire stream into an in-memory list before
returning one JSON blob — and unlike `GetNotifications`'s explicit 1 MB guard, there's no cap here.
A project with a large order history means unbounded gateway memory growth per request.

**🟡 MEDIUM · production-readiness** — No EF Core connection retry on either database provider
`src/order-api/Program.cs:39-40` (Npgsql), `src/gateway-api/Program.cs:39-40` (MySql) Fail-fast on a
missing connection string at startup is correct (ADR-006), but neither configures
`EnableRetryOnFailure()` — a transient connection blip mid-operation isn't retried at the ORM layer
at all.

**Also worth a look — backend:**

- Money modeled as `double` in the proto contract and in the `orders.amount.total` metric — fine for
  an approximate signal, wrong type for anything resembling a ledger.
- Validation magic numbers (`999_999.99`, 500-char limits) duplicated verbatim across gateway-api
  and order-api with nothing keeping them in sync.
- A code comment in `OrderGrpcService.cs` still describes the pre-refactor "loads everything into
  memory" implementation directly above code that already uses `AsAsyncEnumerable()`.
- Dead `Npgsql.OpenTelemetry` package reference — the code's own comment explains it's no longer
  used.
- `OrderPublisher.cs`, self-labeled "the most critical instrumentation point in the lab," is mocked
  away in every test that touches it — the actual RabbitMQ publish/header-encoding logic has zero
  direct test coverage.
- Unauthenticated client headers (`X-Plant-Id`, user-agent) land on spans with no length cap — low
  risk today, but an open door to trace-storage cost/tag-index abuse.
- Dead `InflightMiddleware` class in gateway-api, never registered, its own comment admits it's a
  placeholder.

### 2.2 Messaging (notification-svc) & frontend

**🔴 CRITICAL · both** — Docs describe an idempotency mechanism that isn't what the code does
`docs/services/notification-svc.md:118-146` vs. `app/consumer.py:192-203` The docs show atomic
`redis.hsetnx()` on a single `notifications:{order_id}` key with a 24h TTL. The real implementation
checks a _separate_ `dedup:{order_id}` key via atomic `SET ... NX` with a _different_ (1h) TTL. The
underlying race this describes was fixed (see below), but the doc was never brought back in line
with what shipped — it still names the wrong key, the wrong primitive, and the wrong TTL. Anyone
designing against — or citing in an interview — the documented mechanism would be describing a
system that isn't running.

**🟠 HIGH · both** — A single broad `except Exception` sends transient infra failures and poison
messages to the DLQ alike, with zero retry `consumer.py:235-241`; contradicts
`docs/services/notification-svc.md:221` and ADR-008 Confirmed intentional and tested
(`test_redis_failure_nacks_to_dlq`) — but it contradicts both the service doc ("Redis unavailable →
NACK requeue=True") and ADR-008's stated retry-then-DLQ design, neither of which is actually
implemented. A Redis pod restart during any routine deploy would immediately dead-letter live
traffic with no automatic recovery.

**🟠 HIGH · interview-signal** — Frontend docs describe a runtime-config mechanism that was replaced
and never updated `docs/services/frontend.md:110-120` vs. `docker-entrypoint.sh:19-24`,
`faro.ts:13-27` The doc describes `envsubst` rewriting the compiled bundle directly — which would
actively conflict with nginx's immutable JS caching. The real, better mechanism writes a separate
`window.__ENV` object to `assets/env.js`. Good thing the documented approach isn't real; bad that a
doc meant to onboard someone to the mechanism describes the wrong one.

**🟠 HIGH · production-readiness** — A runtime config knob is wired end-to-end and never actually
consumed `k8s/app/frontend/deployment.yaml:50-51`, `docker-entrypoint.sh` vs. `api.service.ts:45`
`API_BASE_URL` is set in the Deployment, written into `env.js` as `window.__ENV.API_BASE_URL` — and
never read. The app pulls `environment.apiBaseUrl` from the build-time Angular environment file
instead. It only "works" today because both defaults happen to match. Change the Deployment env var
in a different cluster and it silently does nothing — a classic
confusing-incident-during-environment-promotion bug.

**🟡 MEDIUM · production-readiness** — Mismatched TTLs let redelivery duplicate the notification
list Dedup key 1h TTL vs. notification record 24h TTL, `consumer.py` A redelivery between 1h and 24h
bypasses dedup, overwrites the notification hash, and unconditionally pushes a second copy of the
same ID onto the list — no dedup on the list itself.

**🟡 MEDIUM · interview-signal** — The `readOnlyRootFilesystem` exception write-up misses the
simplest fix `docs/infrastructure/hardening.md:48-66` Two options are weighed (emptyDir +
initContainer, or an initContainer writing `env.js`) and neither is the standard idiom: mount
`env.js` as a ConfigMap volume with `subPath` directly. Volume mounts are exempt from
`readOnlyRootFilesystem` — no entrypoint script or initContainer needed, and the one workload
missing this protection (the most exposed one) could have it.

**🟡 MEDIUM · production-readiness** — No resilience layer in the Angular HTTP client — every
component hand-rolls error handling `app.config.ts` (no interceptors); `dashboard.component.ts:47`,
`create-order.component.ts:70` No retry/backoff/timeout, no centralized error normalization. A
transient 502 from gateway-api surfaces immediately as a raw `HttpErrorResponse.message` string,
which can leak the upstream URL to the user.

**🟡 MEDIUM · production-readiness** — No JS dependency vulnerability scanning anywhere in CI
`.github/workflows/ci.yml:218-227` Python gets `pip-audit`, .NET gets
`dotnet list package --vulnerable`, all four stacks get Trivy — but Trivy's frontend scan runs
against the final nginx runtime image, after the multi-stage build has already discarded
`node_modules`/`package.json`. There's nothing left to scan, and no `npm audit` step exists
elsewhere. A silent, asymmetric SCA gap relative to the other three stacks.

**Also worth a look — messaging & frontend:**

- Unused `opentelemetry-instrumentation-pika` dependency — the code's own docstring explains why
  manual extraction is used instead.
- Money modeled as `float` in notification models, matching the same anti-pattern found on the
  backend.
- `RABBITMQ_USER: guest` hardcoded as a literal while the password is sourced from a Secret — an
  asymmetric half-measure on an otherwise-consistent secret contract.
- Faro's PII scrubbing is a fragile `string.includes('/healthz')` check — filters health-check
  noise, provides no real redaction of user-entered data.
- The frontend's Jest suite itself is unusually rigorous for a lab — real DOM assertions,
  `HttpTestingController` with `http.verify()`, fake-timer navigation tests. Worth noting as a
  genuine strength inside this subsection.

### 2.3 Observability pipeline

**🟠 HIGH · both** — A structured "Dual-Export" section documents a feature that doesn't exist
`docs/OTEL-PATTERNS.md:557-561` (§12, "Grafana Cloud Dual-Export") A full numbered section with its
own architecture diagram claims Alloy "dual-exports all signals to Grafana Cloud when credentials
are present." Local and cloud mode are mutually exclusive per `CLAUDE.md`'s own framing — there is
no dual-export path anywhere in the live config. It's also where real AKV vault/secret naming lives
(see the finding below).

**🟠 HIGH · production-readiness** — Real employer infrastructure naming is committed as plaintext
`Makefile:159-167`, `.env.example:60`, `docs/deployment/grafana-cloud.md`, `docs/OTEL-PATTERNS.md`,
`scripts/fetch-grafana-cloud-conf-from-akv.sh` All hardcode the literal `grafana-mccaindev-*` AKV
secret-name prefix. Even with values redacted, publishing the exact secret-naming convention
unambiguously identifies the employer and environment tier. Defensible for opaque IDs; doesn't
extend to a self-describing secret-naming convention tied to a real company.

**🟡 MEDIUM · production-readiness** — The multi-window burn-rate SLO alerts never fire out of the
box `conf.yml:137` (`slo_rules.enabled: false`); `values-cloud.yaml.tmpl:116-117`
(`prometheusOperatorObjects` disabled) Even enabled, applying the `PrometheusRule` against Grafana
Cloud Mimir requires a manual `cortex-tool rules load` step never invoked by `deploy-local.sh`. Good
SLO math with no evaluation path is a common way "we have SLOs" claims fall apart under review — the
honest current answer to "show me the alert firing" is "it can't, by default."

**🟡 MEDIUM · both** — `project_id` as a Prometheus label — the one place cardinality discipline
slips `docs/observability/otel-contracts.md:283-289` — `orders.created.total`,
`orders.amount.total`, `orders.processing.duration` Exactly the tenant/entity-ID-as-label pattern
this project's own engineering principles flag as an automatic stop for unbounded cardinality.
Contrast with the collector-level `spanmetrics` dimensions, which are all correctly
fixed-cardinality. Low blast radius at lab scale; wouldn't survive a real multi-tenant install with
no relabel/drop rule or stated bound.

**🟡 MEDIUM · production-readiness** — No `memory_limiter` or exporter retry/queue configuration
anywhere in the Alloy pipeline `k8s/monitoring/` (grepped for `memory_limiter`, `sending_queue`,
`retry_on_failure` — zero hits) The batch processor buffers by size/timeout only, with no
backpressure protection against a traffic spike OOM-killing Alloy, and none of the three local
exporters configure a sending queue or retry policy — a transient backend blip drops data outright
instead of buffering. Fine for a lab demo; toy-grade if presented as production-representative, and
never caveated as such.

**🟡 MEDIUM · both** — Loki's `derivedFields` regex can never match — log→trace click-through is
dead `k8s/monitoring/local/grafana/provisioning/datasources.yaml:32-36` Expects `"trace_id":"(\w+)"`
in the raw log line body, but the pipeline promotes `trace_id` only as structured metadata — the raw
JSON still contains `TraceId` (.NET) or `otelTraceID` (Python), neither of which matches. The
trace→logs direction works correctly; the reverse direction — click a log line, jump to its trace —
is dead configuration that a five-minute click-through would have caught.

**Also worth a look — observability pipeline:**

- The local-mode DaemonSet still injects unused Grafana Cloud secret env vars that its own River
  config never reads — vestigial coupling from before the local/cloud split was cleanly separated.
- An orphaned config file still comments the chart version as 3.6.0 against the live 3.8.4 pin — one
  more signal it should be deleted, not reconciled.

### 2.4 Kubernetes / Helm / infra & security

**🟠 HIGH · both** — The Mimir-endpoint footgun is confirmed still live, with a second-order
diagnostic effect `Makefile:150-173` vs. `scripts/fetch-grafana-cloud-conf-from-akv.sh:130` and
`values-cloud.yaml.tmpl` The legacy `make secrets-fetch-akv` still writes the OTLP-style Mimir
endpoint (`/api/v1/otlp`) into the Secret while the canonical path requires the Prometheus
remote_write form (`/api/prom/push`). The runbook and `CLAUDE.md` now both explicitly warn against
running this target for this purpose — a real mitigation — but the target itself still does the
wrong thing if anyone runs it anyway, and `scripts/debug.sh`'s remote-write reachability probe would
still be checking a Secret carrying the wrong URL if it did.

**🟡 MEDIUM · production-readiness** — `docs/operations/security.md` contradicts itself on where
`db-secrets` comes from Its own diagram routes AKV → `make secrets-fetch-akv` → `db-secrets`. That
target only ever populates `grafana-cloud-secrets`; `db-secrets` is the static file rotated by hand
per the same doc's later rotation-procedure section. An on-call engineer following the diagram looks
in the wrong place.

**🟡 MEDIUM · both** — A NetworkPolicy ingress rule allows any namespace, not just the ingress
controller `k8s/infra/network-policies.yaml:53-74` (`allow-ingress-from-controller`,
`namespaceSelector: {}`) Deliberately avoids hardcoding `kube-system`, per the docs' own tradeoff
note — but it materially weakens the "default-deny + tiered allow" story: a compromised pod in any
namespace (including `monitoring`) can hit app-tier ports directly, bypassing the ingress
controller. Combined with the self-documented fact that flannel doesn't enforce any of this on the
actual dev cluster, the whole NetworkPolicy suite is currently unverified-in-practice.

**🟡 MEDIUM · production-readiness** — RabbitMQ's application identity is the reserved `guest`
account `k8s/datastores/rabbitmq/statefulset.yaml:41`; order/notification deployments Very likely
functional (the official image's entrypoint lifts the loopback-only restriction when credentials are
set explicitly), but shipping the reserved default account as the permanent cross-pod principal —
with no purpose-named user/vhost — is flagged nowhere as a pre-prod TODO the way the DB passwords
are.

**🟡 MEDIUM · production-readiness** — Two supply-chain claims read stronger than what's actually
enforced `ignore-unfixed: true` in Trivy's gate means an unpatched HIGH/CRITICAL CVE ships, gets
cosign-signed, and passes CI silently — visible only in the Security tab, not blocking, and there
are actionable mitigations beyond "wait for upstream" (pin an alternate base, scheduled re-scan of
published images, tiered fixed/unfixed policy). Separately, cosign signatures are produced but
verified nowhere downstream — "we sign our images" currently provides audit value only, zero
enforcement, which is a materially weaker claim than it sounds.

**Also worth a look — infra:**

- No HPA/VPA anywhere — honestly disclosed in `reliability.md`'s "what this doesn't cover," but
  since resource requests and SLO burn-rate rules already exist, one illustrative HPA wired to them
  would have been a stronger signal than a static replica count.
- `k8s/infra/ingress.yaml` ships a duplicate hostless catch-all rule with no `ssl-redirect`,
  deliberately for dev convenience — a real risk if copy-pasted forward without re-reading the
  comment.
- Prod PDBs don't scale with the prod overlay's replica-count patch — a percentage-based
  `minAvailable` would track automatically instead of needing a separate patch that doesn't
  currently exist.
- `docs/operations/security.md`'s RBAC excerpt for the Alloy ServiceAccount understates the actual
  (still reasonable, no-wildcard) scope — minor drift, not a security issue.
- The 10-year self-signed CA (`cert-manager-issuer.yaml`) has a well-documented ACME swap path but
  no inline warning against copy-paste reuse into a real environment.

### 2.5 Testing & documentation integrity

**🟠 HIGH · both** — `docs/api/grpc.md` documents an API contract that doesn't exist in code
`docs/api/grpc.md:90` vs. `OrderGrpcService.cs`'s `GetOrdersByProject` Claims `INVALID_ARGUMENT` for
`project_id ≤ 0`. The real implementation has no validation at all — a zero/negative value just
returns an empty stream, and no test exercises the claimed error path. A fabricated contract: anyone
integrating against the docs gets silently wrong results instead of the documented error.

**🟡 MEDIUM · interview-signal** — Frontend's `package.json` still declares the wrong test stack
`devDependencies` lists the unused default Karma/Jasmine scaffold; Jest — what actually runs — is
not declared anywhere in the file, installed ad hoc into `/tmp/ng-test-deps`. Reads as an incomplete
Karma→Jest migration to anyone opening `package.json` first.

**🟡 MEDIUM · production-readiness** — The CI Jest workaround is unpinned and solves a problem CI
doesn't have `.github/workflows/ci.yml:78-81`
`npm install --prefix /tmp/ng-test-deps jest ... --legacy-peer-deps`, zero version pins, no
lockfile. The root-owned `node_modules` problem it works around is a local-Docker-build artifact; a
fresh GitHub Actions checkout via `actions/setup-node` + `npm ci` never has it. Porting the local
fix into CI verbatim means test behavior can silently shift with a new Jest/TS major release.

**🟡 MEDIUM · both** — Zero integration tests for the headline scenario the whole lab exists to
demonstrate The 5-hop cross-language trace propagation claim is validated only "manually via Jaeger
UI," per the testing doc's own gap table. Solid unit base → one shallow syntactic proto-diff check →
zero integration tests → a CI-disconnected manual load test. A Staff-level bar would expect at least
one Testcontainers-based test asserting the RabbitMQ message and SpanLink actually connect
end-to-end.

**🟡 MEDIUM · production-readiness** — Legacy Makefile AKV secret names have drifted from the
current fetch script — a second break in the same legacy path The canonical script reads one set of
AKV secret names; the legacy Make target reads entirely different ones. If those secrets were
renamed when the script-based flow was introduced, the legacy target doesn't just write a stale
endpoint (the Mimir-endpoint footgun in §2.4) — it hard-fails on lookup too.

**Also worth a look — testing & docs:**

- `docs/testing.md`'s gateway-api.Tests breakdown table sums to 19 against its own stated (correct)
  total of 22 — symptomatic of hand-edited tables rather than anything generated from the suite.
- The ADRs in `docs/architecture/decisions.md` were spot-checked against source (SpanLink, fail-fast
  secrets, `AsAsyncEnumerable`) and are the real thing — worth repeating here as a counterweight to
  the drift found elsewhere.
- The non-root UID table, the Kustomize "files stay in place" claim, and the
  `readOnlyRootFilesystem` rationale in `CLAUDE.md`/`hardening.md` all still check out exactly
  against current Dockerfiles and manifests.
- The k6 load test under `k8s/loadtest/` is honestly scoped and consistently referenced — correctly
  labeled "manual" today, with the CronJob upgrade correctly marked "planned," not oversold.

---

## 3. Verdict matrix

| Domain                        | Interview / portfolio signal                                  | Production sign-off                                                                                                                                                            | Where they diverge                                                                                      |
| ----------------------------- | ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------- |
| Backend services & gRPC       | Strong — outbox pattern, cursor streaming, layered validation | No — outbox relay's multi-replica race is unguarded, blanket exception→502 mapping still erases most gRPC status codes                                                         | Docs/comments describe a system slightly ahead of what actually shipped after the outbox refactor       |
| Messaging & frontend          | Strong — correct async span semantics, tested backoff logic   | No — DLQ still conflates transient infra failure with poison messages (no retry), frontend `API_BASE_URL` knob is dead end-to-end                                              | The design vocabulary is right; the dedup docs actively describe a mechanism the code doesn't implement |
| Observability pipeline        | Very strong, if only the local-mode path is inspected         | Conditional — correlation now ships in cloud mode, but a fabricated "Dual-Export" doc section and real employer/vault naming remain committed                                  | The sophisticated design mostly ships; the documentation and secret-naming hygiene haven't caught up    |
| K8s / Helm / infra & security | Strong — unusually honest self-disclosed gaps                 | Conditional — the Mimir-endpoint footgun is still live in the legacy Makefile path, NetworkPolicy allows any namespace, RabbitMQ runs as `guest`                               | Self-awareness about known gaps doesn't offset the remaining unguarded legacy path                      |
| Testing & docs integrity      | Tests: strong. Docs: weak once spot-checked                   | Conditional — `docs/api/grpc.md` still documents a validation contract the code doesn't implement, and there's still zero integration coverage for the trace-propagation claim | Real test engineering is undermined by unmaintained downstream docs                                     |

---

## 4. Closing assessment

**As a portfolio / interview artifact:** this is genuinely strong Staff-adjacent signal — the outbox
pattern, the SpanLink async semantics with an ADR that reasons through redelivery correctly, the
spanmetrics-before-sampling ordering, the multi-window burn-rate SLO math, and the pattern of docs
that honestly name their own gaps are not things most "learning lab" projects attempt, let alone
execute correctly. That's a real, defensible signal of judgment, and it would hold up well in a
system-design conversation about async trace propagation or reliable event delivery specifically.

The pattern that would cost points in the same conversation is consistent across every domain
reviewed: **verification discipline after a refactor.** The dedup mechanism changed but the docs and
ADR didn't catch up; the outbox refactor shipped but the trace-shape docs still describe the old
synchronous publish; the cloud-mode Helm chart replaced the hand-rolled Alloy config but
`docs/OTEL-PATTERNS.md` still describes a fictional "Dual-Export" feature; the legacy Makefile's
`secrets-fetch-akv` target still writes the wrong Mimir endpoint format long after the canonical
path moved on. None of these are hard to explain in an interview — but an interviewer who reads as
carefully as this review did will find at least one of them within minutes, and "I didn't re-check
the docs after the refactor" is a real deduction at the Staff bar, not a rounding error.

**As something to ship:** closer, but still no. The prior blockers that were unconditional stops —
live credential exposure, the retry/idempotency gap on order creation, and the prod overlay's
structural inability to avoid dev's placeholder secrets — are resolved. What's left is smaller in
scope but still real: the outbox relay's multi-replica race is unguarded, the notification DLQ still
can't distinguish a transient Redis blip from a poison message, `docs/api/grpc.md` documents
validation that doesn't exist, and the legacy Makefile path still ships a wrong Mimir endpoint next
to real employer/vault naming committed in plaintext. Every one of these is a small, well-scoped fix
— the same class of gap the first pass closed, just the next layer down.

**The throughline:** where the two lenses diverge, they diverge in the same direction every time:
the design vocabulary and the hard engineering decisions are ahead of the implementation and
documentation discipline that should track them. That gap is cheap to close and, done well, becomes
its own interview story — "here's how I audit a system for drift between what it claims to do and
what it actually does" is a stronger Staff narrative than pretending the drift never happened.

---

_Five parallel subsystem reviews (backend/gRPC, messaging/frontend, observability pipeline,
K8s/infra/security, testing/docs) consolidated and de-duplicated on 2026-07-08. Trimmed the same
day: the credential-exposure incident (§1 of the original) and 15 numbered fix-list items were
verified fixed directly against current source — not taken on the fix list's own say-so — and
removed, along with any "also worth a look" item that turned out to be a side effect of those fixes.
Two findings were reworded rather than dropped where the underlying fix only partially addressed
them (the gateway's 502 mapping, now correct for `GetOrder`'s `NotFound` case only; the
Mimir-endpoint footgun, now doc-guarded but still live in the Makefile). Lower-severity items were
compacted into "also worth a look" lists per subsection rather than omitted._
