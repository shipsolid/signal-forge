"""
OpenTelemetry bootstrap for notification-svc.

Responsibilities:
  • Initialise TracerProvider with OTLP/gRPC export to Alloy
  • Initialise MeterProvider with OTLP/gRPC export to Alloy
  • Wire up LoggingInstrumentation so Python log records get
    otelTraceID / otelSpanID attributes injected automatically
  • Expose thin factory functions (get_tracer, get_meter) and
    factory helpers for the three custom metric instruments

Call setup_telemetry() ONCE, early in process startup (main.py),
before any instrumented code runs.  All subsequent calls to
get_tracer() / get_meter() return the configured global providers.

OTEL_LOGS_EXPORTER=none is set in the Dockerfile and K8s Deployment:
  We deliberately do NOT export logs via OTLP.  Instead, the
  application writes structured JSON to stdout, and Grafana Alloy's
  loki.source.kubernetes pipeline tails those logs, extracts TraceId
  and SpanId via a JSON stage, and ships them to Loki with structured
  metadata.  This mirrors the production log-shipping pattern and
  validates the Alloy log-tailing + trace-correlation pipeline.
"""

import os

from opentelemetry import metrics, trace
from opentelemetry.exporter.otlp.proto.grpc.metric_exporter import OTLPMetricExporter
from opentelemetry.exporter.otlp.proto.grpc.trace_exporter import OTLPSpanExporter
from opentelemetry.instrumentation.logging import LoggingInstrumentor
from opentelemetry.instrumentation.redis import RedisInstrumentor
from opentelemetry.sdk.metrics import MeterProvider
from opentelemetry.sdk.metrics.export import PeriodicExportingMetricReader
from opentelemetry.sdk.resources import SERVICE_NAME, Resource
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import BatchSpanProcessor

SERVICE = "notification-svc"

# OTEL_EXPORTER_OTLP_ENDPOINT is set via K8s Deployment env var to
# "http://alloy.otel-lab.svc.cluster.local:4317".
# The default here works for docker-compose / local runs.
OTLP_ENDPOINT = os.getenv("OTEL_EXPORTER_OTLP_ENDPOINT", "http://alloy:4317")


def setup_telemetry() -> None:
    """
    Initialise the global OTel SDK providers.

    Must be called before any instrumented code (FastAPI app creation,
    Redis client initialisation, etc.).

    Thread safety: safe to call once from the main thread at startup.
    """

    # Resource describes the entity producing telemetry.
    # OTEL_RESOURCE_ATTRIBUTES env var can extend this dict without
    # code changes (e.g. adding k8s.pod.name at runtime in K8s via
    # the downward API — though Alloy's k8sattributes processor handles
    # that at the collector level for us).
    resource = Resource.create(
        {
            SERVICE_NAME: SERVICE,
            "service.namespace": "otel-lab",
            "service.version": "1.0.0",
            "deployment.environment": os.getenv("DEPLOYMENT_ENVIRONMENT", "local"),
        }
    )

    # ── TracerProvider ────────────────────────────────────────────────────────
    # BatchSpanProcessor buffers spans and sends them in configurable batches.
    # This is the production-appropriate exporter (vs SimpleSpanProcessor which
    # exports synchronously and blocks the calling thread).
    tracer_provider = TracerProvider(resource=resource)
    tracer_provider.add_span_processor(
        BatchSpanProcessor(OTLPSpanExporter(endpoint=OTLP_ENDPOINT, insecure=True))
    )
    trace.set_tracer_provider(tracer_provider)

    # ── MeterProvider ─────────────────────────────────────────────────────────
    # PeriodicExportingMetricReader collects and exports on a fixed interval.
    # 15s matches Prometheus's default scrape interval — if using push export
    # to Alloy's OTLP receiver, a 15s interval ensures no gaps in dashboards.
    reader = PeriodicExportingMetricReader(
        OTLPMetricExporter(endpoint=OTLP_ENDPOINT, insecure=True),
        export_interval_millis=15_000,
    )
    meter_provider = MeterProvider(resource=resource, metric_readers=[reader])
    metrics.set_meter_provider(meter_provider)

    # ── Log context injection ─────────────────────────────────────────────────
    # LoggingInstrumentation patches the standard Python logging system to
    # inject otelTraceID, otelSpanID, and otelServiceName into every LogRecord.
    # These appear as extra fields in JSON log output and are extracted by
    # Alloy's loki.process stage.template normalisation block for trace correlation.
    LoggingInstrumentor().instrument()

    # ── Redis instrumentation ─────────────────────────────────────────────────
    # Must be called here — after the global TracerProvider is set — so that
    # Redis command spans are exported correctly.  Calling instrument() after
    # the provider is configured is the required ordering.
    # redis_client.get_redis() does NOT call instrument(); it only creates the
    # client connection, which may happen before or after any request arrives.
    RedisInstrumentor().instrument()


def get_tracer():
    """Return the configured global tracer for notification-svc."""
    return trace.get_tracer(SERVICE)


def get_meter():
    """Return the configured global meter for notification-svc."""
    return metrics.get_meter(SERVICE)


# ── Custom instrument factories ───────────────────────────────────────────────
# Each instrument is created once and cached in the consumer module.
# They are defined as factory functions (not module-level singletons) because
# instruments must be created AFTER setup_telemetry() configures the global
# MeterProvider — creating them before would silently attach them to a
# no-op provider and produce no data.

_meter: metrics.Meter | None = None


def _lazy_meter() -> metrics.Meter:
    """Return cached meter, creating it on first call."""
    global _meter
    if _meter is None:
        _meter = get_meter()
    return _meter


def notifications_processed_counter():
    """
    Counter: total notifications processed, labeled by status.

    Labels: status=success | duplicate | failed

    Validates: cross-language metric visible in Prometheus alongside
    .NET metrics from gateway-api and order-api.
    Prometheus name: notifications_processed_total
    """
    return _lazy_meter().create_counter(
        "notifications.processed.total",
        unit="{notification}",
        description="Total notifications processed, labeled by processing status",
    )


def processing_duration_histogram():
    """
    Histogram: end-to-end processing time from RabbitMQ delivery to ACK.

    Captures the full cost of: dedup check + Redis write + email mock.
    Useful for detecting Redis or downstream latency regressions.
    Prometheus name: notifications_processing_duration
    """
    return _lazy_meter().create_histogram(
        "notifications.processing.duration",
        unit="ms",
        description="End-to-end notification processing duration from consume to completion",
    )


def email_send_duration_histogram():
    """
    Histogram: mock email send latency (100-500ms artificial delay).

    In the real service this would measure actual email API calls.
    Validates that Python histograms flow through Alloy to Prometheus.
    Prometheus name: notifications_email_send_duration
    """
    return _lazy_meter().create_histogram(
        "notifications.email.send.duration",
        unit="ms",
        description="Mock email send latency per notification",
    )
