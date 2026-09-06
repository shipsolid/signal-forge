"""
FastAPI application for notification-svc.

Startup sequence (order matters for OTel correctness):
  1. Configure structured JSON logging (before OTel so logger exists)
  2. Call setup_telemetry() — installs global TracerProvider + MeterProvider
  3. Create FastAPI app — FastAPIInstrumentor wraps it immediately
  4. Lifespan: start RabbitMQ consumer thread on startup

REST endpoints:
  GET /notifications      — list last 100 notifications from Redis
  GET /notifications/{id} — get a single notification by ID
  GET /healthz            — process liveness probe (excluded from traces)
  GET /readyz             — consumer/Redis dependency readiness probe

The RabbitMQ consumer runs in a background daemon thread.  It blocks on
pika's start_consuming() loop, which is incompatible with asyncio, hence
the thread rather than an async task.  The _consumer_loop() wrapper retries
on connection errors so transient RabbitMQ restarts are handled gracefully.
"""

import logging
import threading
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException
from opentelemetry.instrumentation.fastapi import FastAPIInstrumentor
from pythonjsonlogger import jsonlogger

from app.consumer import is_connected, start_consumer
from app.redis_client import get_redis
from app.telemetry import setup_telemetry

# ── Structured JSON logging ───────────────────────────────────────────────────
# MUST be configured before setup_telemetry() so the LoggingInstrumentation
# (called inside setup_telemetry) sees the handler and can inject OTel fields.
#
# pythonjsonlogger.JsonFormatter writes one JSON object per log line.
# The format string determines which LogRecord fields appear as JSON keys.
# After LoggingInstrumentation is applied, these additional fields are added:
#   otelTraceID  — 32-char hex trace ID of the active span
#   otelSpanID   — 16-char hex span ID
#   otelServiceName — "notification-svc"
#
# Alloy's loki.process "trace_correlation" stage extracts otelTraceID and
# otelSpanID from these JSON lines (alongside TraceId/SpanId for .NET services)
# and promotes them as Loki structured metadata for "Logs for this span".
handler = logging.StreamHandler()
handler.setFormatter(
    jsonlogger.JsonFormatter(
        "%(asctime)s %(name)s %(levelname)s %(message)s %(otelTraceID)s %(otelSpanID)s"
    )
)
logging.basicConfig(level=logging.INFO, handlers=[handler])
logger = logging.getLogger(__name__)

# ── OTel setup ────────────────────────────────────────────────────────────────
# Must happen BEFORE the FastAPI app is created so that FastAPIInstrumentor
# has an active TracerProvider to register with.
setup_telemetry()


# ── Lifespan ──────────────────────────────────────────────────────────────────
@asynccontextmanager
async def lifespan(app: FastAPI):
    """Start background consumer thread on startup; log shutdown."""
    t = threading.Thread(target=_consumer_loop, daemon=True, name="rabbitmq-consumer")
    t.start()
    logger.info("Notification service started, consumer thread running")
    yield
    # FastAPI calls this on SIGTERM / KeyboardInterrupt. The daemon thread exits
    # with the process; this lab deliberately does not call stop_consuming() or
    # flush telemetry. Kubernetes may redeliver an unacknowledged message after
    # termination, which is why the consumer's Redis deduplication is required.
    logger.info("Notification service shutting down")


def _consumer_loop() -> None:
    """
    Wrapper that calls start_consumer() in a retry loop with exponential backoff.

    start_consumer() blocks on pika's start_consuming() until the connection
    drops or an exception is raised.  When that happens, we sleep with
    exponential backoff (5s → 10s → 20s … capped at 300s) before reconnecting.
    This handles:
      • RabbitMQ not yet ready on pod startup (race with readinessProbe)
      • Transient network errors or broker restarts
      • The "Scale to 0 → Scale back up" resilience scenario in spec §11.10

    A successful connection resets the backoff so the next failure starts fresh.
    """
    base_delay = 5
    max_delay = 300
    attempt = 0

    while True:
        try:
            start_consumer()
            # start_consumer() returned cleanly (shouldn't happen in normal
            # operation, but treat it the same as a crash and reconnect).
            attempt = 0
        except Exception:
            attempt += 1
            delay = min(base_delay * (2 ** (attempt - 1)), max_delay)
            logger.error(
                "Consumer crashed (attempt %d), restarting in %ds",
                attempt,
                delay,
                exc_info=True,
            )
            if delay >= max_delay:
                logger.critical(
                    "RabbitMQ consumer has failed %d consecutive times and is at max backoff (%ds). "
                    "Manual intervention may be required.",
                    attempt,
                    max_delay,
                )
            time.sleep(delay)


# ── FastAPI app ───────────────────────────────────────────────────────────────
app = FastAPI(title="notification-svc", version="1.0.0", lifespan=lifespan)

# FastAPIInstrumentor wraps every route handler in an OTel server span.
# excluded_urls is a regex — /healthz is excluded so kubelet probes don't
# create traces (the filter in Alloy is a second layer of protection).
FastAPIInstrumentor().instrument_app(app, excluded_urls="/healthz")


# ── Endpoints ─────────────────────────────────────────────────────────────────


@app.get("/healthz", include_in_schema=False)
def health():
    """
    Liveness probe — no OTel span, no Loki log.

    Deliberately just "is the process alive": a k8s liveness failure restarts the
    pod, which is the wrong response to "RabbitMQ is unreachable" or "Redis is
    down" — those are readiness concerns (see /readyz), not process-health ones.
    """
    return {"status": "healthy"}


@app.get("/readyz", include_in_schema=False)
def ready():
    """
    Readiness probe — reflects actual consumer/Redis state, unlike /healthz.

    A stuck consumer (backed off after repeated RabbitMQ connection failures,
    see consumer.py's _consumer_loop) or an unreachable Redis previously still
    reported healthy on both probes, so k8s kept routing traffic to a pod doing
    no useful work. Failing readiness here pulls the pod from service rotation
    without restarting it — restarting wouldn't fix an external dependency outage.
    """
    if not is_connected():
        raise HTTPException(status_code=503, detail="RabbitMQ consumer not connected")
    try:
        get_redis().ping()
    except Exception as exc:
        raise HTTPException(status_code=503, detail="Redis unreachable") from exc
    return {"status": "ready"}


@app.get("/notifications")
def list_notifications():
    """
    Return the most recent 100 notifications stored in Redis.

    Storage pattern:
      • "notification_ids" is a Redis list acting as a capped queue
        (LPUSH new IDs + LTRIM to 1000).
      • Each notification is stored as a Redis hash at
        "notifications:{id}" with TTL 24h.

    OTel: Redis GET and LRANGE commands appear as child spans under the FastAPI
    server span. RedisInstrumentation is registered once by
    telemetry.py::setup_telemetry before routes begin serving traffic.
    """
    r = get_redis()
    # LRANGE 0 99 = first 100 elements (most recent, since we LPUSH).
    ids = r.lrange("notification_ids", 0, 99)
    notifications = []
    for nid in ids:
        data = r.hgetall(f"notifications:{nid}")
        if data:
            notifications.append(data)
    logger.info("Listed %d notifications", len(notifications))
    return notifications


@app.get("/notifications/{notification_id}")
def get_notification(notification_id: str):
    """
    Return a single notification by its ID.

    Returns 404 if the ID is not found or if the 24h TTL has expired.
    """
    r = get_redis()
    data = r.hgetall(f"notifications:{notification_id}")
    if not data:
        raise HTTPException(status_code=404, detail="Notification not found")
    logger.info("Retrieved notification %s", notification_id)
    return data
