---
title: "Testing"
description: "Reference for signal-forge's 140 automated tests across all four services, including setup commands, per-suite coverage, and known gaps."
tags: ["ShipSolid", "Signal Forge", "Testing"]
updated: 2026-09-06
zettelId: "202607091847-42"
relations:
  - slug: patterns/04-microservice-patterns/05-backpressure/05-backpressure
    kind: related
  - slug: patterns/04-microservice-patterns/14-outbox/14-outbox
    kind: related
  - slug: projects/app-signal-forge/spec
    kind: related
  - slug: observability/reference/jaeger
    kind: related
---

## Testing

This project has 140 fast automated service tests across all four services, plus an opt-in
cross-language integration test and repository policy tests. Most run locally without a running
cluster, database, or message broker — the one exception is `OutboxRelayWorkerTests`, which needs a
real PostgreSQL via Testcontainers (Docker required), not a cluster or broker.

## Quick start

```bash
# From the repo root: run everything
make test-unit
```

Or run each suite individually:

```bash
# .NET services
cd src
dotnet test order-api.Tests/order-api.Tests.csproj
dotnet test gateway-api.Tests/gateway-api.Tests.csproj

# Python service
cd src/notification-svc
python -m venv .venv && .venv/bin/pip install -r requirements-test.txt
.venv/bin/python -m pytest tests/ -v

# Angular frontend
cd src/frontend
npm ci --legacy-peer-deps   # jest/jest-preset-angular are real devDependencies now
npx jest --config jest.config.js
```

### CI policy tests

The release workflow also executes a small Python regression suite for the immutable-deployment and
observability-policy contracts. It is distinct from service unit tests: it asserts that all four
local app image markers become digest references, Secrets are excluded from the rendered plan,
runtime telemetry identity is preserved, QA ingress is TLS/host scoped, incomplete releases fail,
and known unbounded metric dimensions are rejected.

```bash
python3 -m unittest discover -s scripts/ci/tests -v
```

The workflow additionally renders the collector inputs, runs `promtool` against SLO rules, and runs
the real Alloy validator. See [Immutable CI/CD Promotion](deployment/ci-cd.md) and
[OTel Signal Contracts](observability/otel-contracts.md#observability-as-release-policy).

## Test suites

### order-api.Tests (30 tests)

**Framework:** xUnit 2.9, Moq 4.20, EF Core InMemory 8.0 (`OrderGrpcServiceTests`) + a real
PostgreSQL via Testcontainers (`OutboxRelayWorkerTests` — see why below)

**Location:** `src/order-api.Tests/`

**What it covers:**

| Test group                      | Count | Description                                                                                                                                                                                                                                                                                                                             |
| ------------------------------- | ----- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CreateOrder` — validation      | 7     | Zero/negative `ProjectId`; zero/negative/over-max `Amount` (3 cases); empty/over-500-char `Description`                                                                                                                                                                                                                                 |
| `CreateOrder` — happy path      | 4     | Persists to DB, writes outbox entry, boundary amounts (0.01 and 999,999.99)                                                                                                                                                                                                                                                             |
| `CreateOrder` — idempotency key | 3     | Repeated key replays the original order (no duplicate row), different keys create separate orders, no key allows multiple orders (`OrderGrpcServiceTests.cs`)                                                                                                                                                                           |
| `GetOrder`                      | 2     | Found (returns all fields), not found (StatusCode.NotFound)                                                                                                                                                                                                                                                                             |
| `GetOrdersByProject`            | 4     | Returns matching rows only, returns empty stream when no rows exist, zero/negative `ProjectId` → `InvalidArgument` (2 cases)                                                                                                                                                                                                            |
| `OutboxRelayWorkerTests`        | 8     | Publishes + marks processed, payload contains order ID, no-op when queue empty, publisher failure leaves message unprocessed, already-processed message skipped, traceparent forwarded, **two concurrent replicas only publish each message once**, `outbox.relay` shares the original request's trace ID (`OutboxRelayWorkerTests.cs`) |
| `OrderPublisherTests`           | 2     | Real broker (Testcontainers RabbitMQ): message arrives with `traceparent` header intact when present, publishes without the header when absent (`OrderPublisherTests.cs`)                                                                                                                                                               |

**Key test utilities:**

- `TestServerCallContext` — minimal `ServerCallContext` implementation for passing to gRPC service
  methods
- `FakeServerStreamWriter<T>` — collects `Written` messages in-memory for assertion on streaming
  RPCs

**Infrastructure isolation:** `OrderGrpcServiceTests` uses EF Core InMemory (unique DB name per
test); `IOrderPublisher` is mocked via `Moq` throughout. `OutboxRelayWorkerTests` exercises the
[[patterns/04-microservice-patterns/14-outbox/14-outbox|transactional outbox]] relay against a real
`postgres:16.4` container via Testcontainers instead — InMemory doesn't support transactions or raw
SQL, and the worker's multi-replica-safe row claiming (`FOR UPDATE SKIP LOCKED`) needs both to be
exercised for real, not simulated. One container is shared across the test class; each test gets a
fresh schema via `EnsureDeleted`/`EnsureCreated` in `IAsyncLifetime.InitializeAsync`. Requires
Docker.

```bash
dotnet test src/order-api.Tests/order-api.Tests.csproj
# Passed: 30, Failed: 0
```

---

### gateway-api.Tests (27 test methods, 29 executions)

**Framework:** xUnit 2.9, Moq 4.20, EF Core InMemory 8.0, `Microsoft.AspNetCore.Mvc.Testing` 8.0

**Location:** `src/gateway-api.Tests/`

**What it covers:**

| Test group                      | Count | Description                                                                                                                   |
| ------------------------------- | ----- | ----------------------------------------------------------------------------------------------------------------------------- |
| `GET /api/projects`             | 2     | Empty list, list with data                                                                                                    |
| `GET /api/projects/:id`         | 2     | Found (200), not found (404)                                                                                                  |
| `POST /api/projects`            | 2     | Creates project, persists to DB                                                                                               |
| `DELETE /api/projects/:id`      | 2     | Found (204), not found (404)                                                                                                  |
| `GET /api/projects/:id/orders`  | 4     | gRPC streaming proxy success, and status mapping for Unavailable→503, InvalidArgument→400, Internal→502                       |
| `POST /api/orders` — validation | 7     | Zero/negative `projectId`, invalid amounts (3 cases, one `[Theory]`), empty/long description                                  |
| `POST /api/orders` — happy path | 3     | 201 with `{id, status}`, 502 on generic gRPC failure, 400 on gRPC InvalidArgument                                             |
| `GET /api/orders/:id`           | 3     | Found (200), not found (404), 503 on gRPC Unavailable — completes the passthrough `CreateOrder`'s `Location` header points at |
| `GET /api/notifications`        | 3     | Downstream 200 (proxies body), downstream 502, downstream 4xx passed through as-is                                            |
| `GET /healthz`                  | 1     | Returns `{"status":"healthy"}`                                                                                                |

**Key test infrastructure — `CustomWebApplicationFactory`:**

- Replaces MySQL (`DbContextOptions<AppDbContext>`) with EF Core InMemory using a fixed DB name
  captured outside the lambda (ensures all DI scopes share one store)
- Replaces `OrderService.OrderServiceClient` with a Moq mock (`MockOrderClient`)
- Replaces `IHttpClientFactory` with a Moq mock (`MockHttpClientFactory`) for `notification-svc`
  proxy isolation
- Uses `builder.UseSetting()` to inject `ConnectionStrings:DefaultConnection` before startup
  validation fires

```bash
dotnet test src/gateway-api.Tests/gateway-api.Tests.csproj
# Passed: 29, Failed: 0
```

---

### notification-svc tests (27 tests)

**Framework:** pytest 8.3, fakeredis 2.23, httpx 0.27

**Location:** `src/notification-svc/tests/`

**Setup:**

```bash
cd src/notification-svc
python -m venv .venv
.venv/bin/pip install -r requirements-test.txt
.venv/bin/python -m pytest tests/ -v
```

> **Note:** The active shell Python may point to a different project's venv. Always run tests via
> `.venv/bin/python` to use the correct environment.

**What it covers:**

| Test group                           | Count | Description                                                                                                                                                                              |
| ------------------------------------ | ----- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `test_routes.py` — health            | 1     | `GET /healthz` returns `{"status":"healthy"}`                                                                                                                                            |
| `test_routes.py` — readiness         | 3     | Ready when consumer connected + Redis reachable, not-ready when consumer disconnected, not-ready when Redis unreachable (`/readyz`)                                                      |
| `test_routes.py` — notifications     | 5     | Empty list, stored items returned, 100-item limit, get by ID found/not-found                                                                                                             |
| `test_consumer.py` — happy path      | 6     | ACKs message, stores hash, pushes to list, sets dedup TTL, sets notification TTL, increments counter                                                                                     |
| `test_consumer.py` — deduplication   | 3     | Skips duplicate, sets `notification.duplicate` span attribute, increments duplicate counter                                                                                              |
| `test_consumer.py` — dedup atomicity | 2     | Dedup uses a single atomic `SET ... NX` (not `exists()`+`set()`), second of two rapid deliveries is deduped                                                                              |
| `test_consumer.py` — reprocessing    | 1     | Reprocessing the same order (after TTL drift) doesn't push a duplicate `notification_ids` list entry (`LREM` before `LPUSH`)                                                             |
| `test_consumer.py` — error handling  | 5     | Invalid JSON → NACK to DLQ, increments failed counter, Redis error → NACK **with requeue** (transient, not DLQ'd), increments `failed_transient` counter, unexpected error → NACK to DLQ |
| `test_consumer.py` — list capping    | 1     | List trimmed to 1000 entries                                                                                                                                                             |

**Infrastructure isolation:**

- `fakeredis.FakeRedis(decode_responses=True)` replaces the real Redis client
- `unittest.mock.patch("app.main._consumer_loop")` prevents the RabbitMQ consumer thread from
  starting
- `app.telemetry` is stubbed in `conftest.py` via `sys.modules` before any app module is imported —
  this prevents the `opentelemetry-exporter-otlp-proto-grpc` protobuf C extensions from loading
  (incompatible with Python 3.14's metaclass changes); tests use the real OTel no-op
  `TracerProvider` for span operations

```bash
.venv/bin/python -m pytest tests/ -v
# 27 passed
```

---

### Frontend tests (54 tests)

**Framework:** Jest 29.7, jest-preset-angular 14.6, jsdom

**Location:** `src/frontend/src/app/`

**Setup:**

`jest`, `jest-preset-angular`, `jest-environment-jsdom`, and `@types/jest` are real, pinned
`devDependencies` in `package.json` — installed into `src/frontend/node_modules` like everything
else:

```bash
cd src/frontend
npm ci --legacy-peer-deps   # or: npm install
npx jest --config jest.config.js
```

`--legacy-peer-deps` is needed because `@angular-devkit/build-angular` and `jest-preset-angular`
declare overlapping-but-not-identical Angular peer ranges; both are satisfied in practice.

If `node_modules` ends up root-owned (e.g. from an `npm ci` run inside a bind-mounted Docker
container), `npm ci` fails loudly with `EACCES` rather than silently misbehaving. Fix with
`sudo chown -R $USER:$USER src/frontend/node_modules` and re-run.

> **Formerly:** this project installed Jest ad hoc into a separate `/tmp/ng-test-deps` prefix
> (worked around root-owned `node_modules`) and referenced it via `NODE_PATH`. That split install
> was the actual root cause of a real breakage, not just a theoretical risk: `jest-preset-angular`'s
> own `require()` calls resolved `typescript` from its _own_ prefix's `node_modules` first — an
> unpinned install there could land a `typescript` major ahead of this project's pinned `~5.4.2`
> (observed: `6.0.3`) and/or a `jest-preset-angular` major requiring a newer Angular than this
> project pins, and either one broke Ivy's DI factory generation with a bare `NG0202` at every
> `TestBed.inject()` call — no code change on either side. Colocating everything in one
> `node_modules` (this section, now) removes the cross-resolution ambiguity entirely: `npm ci`
> installs versions declared and locked in this project's own `package.json`/`package-lock.json`.

**What it covers (by spec file):**

| Spec file                          | Count | Covers                                                                                                                                                   |
| ---------------------------------- | ----- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `api.service.spec.ts`              | 11    | `ApiService` — projects/orders/notifications CRUD + error propagation, `window.__ENV.API_BASE_URL` wins over `environment.apiBaseUrl` when set (2 cases) |
| `dashboard.component.spec.ts`      | 9     | Init, list rendering, loading/error/empty states, project mutations                                                                                      |
| `error-test.component.spec.ts`     | 6     | `/api/error` and `/api/slow` demo endpoints                                                                                                              |
| `create-order.component.spec.ts`   | 8     | Order creation form validation and submission                                                                                                            |
| `project-detail.component.spec.ts` | 8     | Project detail view, order list for a project                                                                                                            |
| `notifications.component.spec.ts`  | 8     | Notifications list rendering and polling                                                                                                                 |
| `faro.spec.ts`                     | 4     | `scrubTelemetryItem` — redacts emails from string fields, leaves non-string fields untouched, ignores non-matching items                                 |

**Infrastructure isolation:** `HttpClientTestingModule` + `HttpTestingController` intercept all HTTP
calls; `ApiService` is mocked via `jest.Mocked<ApiService>` in component tests.

```bash
npx jest --config jest.config.js
# 54 passed
```

---

### integration-tests (1 test, opt-in)

**Framework:** xUnit 2.9, Testcontainers 4.13 (base + `.PostgreSql` + `.RabbitMq`)

**Location:** `src/integration-tests/`

**Not part of the fast default suite** — needs Docker to build order-api and notification-svc from
their real Dockerfiles (not project references) and run six containers (Postgres, RabbitMQ, Redis,
Jaeger, order-api, notification-svc). Expect ~1.5-2 minutes: mostly the two image builds, since
Testcontainers can't reuse `deploy-local.sh`'s image cache. Marked
`[Trait("Category", "Integration")]`; there's no `.sln` in this repo so it's never picked up by the
four project-scoped `dotnet test` commands above — it only runs when invoked directly:

```bash
dotnet test src/integration-tests/integration-tests.csproj
```

**What it covers:** the full cross-language 5-hop trace, for real — a genuine gRPC `CreateOrder`
call against a real order-api (real Postgres) publishes to a real RabbitMQ, a real Python
notification-svc consumes it (real Redis), and the resulting Jaeger trace is queried via its HTTP
API to assert `order.create`, `outbox.relay`, `order.publish`, and `notification.process` all share
one trace ID. This test is what surfaced three real bugs during this session, none of which were
visible from the mocked unit suites above:

- **order-api's gRPC port never actually worked over plain HTTP.** A single Kestrel endpoint
  configured for mixed HTTP/1.1+HTTP/2 without TLS silently downgrades every connection to HTTP/1.1
  (Kestrel logs "HTTP/2 requires TLS application protocol negotiation"), so gRPC's HTTP/2
  prior-knowledge preface got rejected with `HTTP_1_1_REQUIRED`. Fixed by splitting order-api onto
  two dedicated ports — 5001 HTTP/1.1-only for kubelet's `/healthz`, 5002 HTTP/2-only for gRPC (see
  `Program.cs`'s "gRPC server" comment). Confirmed via a from-scratch, no-Docker, no-k8s repro
  before touching any production code.
- **`OutboxRelayWorker`'s explicit transaction was incompatible with `EnableRetryOnFailure()`.** A
  bare `Database.BeginTransactionAsync()` under a registered retrying execution strategy throws
  `InvalidOperationException` on every call — meaning every outbox poll cycle failed silently
  (caught by the worker's own retry-logging catch block) since `EnableRetryOnFailure()` was added
  earlier in the same review-remediation pass. Fixed by wrapping the transaction in
  `Database.CreateExecutionStrategy().ExecuteAsync(...)`.
- The `outbox.relay`/`order.publish` disconnected-trace gap this test was originally written to
  verify — see `OutboxRelayWorker.PublishAndMarkAsync`'s `ActivityLink` fix and
  `OrderPublisher.cs`'s updated header-comment trace diagram.

```bash
dotnet test src/integration-tests/integration-tests.csproj
# Passed: 1, Failed: 0
```

---

## Coverage summary

| Service          | Tests   | Frameworks                        | DB/IO isolation                                        |
| ---------------- | ------- | --------------------------------- | ------------------------------------------------------ |
| order-api        | 30      | xUnit, Moq                        | EF InMemory + Testcontainers (real Postgres, RabbitMQ) |
| gateway-api      | 29      | xUnit, Moq, WebApplicationFactory | EF InMemory, Moq gRPC/HTTP                             |
| notification-svc | 27      | pytest, fakeredis                 | fakeredis, patched consumer                            |
| frontend         | 54      | Jest, jest-preset-angular         | HttpTestingController, jest mocks                      |
| **Total**        | **140** |                                   |                                                        |

## What is not covered

| Gap                                                                                                   | Reason                                                                                                                                                                                                              |
| ----------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Proto schema contract tests                                                                           | No schema registry in the lab; drift between gateway-api and order-api would only surface at runtime                                                                                                                |
| RabbitMQ consumer [[patterns/04-microservice-patterns/05-backpressure/05-backpressure\|backpressure]] | Requires a real broker; out of scope for unit tests                                                                                                                                                                 |
| Concurrent idempotency-key race                                                                       | The `CreateOrder` idempotency fast-path (`OrderGrpcService.cs`) is unit-tested for sequential retries; genuinely concurrent duplicate submissions rely on the DB unique index as an untested-here backstop          |
| End-to-end trace propagation                                                                          | Automated by `src/integration-tests` (opt-in, needs Docker — see above); also validated manually via Jaeger UI ([[projects/app-signal-forge/spec\|docs/spec.md]] checklist) for exploratory checks |
| Load / performance                                                                                    | `kubectl apply -f k8s/loadtest/` for cluster-level load generation                                                                                                                                                  |
