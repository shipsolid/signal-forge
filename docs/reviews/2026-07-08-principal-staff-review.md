# SignalForge — Principal/Staff Engineering Review

**Repo:** shipsolid/app-signal-forge **Date:** 2026-07-08 (findings) / trimmed 2026-07-08 /
Critical+High closed 2026-07-08 / all Medium+Low/Nit closed 2026-07-09 **Reviewer lens:**
interview/portfolio signal _and_ real production sign-off

SignalForge is a four-service, three-language distributed system (Angular → gateway-api
[.NET/gRPC-BFF] → order-api [.NET/gRPC] → RabbitMQ → notification-svc [Python]) built to demonstrate
cross-language OpenTelemetry propagation under a dual-mode observability pipeline (in-cluster vs.
Grafana Cloud). Five parallel deep-dives covered the backend services, messaging & frontend, the
observability pipeline, Kubernetes/Helm/infra, and testing/documentation integrity.

> This is the original five-domain review with every closed item removed (verified against current
> source, not just against fix claims). The credential-exposure incident, the 1 Critical/9 High
> findings, and — as of this pass — every Medium and actionable Low/Nit finding have been found,
> fixed, and verified closed; that record isn't reproduced in full here beyond the notes at the
> bottom. What remains below is the small residue that's out of scope for a code fix (owner
> sign-off items) plus this pass's own honest disclosures about environment limitations discovered
> along the way.

**Open findings: 0 Critical · 0 High · 0 Medium · 0 Low/Nit** (2 items require repo-owner action
outside a code fix — see §4)

## Contents

- [SignalForge — Principal/Staff Engineering Review](#signalforge--principalstaff-engineering-review)
  - [Contents](#contents)
  - [1. What's genuinely strong](#1-whats-genuinely-strong)
  - [2. Domain findings](#2-domain-findings)
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
  through the NACK/redelivery case most teams miss (ADR-002). As of this pass, the same reasoning
  now also links the producer side (`outbox.relay`/`order.publish`) back to the original request
  trace — previously an open gap, now closed and verified by a real cross-language integration test
  (see §4).
- **spanmetrics before tail-sampling** (ADR-003) — RED metrics are computed on 100% of traffic
  before the 25% sample is taken, so dashboards don't silently read 4x low. This ordering detail is
  one most engineers get backwards on the first pass.
- **Real multi-window, multi-burn-rate SLO alerts** — correct 14.4×/6× burn-rate math with a
  `clamp_min` divide-by-zero guard, not naive threshold alerts wearing an "SLO" label.
- **Fail-fast on missing secrets** (ADR-006) and **`secretKeyRef`-everywhere** (ADR-007) — no silent
  defaults, no plaintext credentials in manifests.
- **Dead-letter queue offloaded to RabbitMQ's native mechanism** (ADR-008) instead of a hand-rolled
  Redis retry counter — the simpler, more correct choice.
- **Substantive test suites** — 140 tests across the stack with genuine boundary-value and
  failure-path coverage (Moq-based gRPC status mapping, Redis-down → DLQ, invalid JSON → DLQ), not
  scaffolding. Includes real concurrency and cross-language coverage: `OutboxRelayWorkerTests` (via
  Testcontainers against an actual PostgreSQL) proving two replicas racing for the same outbox row
  only publish it once, and a new opt-in `src/integration-tests` project proving the full 5-hop
  trace end-to-end against real order-api/notification-svc containers, a real RabbitMQ, and a real
  Jaeger.
- **A real CI supply chain** — Trivy + Syft CycloneDX SBOM + cosign keyless OIDC signing +
  `pip-audit` + `dotnet list package --vulnerable` + `gitleaks`, correctly gated to `main`, now with
  a scheduled weekly re-scan and a `cosign verify` gate before cloud-mode manifests apply.
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

Every finding from the original five-domain review — 18 Medium, ~30 Low/Nit — has been fixed and
verified against current source as of this pass. Nothing is reproduced in full here; see §4 for
what changed and why, organized by the same five subsystems the original review used.

---

## 3. Verdict matrix

| Domain                        | Interview / portfolio signal                                                                                          | Production sign-off                                                                                                                          | Where they diverge                                                                             |
| ------------------------------ | ----------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| Backend services & gRPC       | Strong — outbox pattern, cursor streaming, layered validation, multi-replica-safe relay, now a verified 5-hop trace    | Clear — gRPC status codes map to real HTTP statuses, `AllowedHosts` is a real narrow allow-list, proto is single-sourced, gRPC itself now genuinely works end-to-end (see §4) | None material remaining; the gap between design intent and verified behavior is closed          |
| Messaging & frontend          | Strong — correct async span semantics, tested backoff logic, DLQ distinguishes transient from permanent failures       | Clear — TTL/dedup aligned, Angular has a real resilience interceptor, JS deps are scanned in CI                                                | None material remaining                                                                          |
| Observability pipeline        | Very strong — local and cloud-mode docs both match what ships                                                          | Clear — cardinality discipline applied to metrics, memory_limiter + retry/queue configured, log→trace click-through works, SLO alerts evaluate automatically in local mode with a documented manual push for cloud | None material remaining                                                                          |
| K8s / Helm / infra & security | Strong — unusually honest self-disclosed gaps, all subsequently closed                                                 | Clear — NetworkPolicy scoped to the real ingress controller namespace, RabbitMQ runs a purpose-named account, supply-chain claims match enforcement, HPA/PDB scale with the prod overlay | None material remaining                                                                          |
| Testing & docs integrity      | Very strong — 140 tests including a real Testcontainers concurrency test and a real cross-language trace-propagation test | Clear — the one prior integration-coverage gap (5-hop trace) is closed with a real, passing test; legacy Makefile drift fixed                | None material remaining                                                                          |

---

## 4. Closing assessment

**As a portfolio / interview artifact:** this is genuinely strong Staff-adjacent signal — the outbox
pattern, the SpanLink async semantics with an ADR that reasons through redelivery correctly, the
spanmetrics-before-sampling ordering, the multi-window burn-rate SLO math, and the pattern of docs
that honestly name their own gaps are not things most "learning lab" projects attempt, let alone
execute correctly. That's a real, defensible signal of judgment, and it would hold up well in a
system-design conversation about async trace propagation or reliable event delivery specifically.

**As something to ship:** this pass closed every remaining Medium and actionable Low/Nit finding
from the original review — 18 Medium, ~30 Low/Nit — verified against current source, not just
claimed fixed. Highlights, grouped by the original review's five subsystems:

- **Backend & gRPC (§2.1 original):** `plant.id` now genuinely forwards through gRPC metadata;
  `AllowedHosts` is a real narrow allow-list (see the h2c/AllowedHosts discovery below); the proto
  contract is single-sourced at `src/proto/orders.proto` with both services building from it, ending
  the three-copy drift risk entirely rather than adding a check on top of it; gateway-api's
  streaming proxy now caps in-memory accumulation like `GetNotifications` already did; both DB
  providers now retry transient connection failures. Money-as-`double`, the validation-constant
  duplication, the dead `Npgsql.OpenTelemetry` reference, `OrderPublisher`'s zero test coverage, the
  uncapped span-tag headers, and the dead `InflightMiddleware` class were all also closed.
- **Messaging & frontend (§2.2 original):** the dedup/notification TTL mismatch is resolved (aligned
  to 24h, plus an `LREM`-before-`LPUSH` idempotency guard as defense-in-depth); the frontend's
  `readOnlyRootFilesystem` exception is gone — `env.js` is now a ConfigMap volume mounted via
  `subPath`, the standard idiom the original finding named; the Angular HTTP client has a real
  resilience interceptor (retry/backoff/timeout, normalized user-facing errors); CI now runs
  `npm audit` against the real lockfile, closing the JS dependency-scanning gap. The unused pika
  instrumentation dependency, the `float`-as-money comment, the hardcoded `guest` RabbitMQ username,
  and Faro's PII scrubbing were also closed — Faro now redacts email patterns from string fields
  instead of a bare `/healthz` substring check.
- **Observability pipeline (§2.3 original):** `project_id` is no longer a metric dimension —
  removed from all three instruments, kept only as the existing span attribute, consistent with
  this project's own cardinality-discipline principles; the Alloy pipeline now has a
  `memory_limiter` processor plus retry/queue configuration on all three local exporters; Loki's
  `derivedFields` now matches `trace_id` as structured metadata instead of a body regex that could
  never fire, so log→trace click-through works in both directions. The unused
  `GRAFANA_CLOUD_*` env vars on the local-mode DaemonSet were removed; the "orphaned 3.6.0 config
  file" flagged in the original review no longer exists anywhere in the repo — re-confirmed at
  closeout, a non-finding rather than a skipped one.
- **K8s / Helm / infra & security (§2.4 original):** `docs/operations/security.md`'s `db-secrets`
  diagram now correctly shows it as the static, hand-rotated file it actually is; the
  `allow-ingress-from-controller` NetworkPolicy is scoped to `kube-system` (where Traefik actually
  runs here), with a documented Kustomize-patch path for environments where the controller lives
  elsewhere; RabbitMQ's application identity is now a purpose-named `signalforge` account sourced
  from the same Secret pattern as the password, not the reserved `guest` account; the supply-chain
  gaps are closed with a scheduled weekly Trivy re-scan (without `ignore-unfixed`) and a `cosign
  verify` gate before cloud-mode manifests apply. HPA now exists on gateway-api/order-api in the
  prod overlay, prod PDBs use percentage-based `minAvailable` that scales automatically with the
  overlay's replica patch, the ingress hostless-catch-all has a strengthened warning against
  copy-paste reuse, `security.md`'s RBAC excerpt matches the actual scope, and the 10-year
  self-signed CA has an inline warning against reuse in a real environment.
- **Testing & docs integrity (§2.5 original):** the headline gap — zero integration coverage for the
  5-hop cross-language trace propagation claim — is closed with a real, passing
  `src/integration-tests` project (Testcontainers: real Postgres, RabbitMQ, Redis, Jaeger, and both
  order-api and notification-svc built from their actual Dockerfiles), asserting `order.create`,
  `outbox.relay`, `order.publish`, and `notification.process` all land in one Jaeger trace. The
  legacy Makefile's drifted AKV secret names are fixed to match the canonical fetch script.
  `docs/testing.md`'s test-count tables now reflect the real, currently-measured counts (140 total,
  up from the 127 the original review found already-inconsistent) rather than hand-edited estimates.

**Two discoveries made building that integration test, outside the original review's scope
entirely** — the kind of gap that "validated only manually" was hiding, which is exactly why the
original review flagged the missing coverage as a Medium in the first place:

1. **order-api's gRPC endpoint may never have actually completed a call in this environment.** A
   single Kestrel endpoint configured for mixed HTTP/1.1+HTTP/2 without TLS silently downgrades
   every connection to HTTP/1.1 (Kestrel logs "HTTP/2 requires TLS application protocol
   negotiation"); gRPC's HTTP/2 prior-knowledge preface then gets rejected with an
   `HTTP_1_1_REQUIRED` error. This was confirmed with a from-scratch repro (no Docker, no k8s — a
   bare `dotnet run` and a hand-written gRPC client) before touching any production code, to rule out
   an environment artifact. Fixed by splitting order-api onto two dedicated Kestrel endpoints: 5001
   stays HTTP/1.1-only for kubelet's `/healthz`, a new 5002 is HTTP/2-only for gRPC — the standard
   fix for gRPC+REST coexisting on cleartext Kestrel without TLS. gateway-api's `OrderApi:Address`
   now points at 5002; NetworkPolicy, Dockerfile `EXPOSE`, and docs were all updated to match.
2. **`OutboxRelayWorker`'s explicit transaction was incompatible with `EnableRetryOnFailure()`** —
   the EF Core retry fix from this same review pass (§2.1 above). A bare
   `Database.BeginTransactionAsync()` under a registered retrying execution strategy throws
   `InvalidOperationException` on every call, meaning every outbox poll cycle failed silently (caught
   by the worker's own retry-and-log catch block) from the moment `EnableRetryOnFailure()` was added
   earlier in this same pass until this was caught. Fixed by wrapping the transaction in
   `Database.CreateExecutionStrategy().ExecuteAsync(...)`; a new regression test
   (`DrainOutboxAsync_OutboxRelaySpan_SharesTraceIdWithOriginalRequest`, using a real Testcontainers
   Postgres) exercises the exact code path and would have caught this immediately.

Both were found and fixed the same day as the review-remediation pass that introduced the second
one — caught by writing the integration test the first review asked for, which is itself the
strongest argument for why that finding mattered.

**An honest environment-limitation note, not a code finding:** this session's local k3d cluster
(WSL2 + Docker Desktop) exhibits a reproducible pod-to-pod networking issue — newly-scheduled pods
on this specific cluster sometimes cannot reach each other directly (`Connection refused`), while
the same pods remain reachable via `kubectl port-forward` and kubelet's own liveness/readiness
probes succeed normally. This was verified to be unrelated to any change in this pass: completely
unrelated pod pairs (`prometheus` → `grafana`) exhibit the identical symptom. Because of this, the
real order-creation flow could not be click-through-verified against *this* local k3d cluster on
this pass. It was instead verified two other ways: (a) the new `src/integration-tests` project,
which uses plain Docker networking rather than k3d/flannel and passes end-to-end, and (b) direct,
from-scratch repros (bare `dotnet run`, no Docker/k8s) for both of the two bugs above. Recreating
the k3d cluster is the known workaround for this specific symptom when it recurs; it isn't a defect
in any manifest or code path this review touched.

**The throughline:** where the two lenses diverged across this review's five passes, they diverged
in the same direction every time — design vocabulary and hard engineering decisions ahead of
implementation and documentation discipline. That gap is now closed and verified, not just claimed
closed, including the two bugs that surfaced only once the review's own "write a real integration
test" recommendation was actually followed.

---

_Five parallel subsystem reviews (backend/gRPC, messaging/frontend, observability pipeline,
K8s/infra/security, testing/docs) consolidated and de-duplicated on 2026-07-08. Trimmed twice the
same day: first pass closed the credential-exposure incident and 15 numbered fix-list items; second
pass closed the resulting 1 Critical and 9 High findings (Testcontainers-based outbox-relay
concurrency test, Angular runtime-config fallback tests, doc changes cross-checked against source).
Third pass, same day: root-caused and fixed the frontend Jest suite's `NG0202` failure (the
`/tmp/ng-test-deps` split-install pattern cross-resolving incompatible TypeScript/Angular-compiler
versions) by making Jest a real pinned `devDependency`, removing the dead Karma/Jasmine scaffold.
Fourth pass, same day: closed the highest-compounding Medium — SLO burn-rate alerts with correct
math but no evaluation path in either mode — by de-wrapping the rule file to a bare Prometheus/Mimir
format consumable by both local mode's automatic `rule_files:` load (verified against a live k3d
deploy — all 4 groups, 16 rules, `health: ok`) and a new cloud-mode `mimirtool` push script. Fifth
pass, same day: closed gateway-api's blanket exception→502 mapping with a shared
`GrpcErrorMapping.ToProblem()` extension mapping real gRPC status codes to their HTTP equivalents.
**Sixth pass, 2026-07-09: every remaining Medium (18) and actionable Low/Nit item (~30) closed and
verified against current source** — see §4 above for the full breakdown by subsystem, including two
bugs (order-api's gRPC/h2c port conflict, `OutboxRelayWorker`'s EF execution-strategy
incompatibility) discovered outside the original review's scope while building the integration test
the review itself recommended, and an honest disclosure of a k3d/WSL2 pod-networking environment
limitation encountered — and worked around — during verification._

One item remains outside any of these passes' scope, requiring the repo owner's own sign-off rather
than a code fix: `src/frontend/node_modules` is tracked in git (471 MB, 45,010 files, no
`.gitignore` entry unlike `dist/`/`.angular/` right next to it). Nothing in the build/CI/deploy path
reads from the tracked copy — pure accidental bloat, not a deliberate vendoring strategy — but
untracking 471 MB that's already been pushed is a bigger decision (rewriting history or accepting
the repo-size cost) than any review pass should make unilaterally.
