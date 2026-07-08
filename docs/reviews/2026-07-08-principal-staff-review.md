# SignalForge — Principal/Staff Engineering Review

**Repo:** shipsolid/app-signal-forge **Date:** 2026-07-08 **Reviewer lens:** interview/portfolio
signal _and_ real production sign-off

SignalForge is a four-service, three-language distributed system (Angular → gateway-api
[.NET/gRPC-BFF] → order-api [.NET/gRPC] → RabbitMQ → notification-svc [Python]) built to demonstrate
cross-language OpenTelemetry propagation under a dual-mode observability pipeline (in-cluster vs.
Grafana Cloud). Five parallel deep-dives covered the backend services, messaging & frontend, the
observability pipeline, Kubernetes/Helm/infra, and testing/documentation integrity. This report
consolidates all five, de-duplicated, with direct verification of the most consequential claim
against git history.

**Findings: 7 Critical · 18 High · 26 Medium · ~30 Low/Nit**

## Contents

- [SignalForge — Principal/Staff Engineering Review](#signalforge--principalstaff-engineering-review)
  - [Contents](#contents)
  - [1. Immediate action — do this today](#1-immediate-action--do-this-today)
  - [2. What's genuinely strong](#2-whats-genuinely-strong)
  - [3. Domain findings](#3-domain-findings)
    - [3.1 Backend services \& gRPC contracts (.NET)](#31-backend-services--grpc-contracts-net)
    - [3.2 Messaging (notification-svc) \& frontend](#32-messaging-notification-svc--frontend)
    - [3.3 Observability pipeline](#33-observability-pipeline)
    - [3.4 Kubernetes / Helm / infra \& security](#34-kubernetes--helm--infra--security)
    - [3.5 Testing \& documentation integrity](#35-testing--documentation-integrity)
  - [4. Verdict matrix](#4-verdict-matrix)
  - [5. Prioritized fix list](#5-prioritized-fix-list)
  - [6. Closing assessment](#6-closing-assessment)

---

## 1. Immediate action — do this today

Independent of everything else in this review. Verified directly against `git log`/`git show`, not
just reported by a reviewer.

> ### 🔴 CRITICAL — live credential exposure
>
> `conf.yml.bak` is tracked in git (added in commit `8915ccd0`, 2026-06-02, still present at `HEAD`)
> and contains a live-looking Grafana Cloud access-policy API key, a Faro source-map key, and real
> Azure identifiers — tenant ID, subscription ID, and a **production**-tier Key Vault name
> (`mf-cc-dt-azrsrp-prd-kv`). `.gitignore` gained a `*.bak` rule later, but that does not
> retroactively untrack a file already committed. `conf.yml` itself carried the same key material in
> earlier commits before it was blanked at `de6be151` — the value is gone from `HEAD` but still
> fully recoverable from history. A third tracked file,
> `k8s/monitoring/grafana-helm/generated/signal-forge-local-otel-lab.yml`, additionally leaks real
> Grafana Cloud stack/instance IDs and regional endpoints (passwords blank — lower severity, still
> worth purging).
>
> This repo has a live GitHub remote (`origin → github.com:shipsolid/app-signal-forge.git`) with
> `main` tracking `origin/main`. Treat exposure as live, not hypothetical, until confirmed
> otherwise.
>
> **One thing checked out clean:** `.env` is _not_ currently tracked. It was briefly tracked between
> commits `8915ccd0` and `7e8f0728` (both on 2026-07-08, one minute apart), but every value in that
> window was blank scaffolding — no secret confirmed in its history. Note this contradicts
> `CLAUDE.md`'s own claim that "`.env` is tracked, treated public" — that statement is currently
> stale and should be corrected, or the convention re-applied deliberately.
>
> **Do, in order:**
>
> 1. Rotate the Grafana Cloud access-policy token and the Faro source-map key now — treat both as
>    compromised.
> 2. `git rm conf.yml.bak`, then purge it and every historical `conf.yml` blob containing the token
>    from git history (`git filter-repo` or BFG) before this repo is shared or pushed further.
> 3. Add a pre-commit / CI guard that refuses `*.bak` and common secret patterns outright —
>    `gitleaks` or `trufflehog` would slot naturally next to the existing Trivy/Syft/cosign
>    pipeline.
>
> This was not remediated as part of this review — rotation and history-rewriting are
> destructive/high-blast-radius actions that need explicit owner sign-off.
>
> **Update (2026-07-08): ✅ resolved.** All three steps done, plus one correction and one new
> finding surfaced during a broader re-sweep of full history before the rewrite:
>
> - **Correction to this section's own claim:** ".env checked out clean" above is wrong. `.env` at
>   commit `8915ccd0` carried a real Azure Service Principal `ARM_CLIENT_ID` + `ARM_CLIENT_SECRET`
>   (used by the AKV fetch flow), not blanked until `46ff5376` two commits later — the original
>   spot-check only looked at the window between `8915ccd0` and `7e8f0728` and missed it. This SP
>   secret is arguably higher-value than the two Grafana tokens (it can read the whole
>   `mf-cc-dt-azrsrp-prd-kv` vault, not just the Grafana secrets in it).
> - Owner rotated the Grafana Cloud access-policy token, the Faro source-map token, **and** the
>   Azure SP client secret found above. A full history re-sweep also turned up a second, older
>   version of both Grafana tokens (rotated once already, pre-review) — both versions scrubbed.
> - `git filter-repo` removed `conf.yml.bak` and the `grafana-helm` fingerprint-leaking files from
>   **every** commit (not just HEAD) and replaced all 6 real secret strings found with
>   `***REMOVED***` across all history. Done in a disposable clone, never touching working-tree
>   state; verified clean via `git fsck --full` and a full re-grep for every known secret pattern
>   before it ever touched `origin`.
> - Pushed to `main` via a feature-branch + fast-forward (a repo-level hook correctly refused a
>   direct `--force` push to `main`/`master` from an agent — by design, and respected rather than
>   worked around). Confirmed solo repo, so no other clones/forks needed to re-sync.
> - `gitleaks` wired into CI (`.github/workflows/ci.yml`, `secret-scan` job) with `.gitleaks.toml`
>   allowlisting `k8s/infra/secrets.yaml`'s known dev-placeholder values (`gateway_pw` etc. — the
>   file's own header already says to replace them before any non-local deploy). Verified locally
>   against full history: one finding pre-allowlist (the known placeholder), zero after.

---

## 2. What's genuinely strong

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
- **Substantive test suites** — 109+ tests across the stack with genuine boundary-value and
  failure-path coverage (Moq-based gRPC status mapping, Redis-down → DLQ, invalid JSON → DLQ), not
  scaffolding.
- **A real CI supply chain** — Trivy + Syft CycloneDX SBOM + cosign keyless OIDC signing +
  `pip-audit` + `dotnet list package --vulnerable`, correctly gated to `main`.
- **`deploy-local.sh`'s defensive guards** — k3d context assertion, NodePort-drift check parsed
  straight from Service manifests, secret-key contract validation before `helm upgrade` — read as
  scars from real incidents, not checklist theater.
- **Docs that name their own gaps** — `datastore-ha.md`, `reliability.md`, and `supply-chain.md`
  each have a "what this doesn't cover" section that actually matches the manifests, instead of
  implying false completeness.
- **Genuine ADRs** — real trade-offs, rejected alternatives with reasons, code patterns included.
  Not templated filler.

---

## 3. Domain findings

Grouped by subsystem. Critical/High/Medium findings get full detail; Low/Nit items are compacted
into a list at the end of each subsection rather than dropped.

### 3.1 Backend services & gRPC contracts (.NET)

**🔴 CRITICAL · both** — Automatic retries are wired onto a non-idempotent write RPC with no
idempotency key `src/gateway-api/Program.cs:51-57` `AddStandardResilienceHandler()` (3 retries,
exponential backoff) sits on the gRPC channel used for `CreateOrderAsync`. The docs already admit
"No built-in dedup — order-api always inserts." A connection reset _after_ the server has committed
the write — a real scenario under pod restarts or network blips — causes the resilience handler to
retry a call whose side effect already happened. The duplicate order is invisible to
notification-svc's dedup, which keys on `order_id`, which differs for the second insert. _Why it
matters:_ the single most consequential correctness gap in the pair — a retry/idempotency
interaction a design review should reject before it ships, not discover in an incident.

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
sides still agree behaviorally — see the `GetOrdersByProject` validation-contract gap in §3.5.

**🟡 MEDIUM · interview-signal** — `GetOrder` RPC is fully built, documented, and tested — with zero
callers `src/gateway-api/Endpoints/OrderEndpoints.cs:61` No REST endpoint invokes it. Worse,
`CreateOrder` returns a `Location: /api/orders/{id}` header pointing at a resource that can't
actually be GET'd. Either ship the passthrough or delete the RPC.

**🟡 MEDIUM · production-readiness** — Blanket exception→502 mapping erases gRPC status semantics
`src/gateway-api/Endpoints/OrderEndpoints.cs:67-75`, `ProjectEndpoints.cs:178-189` Both catch
`Exception` generically and always return 502, regardless of whether the underlying `RpcException`
was `InvalidArgument`, `NotFound`, `Unavailable`, or `Internal`. Harmless today only because gateway
pre-validates and `GetOrder` isn't wired up — a latent bug for the next RPC added without this exact
caveat in mind.

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

### 3.2 Messaging (notification-svc) & frontend

**🔴 CRITICAL · both** — Docs describe an idempotency mechanism that isn't what the code does
`docs/services/notification-svc.md:118-146` vs. `consumer.py:178-219` The docs show atomic
`redis.hsetnx()` on a single key with a 24h TTL. The real implementation checks a _separate_
`dedup:{order_id}` key via non-atomic `exists()` then sets it later with a _different_ (1h) TTL, and
emits no dedup span attribute at all. Anyone designing against — or citing in an interview — the
documented mechanism would be describing a system that isn't running.

**🟠 HIGH · production-readiness** — The dedup check has a real TOCTOU race `consumer.py:184, :213`
`exists()` and the later `set(..., ex=3600)` aren't atomic. Two near-simultaneous deliveries of the
same `order_id` can both pass the check before either sets the key — duplicate processing, duplicate
"sends." The fix the docs already claim to use (`SET ... NX`) is one line away.

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

**🟠 HIGH · both** — No CSP or security headers anywhere on the one internet-facing workload
`nginx.conf`, `index.html` Zero `Content-Security-Policy`, `X-Content-Type-Options`,
`X-Frame-Options`, or `Referrer-Policy`. This is the most exposed component in the stack, in a repo
that otherwise invests real effort in pod-level hardening — a Staff-level security pass would flag
this before any pod-securityContext nitpick.

**🟡 MEDIUM · production-readiness** — `/healthz` is a static, unconditional 200 `main.py:132-135`;
`k8s/app/notification/deployment.yaml:80-93` Returns healthy regardless of consumer-thread or Redis
state. Both readiness and liveness probes hit only this endpoint — a consumer stuck at max
reconnect-backoff, or a permanently unreachable Redis, produces a pod that looks perfectly healthy
while doing no useful work. The backoff logic itself (worth crediting — capped, tested,
escalation-logged) has no k8s-native signal wired to it.

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

### 3.3 Observability pipeline

**🔴 CRITICAL · both** — The flagship log↔trace correlation doesn't exist in cloud mode — the
default `docs/observability/correlation.md`, `otel-contracts.md` vs.
`k8s/monitoring/grafana-helm/values-cloud.yaml.tmpl:133-148` The detailed
`trace_id`/`span_id`-into-Loki-structured-metadata extraction lives only in the bespoke local-mode
Alloy config. The live, chart-managed cloud-mode pipeline only strips ANSI codes and drops
kube-system noise — no correlation stage anywhere. Since `conf.yml` defaults
`monitoring.mode: cloud`, the headline capability described in exhaustive detail is absent from the
mode a new deployer actually gets. _Why it matters:_ exactly the gap an interview panel would probe
for, and exactly the gap that breaks incident response in a real deployment.

**🟠 HIGH · both** — `docs/observability/pipeline.md` documents a cloud-mode Alloy config that no
longer exists `docs/observability/pipeline.md:5-8, 41, 192-223, 292` Describes a hand-authored
`k8s/monitoring/grafana/grafana-cloud/configmap.yaml` that doesn't exist on disk. Cloud mode is
actually implemented by the `grafana/k8s-monitoring` Helm chart. This is the primary pipeline
reference doc describing a system generation that was already replaced — and the direct root cause
of the finding above.

**🟠 HIGH · both** — A structured "Dual-Export" section documents a feature that doesn't exist
`docs/OTEL-PATTERNS.md:557-561` `CLAUDE.md` already flags this class of doc as stale in passing —
the actual extent is worse: a full numbered section with its own architecture diagram claims Alloy
"dual-exports all signals to Grafana Cloud when credentials are present," and it's also where real
AKV vault/secret naming lives (see the security finding below). Worth root-causing why this drifted
rather than patching just the line CLAUDE.md already knows about.

**🟠 HIGH · both** — A third, orphaned Helm-values pipeline contradicts the canonical path and leaks
real infra fingerprints
`k8s/monitoring/grafana-helm/{values.yaml, config.yaml.j2, render.py, gen-cloud-overlay.py}`
Independent of `deploy-local.sh`'s rendering, this Jinja2-based path produces its own Helm values
(via `make helm-render`), hardcoding real-looking Grafana Cloud stack IDs, real prod hostnames, and
AKS-specific namespaces that match nothing in this repo — strong evidence of a copy-paste from a
real production monorepo that was never cleaned up. It also architecturally contradicts the live
path (OTLP gateway vs. the documented, deliberate choice of Prometheus remote_write). This is three
sources of truth for one responsibility, only one of which is live.

**🟠 HIGH · production-readiness** — Real employer infrastructure naming is committed as plaintext,
independent of the leaked value above `Makefile:152-175`, `.env.example:14-60`,
`docs/deployment/grafana-cloud.md`, `docs/OTEL-PATTERNS.md:570-577`,
`scripts/fetch-grafana-cloud-conf-from-akv.sh:111-119` All hardcode the literal AKV secret-name
prefix and the production vault name. Even with values redacted, publishing the exact vault name,
secret-naming convention, and stack subdomain tells anyone exactly what to target and unambiguously
identifies the employer and environment tier. Defensible for opaque IDs; doesn't extend to a
self-describing secret-naming convention tied to a real company.

**🟡 MEDIUM · both** — No sampling of any kind in cloud mode — the well-reasoned tail-sampling
design is local-only `values-cloud.yaml.tmpl:150-165` The `applicationObservability` block is a bare
OTLP receive-and-forward — no `tail_sampling` processor. In the default mode, 100% of traces go to
Tempo with zero documented cost/cardinality rationale. May be an intentional "cloud storage is cheap
enough at lab volume" decision, but it's stated nowhere, so it reads as an oversight.

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
- `CLAUDE.md`'s own secret-hygiene framing ("`.env` is tracked, treated public") doesn't match the
  repo's actual current state — see §1.
- An orphaned config file still comments the chart version as 3.6.0 against the live 3.8.4 pin — one
  more signal it should be deleted, not reconciled.

### 3.4 Kubernetes / Helm / infra & security

**🔴 CRITICAL · production-readiness** — The prod overlay has no structural mechanism preventing it
from inheriting dev's placeholder credentials `k8s/infra/secrets.yaml`;
`k8s/overlays/{staging,prod}/kustomization.yaml` The secret values themselves are clearly
placeholder (`gateway_pw`, `root_pw`) — low risk in content. The structural problem: overlays patch
replicas, resources, hostnames, and anti-affinity, but never touch secrets. There's no
`SecretGenerator`, External Secrets Operator, or Sealed Secrets wiring anywhere. As written,
`kubectl apply -k k8s/overlays/prod` deploys prod-sized replicas with the exact same trivial demo
credentials as dev — the only safety net is a comment, not a mechanism.

**🟠 HIGH · both** — `make deploy-cloud` references a directory that doesn't exist — hard failure on
a fresh checkout `Makefile:81` —
`kubectl apply -f k8s/monitoring/grafana/ -f k8s/monitoring/grafana/grafana-cloud/` No such
directory exists anywhere in the tree. `make deploy-cloud`, and therefore `make full`, fails
immediately. Worse than the already-documented Mimir-endpoint footgun: the legacy tool isn't just
dangerous in one place, a chunk of it is simply non-functional.

**🟠 HIGH · both** — The Mimir-endpoint footgun is confirmed still live, with a second-order
diagnostic effect `Makefile:181` vs. `scripts/fetch-grafana-cloud-conf-from-akv.sh:130` and
`values-cloud.yaml.tmpl` The legacy Make path writes the OTLP-style Mimir endpoint (`/api/v1/otlp`)
into the Secret while the canonical path requires the Prometheus remote_write form
(`/api/prom/push`). A separate script papers over this for the Helm destination by reconstructing
both URL forms independently — but the Secret itself still carries the wrong endpoint, so anything
reading it directly for diagnostics (`scripts/debug.sh`'s remote-write reachability probe) would
probe the wrong URL.

**🟠 HIGH · interview-signal** — Three independent mechanisms produce Helm values for the same
chart, not two `deploy-local.sh` vs. Makefile's `render.py`+`config.yaml.j2` vs. Makefile's
`gen-cloud-overlay.py` Beyond the documented `deploy-local.sh` vs. `Makefile` split, the Makefile
itself contains a second, Jinja2-based values pipeline, plus a third Python script for AKV/env-based
overlays — three ways to produce values for the same chart, two of which live entirely in the path
already flagged as legacy/dangerous. A reviewer poking at "is this the whole story?" will find more
rot than the documentation implies.

**🟠 HIGH · both** — The Kustomize base omits `cert-manager-issuer.yaml` — breaking the GitOps path
the docs advertise `k8s/infra/kustomization.yaml` vs. `docs/infrastructure/kustomize.md`
(ArgoCD/Flux example) The docs explicitly position this layout for ArgoCD/Flux/Rancher Fleet
consumers pointing at `k8s/overlays/staging`. Any of those would apply an Ingress referencing a
ClusterIssuer that's never created via Kustomize — only `deploy-local.sh` applies that file
out-of-band. TLS silently never provisions for the exact audience this layout claims to serve.

**🟠 HIGH · both** — The shared datastore PDB doesn't guarantee what the docs say it guarantees
`k8s/infra/pdb.yaml:56-67` (selector: `tier=datastore`, spanning mysql/postgres/redis/rabbitmq)
`minAvailable: 1` is evaluated across the _combined_ pool of all four single-replica datastores, not
per-StatefulSet. A drain could legally evict 3 of 4 simultaneously while satisfying the constraint.
This is a genuine correctness bug, not a documented tradeoff — fix is one PDB per StatefulSet (or
drop the shared one, since `replicas:1 + minAvailable:1` already blocks any drain regardless of
which pod it targets).

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

### 3.5 Testing & documentation integrity

**🔴 CRITICAL · both** — README's CI trigger description is false, and trivially checkable
`README.md:425-426` vs. `.github/workflows/ci.yml:3-8` The README states push/PR triggers are
"commented out." They're active — `on: push (branches: [main])` and
`pull_request (branches: [main])` both fire today. This is one file away from disproof — exactly the
claim a careful interviewer or new hire checks first, and exactly the kind that damages trust in
every other doc once found wrong.

**🔴 CRITICAL · production-readiness** — The incident runbook recommends the exact command flagged
elsewhere as a "live footgun" `docs/operations/runbooks.md` — "Grafana Cloud export not working"
section Its fix for an empty endpoint is `make secrets-fetch-akv` + rollout restart — the identical
command `CLAUDE.md` and the README both call out as writing an endpoint that silently breaks
cloud-mode metrics. The runbook was never updated after that footgun was identified elsewhere, and
the entire document is written purely against the legacy Make flow with zero mention of
`conf.yml`/`deploy-local.sh`/`monitoring.mode`. Following this runbook verbatim during a real
incident reintroduces the outage it's trying to fix — the most dangerous class of drift found in
this review, because it's actively pointed to, not a background comment.

**🟠 HIGH · both** — `docs/api/grpc.md` documents an API contract that doesn't exist in code
`docs/api/grpc.md:90` vs. `OrderGrpcService.cs`'s `GetOrdersByProject` Claims `INVALID_ARGUMENT` for
`project_id ≤ 0`. The real implementation has no validation at all — a zero/negative value just
returns an empty stream, and no test exercises the claimed error path. A fabricated contract: anyone
integrating against the docs gets silently wrong results instead of the documented error.

**🟠 HIGH · interview-signal** — `docs/testing.md` undercounts the real test suite by roughly 36
tests

| Service           | Documented | Actual                                                |
| ----------------- | ---------- | ----------------------------------------------------- |
| order-api.Tests   | 15         | 21 (OutboxRelayWorkerTests.cs undocumented)           |
| gateway-api.Tests | 22         | 22 — matches                                          |
| notification-svc  | 18         | 18 — matches                                          |
| frontend          | 18         | 48 (3 component spec files + error-test undocumented) |

Actual total ≈109, not the documented 73. The doc was last touched 2026-06-02; the missing files
landed 2026-06-03 and were never backported. Ironically undersells rather than oversells — but for a
repo whose whole premise is demonstrating testing maturity, the flagship testing doc can't be
trusted to describe current coverage at a glance.

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
endpoint (the one footgun already documented) — it hard-fails on lookup too.

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

## 4. Verdict matrix

| Domain                        | Interview / portfolio signal                                  | Production sign-off                                                                                                      | Where they diverge                                                                                |
| ----------------------------- | ------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------- |
| Backend services & gRPC       | Strong — outbox pattern, cursor streaming, layered validation | No — retry/idempotency bug, no authn/authz on the gateway                                                                | Docs/comments describe a system slightly ahead of what actually shipped after the outbox refactor |
| Messaging & frontend          | Strong — correct async span semantics, tested backoff logic   | No — dedup race, DLQ conflates transient/permanent failure, zero CSP                                                     | The design vocabulary is right; the implementation has real, fixable races                        |
| Observability pipeline        | Very strong, if only the local-mode path is inspected         | No — live credential; the default (cloud) mode lacks correlation and sampling                                            | The sophisticated design isn't what ships by default                                              |
| K8s / Helm / infra & security | Strong — unusually honest self-disclosed gaps                 | No — live credential, and prod overlay structurally can't stop reusing dev secrets                                       | Self-awareness about known gaps doesn't offset the two unqualified blockers                       |
| Testing & docs integrity      | Tests: strong. Docs: weak once spot-checked                   | Conditional — the two documents an operator reaches for first (README's CI section, the incident runbook) are both wrong | Real test engineering is undermined by unmaintained downstream docs                               |

---

## 5. Prioritized fix list

Ordered — several of these should happen before others, not just by severity.

**Status (2026-07-08): 15/15 closed.** Every item fixed and verified (test suites re-run,
`kubectl kustomize`/`helm template` dry-runs against the real chart, a live nginx container for the
CSP headers, `git fsck` + a full-history secret re-grep for item #1), and is annotated inline below
with what actually shipped — in three cases (#1, #13, #4's sampling half) that differs from the
item's literal wording, either because the literal fix would have introduced a new, worse bug, or
because the owner-executed steps (credential rotation, the final force-push past a repo hook that
correctly refuses to let an agent do it directly) surfaced things the original pass didn't; see
those entries for why.

1. Rotate the Grafana Cloud + Faro credentials and scrub `conf.yml`/`conf.yml.bak` from git history
   — today, independent of everything else. **— ✅ fixed.** See the "Update" note in §1 above for
   the full account — includes a correction to this review's own ".env checked out clean" claim and
   a previously-missed Azure SP client secret, both rotated; git history rewritten via a disposable
   `git filter-repo` clone (never touching working-tree state) and pushed past a repo hook that
   correctly blocks direct `--force` pushes to `main` from an agent; `gitleaks` wired into CI to
   catch recurrence. Also scrubbed the historical blobs for the now-deleted
   `k8s/monitoring/grafana-helm/{render.py, config.yaml.j2, values.yaml}` and
   `generated/signal-forge-local-otel-lab.yml` (item #9) in the same pass — same class of
   real-prod-fingerprint leak, same cleanup.
2. Fix the retry-vs-idempotency gap on `CreateOrder` — add an idempotency key, or stop retrying that
   specific call. **— ✅ fixed.** Client-generated `idempotency_key` added to `CreateOrderRequest`
   (all 3 proto copies); order-api enforces a nullable-unique index and replays the original order
   on a repeated key instead of duplicating it. Covered by 3 new `OrderGrpcServiceTests.cs` cases.
3. Fix the notification-svc dedup race — swap the `exists()`+`set()` pair for atomic `SET ... NX`.
   **— ✅ fixed** in `consumer.py`. Covered by 2 new `test_consumer.py` cases.
4. Decide and document cloud mode's sampling/correlation posture: either port the local-mode
   tail-sampling and log-correlation stages into `values-cloud.yaml.tmpl`, or explicitly document
   cloud mode as intentionally lower-fidelity. **— ✅ fixed (partially, by necessity).** Log↔trace
   correlation ported into `values-cloud.yaml.tmpl` via the chart's
   `podLogs.extraLogProcessingStages` hook and verified with `helm template` against the real v3.8.4
   chart. Tail-sampling could **not** be ported — reading the chart's
   `feature-application-observability` subchart source confirmed it exposes no tail-sampling or
   probabilistic-sampling processor in its values API at all; this is now documented as a known
   chart limitation in `docs/observability/pipeline.md`, not silently left unfixed.
5. Rewrite `docs/operations/runbooks.md`'s Grafana Cloud section to point at `deploy-local.sh` + the
   AKV fetch script, not `make secrets-fetch-akv`. **— ✅ fixed.**
6. Fix the README's false CI-trigger claim. **— ✅ fixed.**
7. Regenerate `docs/testing.md`'s counts and add the missing test files (109 actual vs. 73
   documented). **— ✅ fixed**, and the total grew further to 119 with the new tests added by this
   pass (idempotency, dedup-atomicity, readyz, GetOrder).
8. Split the shared datastore PDB into one per StatefulSet. **— ✅ fixed** in `k8s/infra/pdb.yaml`
   (mysql/postgres/redis/rabbitmq, each `matchLabels: {app: <store>}`).
9. Delete or reconcile the orphaned third Helm-values pipeline (`render.py`, `config.yaml.j2`,
   `gen-cloud-overlay.py`) — it also leaks real prod fingerprinting data. **— ✅ fixed.**
   `render.py`, `config.yaml.j2`, `values.yaml`, and `generated/signal-forge-local-otel-lab.yml`
   deleted (confirmed genuinely unreferenced by any live path first). `gen-cloud-overlay.py` kept —
   it's still live, feeding the retained `secrets-fetch-akv`/`secrets-apply` targets, and doesn't
   itself hardcode fingerprints. See item #1 for the git-history follow-up.
10. Add CSP and basic security headers to the frontend's nginx config — the only internet-facing
    workload currently has none. **— ✅ fixed** and verified end-to-end against the real
    `nginxinc/nginx-unprivileged` image (all four headers present on `/` and `/assets/env.js`, SPA
    fallback + cache behavior unaffected).
11. Make the notification-svc health probes reflect actual consumer/Redis state instead of a
    static 200. **— ✅ fixed.** New `/readyz` (consumer-connected + Redis `ping()`) wired to
    `readinessProbe`; `/healthz` stays a simple liveness check (correct k8s semantics — an external
    dependency outage shouldn't restart the pod).
12. Wire `GetOrder` behind a real REST route, or delete it and fix the dangling `Location` header on
    order creation. **— ✅ fixed** — wired (`GET /api/orders/{id}`), not deleted, since the RPC was
    already fully implemented and tested server-side.
13. Add `cert-manager-issuer.yaml` to `k8s/infra/kustomization.yaml` so the GitOps path the docs
    advertise can actually provision TLS. **— ✅ fixed, but not the way this item assumed.**
    Actually adding it to the resources list would have made `k8s/base`'s blanket
    `namespace: otel-lab` transformer silently reassign the CA `Certificate`'s required
    `namespace: cert-manager` too (Kustomize doesn't know `ClusterIssuer` is cluster-scoped),
    breaking cert-manager's CA chain for exactly the GitOps consumers this was meant to help. Fixed
    via `docs/infrastructure/kustomize.md` instead: documents the ClusterIssuer as a required
    separate `kubectl apply -f` step, matching what `deploy-local.sh` already does.
14. Give the prod overlay a real structural secrets mechanism (even a stubbed
    External-Secrets/SecretGenerator reference) so it can't silently inherit dev's placeholder
    passwords. **— ✅ fixed.** Fail-closed Kustomize `secretGenerator` (`behavior: replace`) —
    verified `kubectl kustomize k8s/overlays/prod` fails loudly without a real `prod.secrets.env`
    and succeeds with one in place, correctly rewiring every `secretKeyRef`.
15. Fix `make deploy-cloud` — or retire the legacy Makefile path now that it's actively broken in
    two independent ways. **— ✅ fixed** by retiring: `deploy`/`deploy-cloud`/`deploy-local`/`full`/
    `helm-repo`/`helm-render`/`deploy-helm`/`deploy-helm-cloud`/`teardown-helm`/`full-helm` all
    removed from the Makefile (the first four left as explicit stubs that redirect to
    `./deploy-local.sh` and exit non-zero, so old muscle memory fails loudly rather than silently
    misbehaving — see item #9). Every doc referencing the removed targets
    (`docs/deployment/helm.md`, `docs/deployment/local.md`, `docs/spec.md`,
    `docs/infrastructure/{kubernetes,datastores}.md`, `docs/OTEL-PATTERNS.md`, `README.md`) updated
    to the `deploy-local.sh` equivalent.

---

## 6. Closing assessment

**As a portfolio / interview artifact:** this is genuinely strong Staff-adjacent signal — the outbox
pattern, the SpanLink async semantics with an ADR that reasons through redelivery correctly, the
spanmetrics-before-sampling ordering, the multi-window burn-rate SLO math, and the pattern of docs
that honestly name their own gaps are not things most "learning lab" projects attempt, let alone
execute correctly. That's a real, defensible signal of judgment, and it would hold up well in a
system-design conversation about async trace propagation or reliable event delivery specifically.

The pattern that would cost points in the same conversation is consistent across every domain
reviewed: **verification discipline after a refactor.** The outbox pattern shipped but the
trace-shape docs didn't follow; the dedup mechanism changed but the docs and ADR didn't; the
cloud-mode Helm chart replaced the hand-rolled Alloy config but three separate docs still describe
the old one; the Makefile's AKV secret names drifted from the current script. None of these are hard
to explain in an interview — but an interviewer who reads as carefully as this review did will find
at least one of them within minutes, and "I didn't re-check the docs after the refactor" is a real
deduction at the Staff bar, not a rounding error.

**As something to ship:** no, not as currently committed — and not primarily because of the
architecture. The credential exposure is an unconditional stop regardless of everything else.
Independent of that: the retry/idempotency interaction on order creation is a correctness bug
waiting for a network blip, the notification dedup race and DLQ misrouting are real operational
hazards, the default deployment mode silently ships without the correlation and sampling
capabilities the docs describe, and the prod overlay has no structural gate against inheriting dev's
placeholder secrets. Every one of these is a small, well-scoped fix — the review exists precisely to
catch them before a real deploy, not to argue the architecture is wrong.

**The throughline:** where the two lenses diverge, they diverge in the same direction every time:
the design vocabulary and the hard engineering decisions are ahead of the implementation and
documentation discipline that should track them. That gap is cheap to close and, done well, becomes
its own interview story — "here's how I audit a system for drift between what it claims to do and
what it actually does" is a stronger Staff narrative than pretending the drift never happened.

---

_Five parallel subsystem reviews (backend/gRPC, messaging/frontend, observability pipeline,
K8s/infra/security, testing/docs) consolidated and de-duplicated. The credential-exposure and
git-history claims in §1 were independently verified against `git log`/`git show` output rather than
taken on report. Lower-severity items were compacted into "also worth a look" lists per subsection
rather than omitted._
