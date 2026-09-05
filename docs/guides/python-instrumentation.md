---
title: "Guide: Python Instrumentation"
description: "Step-by-step: instrument a Python FastAPI service with OpenTelemetry — SDK wiring, custom metrics, and RabbitMQ consumer-side async trace propagation via manual context extraction and SpanLink."
tags: ["ShipSolid", "Signal Forge", "Observability", "Guides", "Python"]
updated: 2026-07-30
zettelId: "202607301400-04"
relations:
  - slug: projects/app-signal-forge/guides/README
    kind: related
  - slug: projects/app-signal-forge/guides/collector-pipeline-setup
    kind: depends_on
  - slug: projects/app-signal-forge/guides/dotnet-instrumentation
    kind: related
  - slug: projects/app-signal-forge/architecture/adrs/adr-spanlink-for-async-rabbitmq
    kind: depends_on
---

## Guide: Python Instrumentation

Prerequisite: [[collector-pipeline-setup|Collector & Pipeline Setup]] — you need a live OTLP
endpoint before any of this produces visible output.

This guide covers the pattern used for this project's Python service: a FastAPI HTTP server plus a
background RabbitMQ consumer thread. The RabbitMQ consumer side is the most involved part — it's the
receiving end of the
[[dotnet-instrumentation#Step 8 — Async propagation across RabbitMQ (producer side, outbox pattern)|.NET producer pattern]]
covered in the .NET guide. For exact signal names, see [[otel-contracts|OTel Signal Contracts]].

### Step 1 — Pin compatible package versions

```
opentelemetry-api==1.25.0
opentelemetry-sdk==1.25.0
opentelemetry-exporter-otlp-proto-grpc==1.25.0
opentelemetry-instrumentation-fastapi==0.46b0
opentelemetry-instrumentation-redis==0.46b0
opentelemetry-instrumentation-logging==0.46b0
python-json-logger==2.0.7
```

Pin `opentelemetry-api`/`opentelemetry-sdk` together first, then pin every
`opentelemetry-instrumentation-*` contrib package to the matching `0.4Xb0` release train — contrib
instrumentation packages are versioned on their own schedule and must track whatever release train
lines up with your pinned API/SDK version, not just "latest."

`opentelemetry-exporter-otlp-proto-grpc` is a single package providing **both** the trace and metric
OTLP exporters over gRPC — use it if your collector's receiver speaks gRPC (this project's does; see
[[collector-pipeline-setup#Step 3 — Configure the OTLP receiver|Collector Setup Step 3]]). Swap in
`opentelemetry-exporter-otlp-proto-http` only if your collector is HTTP-only.

Swap `opentelemetry-instrumentation-redis`/`-fastapi` for whatever your service's actual
framework/client libraries are — the
[OpenTelemetry registry](https://opentelemetry.io/ecosystem/registry/?component=instrumentation&language=python)
lists the available auto-instrumentation packages per library.

Deliberately **not** included: `opentelemetry-instrumentation-pika` for the RabbitMQ consumer — see
Step 8 for why.

### Step 2 — One `telemetry.py` module, one entrypoint, called first

```python
# app/telemetry.py
import os
from opentelemetry import trace, metrics
from opentelemetry.sdk.resources import Resource, SERVICE_NAME
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.instrumentation.redis import RedisInstrumentor

SERVICE = "notification-svc"
OTLP_ENDPOINT = os.getenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://localhost:4317")

def setup_telemetry() -> None:
    resource = Resource.create({SERVICE_NAME: SERVICE})   # only service.name here — see Step 3

    tracer_provider = TracerProvider(resource=resource)
    tracer_provider.add_span_processor(
        BatchSpanProcessor(OTLPSpanExporter(endpoint=OTLP_ENDPOINT, insecure=True))
    )
    trace.set_tracer_provider(tracer_provider)

    reader = PeriodicExportingMetricReader(
        OTLPMetricExporter(endpoint=OTLP_ENDPOINT, insecure=True),
        export_interval_millis=15_000,   # match your Prometheus scrape interval
    )
    metrics.set_meter_provider(MeterProvider(resource=resource, metric_readers=[reader]))

    LoggingInstrumentor().instrument()
    RedisInstrumentor().instrument()
```

Call `setup_telemetry()` as the **very first thing** in your application's main module — before
`FastAPI()` is constructed, before any client (Redis, a DB driver) is instantiated:

```python
# main.py
import logging
from pythonjsonlogger import jsonlogger
from app.telemetry import setup_telemetry

handler = logging.StreamHandler()
handler.setFormatter(jsonlogger.JsonFormatter(
    "%(asctime)s %(name)s %(levelname)s %(message)s %(otelTraceID)s %(otelSpanID)s"
))
logging.basicConfig(level=logging.INFO, handlers=[handler])

setup_telemetry()   # must run before FastAPI() app is created, and before any instrumented client

from fastapi import FastAPI
app = FastAPI()
```

The ordering is load-bearing, in this exact sequence: (1) configure your log handler/formatter so
`LoggingInstrumentor` has something to patch, (2) call `setup_telemetry()`, which sets the global
providers and instruments logging + any auto-instrumented client, (3) _then_ construct your app
object and any instrumented clients.

### Step 3 — The gotcha to get right the first time: resource attributes

**Hardcode only `service.name` in `Resource.create()`.** Do not put
`service.namespace`/`service.version`/`deployment.environment` there, even though it's tempting to
set them all in one place. This project's own code hit this bug: an earlier version hardcoded all
four in `Resource.create()`'s dict, and because **explicit values passed to `Resource.create()` take
precedence over the SDK's environment-variable resource detector**, it silently shadowed whatever
`OTEL_RESOURCE_ATTRIBUTES` said at deploy time — `deployment.environment` always read the function's
own hardcoded default, regardless of which real environment the pod was actually running in, with no
error or warning anywhere.

The fix, shown in Step 2: `Resource.create({SERVICE_NAME: SERVICE})` — name only. Let
`OTEL_RESOURCE_ATTRIBUTES` (read automatically by the SDK's env-var resource detector, no code
required) supply everything else:

```
OTEL_RESOURCE_ATTRIBUTES=service.namespace=my-app,service.version=1.0.0,deployment.environment=production
```

### Step 4 — Environment variables

```
OTEL_SERVICE_NAME=notification-svc
OTEL_EXPORTER_OTLP_ENDPOINT=http://grafana-k8s-alloy-receiver.monitoring.svc.cluster.local:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_LOGS_EXPORTER=none
OTEL_METRICS_EXEMPLAR_FILTER=trace_based
OTEL_RESOURCE_ATTRIBUTES=service.namespace=my-app,service.version=1.0.0,deployment.environment=production
```

Source `deployment.environment` from the same single value you use for every other service. See
[[collector-pipeline-setup#Step 7 — Keep deployment_environment consistent across signals|Collector Setup Step 7]]
— the collector re-derives this independently per signal type (traces, metrics, and logs each go
through a different mechanism), and on Grafana Cloud destinations that mechanism overrides whatever
this service sets, so drift between this env var and the collector's config surfaces as a service
that Application Observability files under the wrong environment, not as an error here.

Note the code in Step 2 hardcodes the gRPC OTLP exporter classes directly (`OTLPSpanExporter`,
`OTLPMetricExporter` from the `proto.grpc` package) rather than branching on
`OTEL_EXPORTER_OTLP_PROTOCOL` at runtime — so that env var is really documentation/consistency with
your other services rather than something this code reads and acts on. If you need to support both
gRPC and HTTP collectors from the same codebase, you'd need to select the exporter class based on
that env var yourself; this project's Python service doesn't need to, since it only ever talks to a
gRPC-speaking collector.

### Step 5 — FastAPI auto-instrumentation, applied after the provider is live

```python
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor

# after setup_telemetry() has already run
FastAPIInstrumentor().instrument_app(app, excluded_urls="/healthz")
```

`excluded_urls` prevents a span from being created at all for health-check traffic — zero overhead,
not just a filtered-out span. Apply the same instrument-after-provider-is-set rule to every
`*Instrumentor().instrument()` call in your service, not just this one.

### Step 6 — Custom metric instruments, created lazily

Don't create metric instruments at module import time — the global `MeterProvider` may not be set
yet, and importing this module before `setup_telemetry()` runs elsewhere in your app would silently
produce a no-op meter. Create them lazily on first use instead:

```python
import threading

_meter = None
_meter_lock = threading.Lock()

def _lazy_meter():
    global _meter
    if _meter is None:
        with _meter_lock:
            if _meter is None:
                _meter = metrics.get_meter(SERVICE)
    return _meter

def notifications_processed_counter():
    return _lazy_meter().create_counter(
        "notifications.processed.total", unit="{notification}",
        description="Total notifications processed, labeled by processing status")

def processing_duration_histogram():
    return _lazy_meter().create_histogram(
        "notifications.processing.duration", unit="ms",
        description="End-to-end consumer processing latency")
```

Guard the lazy-init with a lock if more than one thread might race to create the meter first (a
FastAPI request thread and a background consumer thread, for instance).

### Step 7 — Custom spans

```python
with tracer.start_as_current_span("notification.send_email") as span:
    span.set_attribute("email.order_id", event.order_id)
    span.set_attribute("email.delay_ms", delay_ms)
    send_mock_email()
```

Same rule as the .NET guide: avoid unbounded values as **metric** labels; put per-entity IDs on
**span attributes** instead, and use the counter's `status` dimension (bounded: `success` /
`duplicate` / `failed`) rather than the entity ID itself.

### Step 8 — RabbitMQ consumer: manual propagation

**Don't use `opentelemetry-instrumentation-pika`.** As of the versions pinned in Step 1, this
project found it does not reliably extract incoming trace context from message headers across pika
versions — it creates its own span but doesn't correctly link back to the producer's context in
every case. Manual extraction is more code but is explicit, version-independent, and gives you full
control over the span kind and relationship (which matters a lot here — see below).

```python
from opentelemetry import trace
from opentelemetry.propagate import extract
from opentelemetry.trace import Link, SpanKind
from opentelemetry.context import attach, detach

class HeadersGetter:
    """pika delivers header values as bytes — decode before extraction."""
    def get(self, carrier: dict, key: str) -> list[str]:
        val = carrier.get(key)
        if val is None:
            return []
        return [val.decode("utf-8")] if isinstance(val, bytes) else [str(val)]

    def keys(self, carrier: dict) -> list[str]:
        return list(carrier.keys()) if carrier else []

_getter = HeadersGetter()

def handle_order_created(ch, method, properties, body: bytes):
    headers = properties.headers or {}
    ctx = extract(headers, getter=_getter)
    token = attach(ctx)

    try:
        parent_span_ctx = trace.get_current_span(ctx).get_span_context()
        links = [Link(parent_span_ctx)] if parent_span_ctx.is_valid else []

        with tracer.start_as_current_span(
            "notification.process",
            kind=SpanKind.CONSUMER,
            links=links,   # Link, not parent — see below
        ) as span:
            span.set_attribute("messaging.system", "rabbitmq")
            span.set_attribute("messaging.operation", "receive")
            span.set_attribute("messaging.rabbitmq.routing_key", "order.created")
            # ... process the message, ack/nack ...
    finally:
        detach(token)   # always — prevents context leaking across message deliveries on a shared thread
```

**Why a `Link`, not a parent-child relationship.** OTel's messaging semantic conventions distinguish
two relationships for a consumer span relative to its producer:

- **Parent-child** — appropriate when the consumer processes the message synchronously, as part of
  the same logical operation, with no meaningful time gap.
- **`Link`** — appropriate for genuinely asynchronous processing: the consumer runs in a separate
  process, possibly much later, and the message might be **redelivered** (e.g. after a NACK),
  producing multiple consumer spans that all relate back to one producer span. A parent-child
  relationship can't represent "one producer span, several independent consumer attempts" cleanly; a
  link can. See [[adr-spanlink-for-async-rabbitmq|ADR-002]] for the full reasoning. In your trace
  UI, a linked span typically renders as a dashed reference rather than a solid parent-child line —
  this is expected and correct, not a bug in your propagation code.

**Always `detach()` in a `finally` block.** If extraction or span creation raises before you reach
`detach()`, the context leaks onto whatever thread handles the next message — on a pooled/reused
thread (which pika's consumer thread is), that means the next unrelated message could inherit the
wrong trace context.

### Step 9 — Test span-content assertions without a real collector

Two levels of test double, matched to what you're actually verifying:

For tests that don't care about span content, stub the whole telemetry module out before anything
else imports it:

```python
# conftest.py
import sys, types
from unittest.mock import MagicMock
from opentelemetry import trace

_telemetry_stub = types.ModuleType("app.telemetry")
_telemetry_stub.get_tracer = lambda name="": trace.get_tracer(name)   # real no-op tracer
_telemetry_stub.setup_telemetry = MagicMock()
sys.modules.setdefault("app.telemetry", _telemetry_stub)
```

Use the **real** `trace.get_tracer(name)` here, not a `MagicMock()` — if any code path formats a
trace/span ID (e.g. `format(span.get_span_context().trace_id, "032x")`), calling that on a
`MagicMock()` raises `TypeError` instead of producing a harmless zero-value ID.

When a test needs to assert on actual span attributes (e.g. "does the duplicate-message path set
`notification.duplicate=True`?"), use an isolated in-memory exporter instead of mocking the tracer
away entirely:

```python
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

def test_duplicate_message_sets_span_attribute():
    exporter = InMemorySpanExporter()
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(exporter))   # synchronous — spans available immediately
    tracer = provider.get_tracer("test")

    with patch("app.consumer.get_tracer", return_value=tracer):
        handle_order_created(mock_channel, mock_method, mock_properties, duplicate_message_body)

    spans = exporter.get_finished_spans()
    assert spans[0].attributes["notification.duplicate"] is True
```

Use `SimpleSpanProcessor` (synchronous export), not `BatchSpanProcessor`, in tests — you need the
span available for assertion the moment the `with` block exits, not after a batch timer fires.

### Step 10 — Verify

1. Publish an order-creation event from your producer service and confirm `notification.process`
   appears in your trace backend as a `CONSUMER` span **linked** (dashed line) to the producer's
   `PRODUCER` span, both carrying the same `traceId`.
2. Confirm log correlation: check that a log line emitted inside the span carries
   `otelTraceID`/`otelSpanID` matching the trace you just found.
3. Confirm the resource-attribute gotcha from Step 3 didn't recur — check `deployment.environment`
   on an actual exported span/metric matches the real environment, not a hardcoded default.

Next: back to [[collector-pipeline-setup|Collector & Pipeline Setup]] if you haven't wired the
receiver yet, or on to [[frontend-rum-instrumentation|Frontend RUM Instrumentation]].
