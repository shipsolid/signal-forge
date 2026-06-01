# Testing

This project has 73 automated tests across all four services. All tests run locally without a running cluster, database, or message broker.

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
# Requires jest and jest-preset-angular (installed to /tmp/ng-test-deps by make test-unit)
NODE_PATH=/tmp/ng-test-deps/node_modules \
  /tmp/ng-test-deps/node_modules/.bin/jest --config jest.config.js
```

## Test suites

### order-api.Tests (15 tests)

**Framework:** xUnit 2.9, Moq 4.20, EF Core InMemory 8.0

**Location:** `src/order-api.Tests/`

**What it covers:**

| Test group                               | Count | Description                                                                                        |
| ---------------------------------------- | ----- | -------------------------------------------------------------------------------------------------- |
| `CreateOrder` — validation               | 5     | Zero/negative `ProjectId`; zero/negative/over-max `Amount`; empty/over-500-char `Description`      |
| `CreateOrder` — happy path               | 4     | Persists to DB, returns ID, publishes to `IOrderPublisher`, boundary amounts (0.01 and 999,999.99) |
| `GetOrder`                               | 2     | Found (returns all fields), not found (StatusCode.NotFound)                                        |
| `GetOrdersByProject`                     | 2     | Returns matching rows only, returns empty stream when no rows exist                                |
| `CreateOrder_Valid_PublishesMessageOnce` | 1     | Verifies the publisher mock is called exactly once with the correct arguments                      |
| Boundary                                 | 1     | Amount = 0.01 (lower inclusive boundary)                                                           |

**Key test utilities:**

- `TestServerCallContext` — minimal `ServerCallContext` implementation for passing to gRPC service methods
- `FakeServerStreamWriter<T>` — collects `Written` messages in-memory for assertion on streaming RPCs

**Infrastructure isolation:** EF Core InMemory database; each test uses a unique DB name so tests are fully independent. `IOrderPublisher` is mocked via `Moq`.

```bash
dotnet test src/order-api.Tests/order-api.Tests.csproj
# Passed: 15, Failed: 0
```

---

### gateway-api.Tests (22 tests)

**Framework:** xUnit 2.9, Moq 4.20, EF Core InMemory 8.0, `Microsoft.AspNetCore.Mvc.Testing` 8.0

**Location:** `src/gateway-api.Tests/`

**What it covers:**

| Test group                      | Count | Description                                                        |
| ------------------------------- | ----- | ------------------------------------------------------------------ |
| `GET /api/projects`             | 3     | Empty list, list with data, get by ID found/not-found              |
| `POST /api/projects`            | 2     | Creates project, persists to DB                                    |
| `DELETE /api/projects/:id`      | 2     | Found (204), not found (404)                                       |
| `GET /api/projects/:id/orders`  | 2     | gRPC streaming proxy success, gRPC 502 on failure                  |
| `POST /api/orders` — validation | 5     | Zero/negative `projectId`, invalid amounts, empty/long description |
| `POST /api/orders` — happy path | 2     | 201 with `{id, status}`, 502 on gRPC failure                       |
| `GET /api/notifications`        | 2     | Downstream 200 (proxies body), downstream 502                      |
| `GET /healthz`                  | 1     | Returns `{"status":"healthy"}`                                     |

**Key test infrastructure — `CustomWebApplicationFactory`:**

- Replaces MySQL (`DbContextOptions<AppDbContext>`) with EF Core InMemory using a fixed DB name captured outside the lambda (ensures all DI scopes share one store)
- Replaces `OrderService.OrderServiceClient` with a Moq mock (`MockOrderClient`)
- Replaces `IHttpClientFactory` with a Moq mock (`MockHttpClientFactory`) for `notification-svc` proxy isolation
- Uses `builder.UseSetting()` to inject `ConnectionStrings:DefaultConnection` before startup validation fires

```bash
dotnet test src/gateway-api.Tests/gateway-api.Tests.csproj
# Passed: 22, Failed: 0
```

---

### notification-svc tests (18 tests)

**Framework:** pytest 8.3, fakeredis 2.23, httpx 0.27

**Location:** `src/notification-svc/tests/`

**Setup:**

```bash
cd src/notification-svc
python -m venv .venv
.venv/bin/pip install -r requirements-test.txt
.venv/bin/python -m pytest tests/ -v
```

> **Note:** The active shell Python may point to a different project's venv. Always run tests via `.venv/bin/python` to use the correct environment.

**What it covers:**

| Test group                          | Count | Description                                                                                          |
| ----------------------------------- | ----- | ---------------------------------------------------------------------------------------------------- |
| `test_routes.py` — health           | 1     | `GET /healthz` returns `{"status":"healthy"}`                                                        |
| `test_routes.py` — notifications    | 5     | Empty list, stored items returned, 100-item limit, get by ID found/not-found                         |
| `test_consumer.py` — happy path     | 6     | ACKs message, stores hash, pushes to list, sets dedup TTL, sets notification TTL, increments counter |
| `test_consumer.py` — deduplication  | 2     | Skips duplicate, increments duplicate counter                                                        |
| `test_consumer.py` — error handling | 3     | Invalid JSON → NACK to DLQ, increments failed counter, Redis failure → NACK                          |
| `test_consumer.py` — list capping   | 1     | List trimmed to 1000 entries                                                                         |

**Infrastructure isolation:**

- `fakeredis.FakeRedis(decode_responses=True)` replaces the real Redis client
- `unittest.mock.patch("app.main._consumer_loop")` prevents the RabbitMQ consumer thread from starting
- `app.telemetry` is stubbed in `conftest.py` via `sys.modules` before any app module is imported — this prevents the `opentelemetry-exporter-otlp-proto-grpc` protobuf C extensions from loading (incompatible with Python 3.14's metaclass changes); tests use the real OTel no-op `TracerProvider` for span operations

```bash
.venv/bin/python -m pytest tests/ -v
# 18 passed
```

---

### Frontend tests (18 tests)

**Framework:** Jest 29, jest-preset-angular 14, jsdom

**Location:** `src/frontend/src/app/`

**Setup:**

The project's `node_modules` is owned by root (created by Docker builds). Jest and `jest-preset-angular` are installed to `/tmp/ng-test-deps/` and referenced via `modulePaths` in `jest.config.js`. Run:

```bash
npm install --prefix /tmp/ng-test-deps jest jest-environment-jsdom jest-preset-angular \
  @types/jest typescript --legacy-peer-deps

cd src/frontend
NODE_PATH=/tmp/ng-test-deps/node_modules \
  /tmp/ng-test-deps/node_modules/.bin/jest --config jest.config.js
```

Once `node_modules` is owned by the current user (`sudo chown -R $USER:$USER src/frontend/node_modules`), `npm test` works directly.

**What it covers:**

| Test group                            | Count | Description                                                                    |
| ------------------------------------- | ----- | ------------------------------------------------------------------------------ |
| `ApiService` — projects               | 4     | `getProjects`, `getProject`, `createProject`, `deleteProject` request/response |
| `ApiService` — orders + notifications | 3     | `getOrdersByProject`, `createOrder`, `getNotifications`                        |
| `ApiService` — error propagation      | 2     | HTTP 500 propagates, HTTP 404 propagates                                       |
| `DashboardComponent` — init           | 4     | Calls API on init, renders list, clears loading flag, no error shown           |
| `DashboardComponent` — edge cases     | 2     | Empty state message, error message displayed                                   |
| `DashboardComponent` — mutations      | 3     | `createProject()` sends correct payload, reloads list on success               |

**Infrastructure isolation:** `HttpClientTestingModule` + `HttpTestingController` intercept all HTTP calls; `ApiService` is mocked via `jest.Mocked<ApiService>` in component tests.

```bash
NODE_PATH=/tmp/ng-test-deps/node_modules \
  /tmp/ng-test-deps/node_modules/.bin/jest --config jest.config.js
# 18 passed
```

---

## Coverage summary

| Service          | Tests  | Frameworks                        | DB/IO isolation                   |
| ---------------- | ------ | --------------------------------- | --------------------------------- |
| order-api        | 15     | xUnit, Moq                        | EF InMemory                       |
| gateway-api      | 22     | xUnit, Moq, WebApplicationFactory | EF InMemory, Moq gRPC/HTTP        |
| notification-svc | 18     | pytest, fakeredis                 | fakeredis, patched consumer       |
| frontend         | 18     | Jest, jest-preset-angular         | HttpTestingController, jest mocks |
| **Total**        | **73** |                                   |                                   |

## What is not covered

| Gap                            | Reason                                                                                               |
| ------------------------------ | ---------------------------------------------------------------------------------------------------- |
| Proto schema contract tests    | No schema registry in the lab; drift between gateway-api and order-api would only surface at runtime |
| RabbitMQ consumer backpressure | Requires a real broker; out of scope for unit tests                                                  |
| End-to-end trace propagation   | Validated manually via Jaeger UI (see `docs/spec.md` validation checklist)                           |
| Load / performance             | `make test` applies `k8s/loadtest/` manifests for cluster-level load generation                      |
