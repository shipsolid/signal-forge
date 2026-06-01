# Contributing

## Adding a new service

Follow these steps to add a fifth (or sixth) service to the lab while keeping the full observability pipeline intact.

### 1. Create the service

Pick a runtime that already has OTel SDK support. The existing services use .NET 8, Python 3, and Angular 17 as reference implementations.

Scaffold the service under `src/<service-name>/`. Copy the `Dockerfile` from the closest existing service and update `COPY` paths.

### 2. Wire OpenTelemetry

Every service must emit the three OTel signals. Use the patterns below — they mirror what the existing services already do.

#### Tracing

```
TracerProvider
  .AddAspNetCoreInstrumentation(opts => {
      opts.Filter   = ctx => ctx.Request.Path != "/healthz"; // exclude probe traffic
      opts.RecordException = true;
  })
  .AddOtlpExporter()   // endpoint from OTEL_EXPORTER_OTLP_ENDPOINT env var
```

Set these resource attributes so Alloy can identify the service:

```
OTEL_SERVICE_NAME=<service-name>
OTEL_RESOURCE_ATTRIBUTES=service.namespace=otel-lab,service.version=1.0.0,deployment.environment=signal-forge-dev
```

#### Metrics

```
MeterProvider
  .AddAspNetCoreInstrumentation()
  .AddRuntimeInstrumentation()    // .NET only
  .AddProcessInstrumentation()
  .AddMeter("<service-name>")     // for custom instruments
  .AddOtlpExporter()
```

Set `OTEL_METRICS_EXEMPLAR_FILTER=trace_based` to link histogram observations to traces.

#### Logs

Write structured JSON to stdout — do **not** export logs via OTLP. Alloy's `loki.source.kubernetes` pipeline tails pod logs and ships them to Loki. See [ADR-001](docs/architecture/decisions.md) for the rationale.

The log record **must** include `TraceId` and `SpanId` fields so Alloy's `trace_correlation` stage can extract them and attach them as Loki structured metadata. Field names differ by runtime:

| Runtime                 | TraceId field | SpanId field |
| ----------------------- | ------------- | ------------ |
| .NET (`AddJsonConsole`) | `TraceId`     | `SpanId`     |
| Python (`logging`)      | `otelTraceID` | `otelSpanID` |

See [Log-to-Trace Correlation](docs/observability/correlation.md) for the Alloy stage configuration.

#### Custom instrumentation checklist

For each business operation (e.g. creating an order, publishing an event):

- [ ] Start a custom `Activity` / `Span` with a meaningful name (`<service>.operation`)
- [ ] Add semantic attributes (`db.system`, `messaging.system`, `rpc.method`, etc.)
- [ ] Record exceptions on the span (`activity.RecordException(ex)` / `span.record_exception(e)`)
- [ ] Set span status to `Error` on failure
- [ ] Create at least one counter and one histogram instrument for the operation

See [OTel Signal Contracts](docs/observability/otel-contracts.md) for the full attribute catalogue.

### 3. Add a health endpoint

```
GET /healthz  →  200 { "status": "healthy" }
```

Exclude it from traces (SDK filter) and from Alloy metrics scraping (drop rule in the pipeline config).

### 4. Write Kubernetes manifests

Copy `k8s/app/gateway/` as a template. At minimum:

```
k8s/app/<service-name>/
  deployment.yaml   # see below
  service.yaml
```

Required env vars in `deployment.yaml`:

```yaml
- name: OTEL_SERVICE_NAME
  value: "<service-name>"
- name: OTEL_EXPORTER_OTLP_ENDPOINT
  value: "http://alloy-receiver.monitoring.svc.cluster.local:4317"
- name: OTEL_EXPORTER_OTLP_PROTOCOL
  value: "grpc"
- name: OTEL_METRICS_EXEMPLAR_FILTER
  value: "trace_based"
- name: OTEL_RESOURCE_ATTRIBUTES
  value: "service.namespace=otel-lab,service.version=1.0.0,deployment.environment=signal-forge-dev"
```

Add a `readinessProbe` and `livenessProbe` pointing at `/healthz`.

### 5. Register in the Makefile

Add the image name to the `IMAGES` variable at the top of `Makefile`:

```makefile
IMAGES := otel-frontend gateway-api order-api notification-svc <service-name>
```

Add the new manifest directory to both `deploy-cloud` and `deploy-local` targets:

```makefile
kubectl apply -f k8s/app/<service-name>/
```

### 6. Write tests

All services must have automated tests that run without a cluster. See [Testing](docs/testing.md) for the test strategy and isolation patterns.

Minimum coverage:

- [ ] Input validation (invalid fields return the correct error code/status)
- [ ] Happy path (correct response shape, side effects verified)
- [ ] Downstream failure handling (gRPC/HTTP/messaging failure → correct error response)

### 7. Write documentation

Add a service doc at `docs/services/<service-name>.md`. Use `docs/services/gateway-api.md` as the template. The doc must include:

- [ ] Role, runtime, port, replica count
- [ ] Endpoint table (path, method, description, downstream calls)
- [ ] Configuration table (env var, source, required?)
- [ ] OTel instrumentation section (packages used, span names, custom attributes, metric instruments)

Update the following cross-references:

- [ ] `docs/architecture/overview.md` — add service to topology diagram and inventory table
- [ ] `docs/observability/otel-contracts.md` — add span names and metric instruments
- [ ] `docs/api/rest.md` or `docs/api/grpc.md` — add endpoint / RPC documentation
- [ ] `README.md` — add to the repository layout tree

---

## Pull request checklist

Before opening a PR:

- [ ] `make test-unit` passes (all 73 + new tests green)
- [ ] `make build` succeeds (all Docker images build cleanly)
- [ ] New service has a `docs/services/<name>.md`
- [ ] OTel contracts documented in `docs/observability/otel-contracts.md`
- [ ] `OTEL_SERVICE_NAME` in `deployment.yaml` matches the `DiagnosticsConfig.ServiceName` constant in code
- [ ] No secrets or credential material in committed files
- [ ] `.gitignore` updated if new build artefact directories were introduced

## Code style

Follow the conventions of the existing service in the same runtime:

- **.NET:** top-of-file comment block listing OTel instrumentation points (see `src/gateway-api/Program.cs`)
- **Python:** module-level docstring; inline comments on OTel spans and counter increments
- **TypeScript:** JSDoc on public service methods

Avoid committing generated files (`bin/`, `obj/`, `__pycache__/`, `dist/`, `.angular/`). They are listed in `.gitignore`.
