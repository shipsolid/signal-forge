"""
RabbitMQ consumer with W3C trace context extraction.

This module is the Python side of the cross-language async trace propagation:
  .NET order-api (PRODUCER) → RabbitMQ → Python notification-svc (CONSUMER)

The same trace ID that started in the Angular browser ends up on the
notification.process span here, producing a 5-hop trace that spans:
  Browser → gateway-api → order-api → RabbitMQ → notification-svc

Key design decisions:
  1. Manual extraction vs opentelemetry-instrumentation-pika
     We use manual W3C extraction rather than the pika auto-instrumentation.
     The pika instrumentation creates its own span but does not correctly
     link the incoming message's trace context to it in all versions.
     Manual extraction gives us full control over the span kind (CONSUMER),
     the link relationship to the producer span, and the span attributes.

  2. SpanLink vs parent-child
     The CONSUMER span uses a *link* to the PRODUCER span rather than making
     the producer span its parent. This matches the OTel messaging semantic
     conventions: async operations that may be processed much later (or by a
     different instance) should use links, not parent-child relationships.
     In Jaeger, a linked span is shown as a dashed arrow rather than a solid
     parent line, making the async nature visually clear.

  3. Context attach/detach
     We attach the extracted context to the current thread with attach() so
     that child spans (Redis, email mock) automatically become children of
     the CONSUMER span without needing to pass context manually.
     The detach(token) call in the finally block ensures context cleanup
     even if an exception is raised.

Validation target (spec §11.1):
  "Async propagation (critical): order-api PRODUCER span → RabbitMQ →
   notification-svc CONSUMER span share the same trace, linked via
   message headers."
"""

import json
import logging
import os
import random
import threading
import time
from datetime import datetime, timezone

import pika
from opentelemetry import trace
from opentelemetry.context import attach, detach
from opentelemetry.propagate import extract
from opentelemetry.trace import Link, SpanKind

from app.models import OrderCreatedEvent
from app.redis_client import get_redis
from app.telemetry import (
    email_send_duration_histogram,
    get_tracer,
    notifications_processed_counter,
    processing_duration_histogram,
)

logger = logging.getLogger(__name__)

EXCHANGE = "orders"
QUEUE = "notifications"
ROUTING_KEY = "order.created"

# Dead-letter exchange: RabbitMQ automatically routes NACK'd (requeue=False)
# messages here so nothing is silently dropped.
DLQ_EXCHANGE = "orders.dlq"
DLQ_QUEUE = "notifications.dlq"

# Instruments are module-level singletons, lazily initialised after
# setup_telemetry() has been called in main.py.
_counter = None
_proc_hist = None
_email_hist = None
_instruments_lock = threading.Lock()


def _instruments():
    """Lazily initialise metric instruments (requires MeterProvider to be set)."""
    global _counter, _proc_hist, _email_hist
    with _instruments_lock:
        if _counter is None:
            _counter = notifications_processed_counter()
            _proc_hist = processing_duration_histogram()
            _email_hist = email_send_duration_histogram()
    return _counter, _proc_hist, _email_hist


class HeadersGetter:
    """
    TextMapPropagator getter for RabbitMQ BasicProperties.headers dict.

    RabbitMQ delivers header values as bytes when the producer wrote them
    as bytes (which the .NET OrderPublisher does).  This getter decodes
    them back to str so the W3C propagator can parse them.
    """

    def get(self, carrier: dict, key: str) -> list[str]:
        val = carrier.get(key)
        if val is None:
            return []
        if isinstance(val, bytes):
            return [val.decode("utf-8")]
        return [str(val)]

    def keys(self, carrier: dict) -> list[str]:
        return list(carrier.keys()) if carrier else []


_getter = HeadersGetter()


def handle_order_created(ch, method, properties, body: bytes) -> None:
    """
    pika basic_consume callback — called for each message on the queue.

    Execution flow:
      1. Extract W3C traceparent from message headers
      2. Attach context → start CONSUMER span (linked to PRODUCER)
      3. Parse message body as OrderCreatedEvent
      4. Redis dedup check (idempotency key = order_id)
      5. Store notification record in Redis hash + ID list
      6. Mock email send (custom span with artificial latency)
      7. Record metrics
      8. ACK or NACK the message

    Idempotency: RabbitMQ may deliver a message more than once if the
    consumer crashes after processing but before ACKing.  The Redis dedup
    key (TTL 1h) guards against duplicate notifications.
    """
    start_ms = time.time() * 1000
    counter, proc_hist, email_hist = _instruments()

    # ── Step 1: Extract trace context ────────────────────────────────────────
    # props.headers is a dict[str, Any] where values may be bytes (as set by
    # the .NET producer) or str.  Our HeadersGetter handles both.
    headers = properties.headers or {}
    ctx = extract(headers, getter=_getter)

    # Initialise token before attach so the finally-block detach is always safe,
    # even if an exception is raised between here and the attach() call.
    token = None
    # Attach context to current thread so child spans (Redis, email) inherit it.
    token = attach(ctx)

    tracer = get_tracer()

    # Build a SpanLink to the producer span's context.
    # Using Link rather than parent preserves the async semantic:
    # the CONSUMER span may start long after the PRODUCER span ended.
    parent_span_ctx = trace.get_current_span(ctx).get_span_context()
    links = [Link(parent_span_ctx)] if parent_span_ctx.is_valid else []

    try:
        # ── Step 2: CONSUMER span ────────────────────────────────────────────
        # SpanKind.CONSUMER is the OTel messaging convention for message receivers.
        # The span covers the full processing lifecycle: from pika delivery to ACK.
        with tracer.start_as_current_span(
            "notification.process",
            kind=SpanKind.CONSUMER,
            links=links,
        ) as span:
            # OTel messaging semantic convention attributes (semconv 1.24)
            span.set_attribute("messaging.system", "rabbitmq")
            span.set_attribute("messaging.operation", "receive")
            span.set_attribute("messaging.source.name", EXCHANGE)
            span.set_attribute("messaging.rabbitmq.routing_key", ROUTING_KEY)

            # ── Step 3: Parse message body ───────────────────────────────────
            event = OrderCreatedEvent(**json.loads(body))
            span.set_attribute("order.id", event.order_id)
            span.set_attribute("order.project_id", event.project_id)

            # ── Step 4: Redis dedup ──────────────────────────────────────────
            # The Redis instrumentation (RedisInstrumentation().instrument())
            # called in redis_client.py automatically creates child spans for
            # every Redis command with db.system=redis, db.statement=<command>.
            r = get_redis()
            dedup_key = f"dedup:{event.order_id}"
            if r.exists(dedup_key):
                logger.info("Duplicate notification skipped for order %d", event.order_id)
                counter.add(1, {"status": "duplicate"})
                ch.basic_ack(delivery_tag=method.delivery_tag)
                return

            # ── Step 5: Store in Redis ───────────────────────────────────────
            notification_id = f"notif-{event.order_id}"
            message = (
                f"Order #{event.order_id} created for project {event.project_id}: "
                f"{event.description} — ${event.amount:.2f}"
            )
            # Include the trace ID in the stored record so the REST API
            # response surfaces it for the Angular Notifications page.
            current_trace_id = format(span.get_span_context().trace_id, "032x")

            r.hset(
                f"notifications:{notification_id}",
                mapping={
                    "id": notification_id,
                    "order_id": event.order_id,
                    "project_id": event.project_id,
                    "message": message,
                    "status": "sent",
                    "created_at": datetime.now(timezone.utc).isoformat(),
                    "trace_id": current_trace_id,
                },
            )
            r.expire(f"notifications:{notification_id}", 86400)  # 24h TTL
            r.set(dedup_key, "1", ex=3600)  # 1h dedup window

            # Maintain a capped list of recent notification IDs.
            # LPUSH + LTRIM = O(1) insert + O(N) trim, but with N=1000 and
            # access only at read time this is acceptable for a lab.
            r.lpush("notification_ids", notification_id)
            r.ltrim("notification_ids", 0, 999)  # keep last 1000

            # ── Step 6: Mock email send ──────────────────────────────────────
            _mock_email_send(email_hist, event.order_id)

            # ── Step 7: Record metrics ───────────────────────────────────────
            elapsed = time.time() * 1000 - start_ms
            proc_hist.record(elapsed)
            counter.add(1, {"status": "success"})

            logger.info(
                "Processed notification for order %d. TraceId: %s", event.order_id, current_trace_id
            )
            # ── Step 8: ACK ─────────────────────────────────────────────────
            ch.basic_ack(delivery_tag=method.delivery_tag)

    except Exception:
        logger.exception("Failed to process order.created event")
        counter.add(1, {"status": "failed"})
        # NACK without requeue: RabbitMQ routes the message to DLQ_EXCHANGE
        # (configured via x-dead-letter-exchange on queue declaration below)
        # so failed messages are preserved for inspection rather than dropped.
        ch.basic_nack(delivery_tag=method.delivery_tag, requeue=False)
    finally:
        # CRITICAL: detach the context even on error to prevent context leak.
        # Forgetting detach() on the thread would corrupt the context for the
        # next message delivery on the same thread.
        # Guard: token is None if attach() was never reached (e.g. extraction raised).
        if token is not None:
            detach(token)


def _mock_email_send(email_hist, order_id: int) -> None:
    """
    Simulates an external email API call with a random 100-500ms delay.

    Wrapped in a custom span to validate:
      • Child span within a CONSUMER parent
      • Artificial latency visible in the trace waterfall
      • email.send.duration histogram (per spec §3.4 custom instruments)

    In production this would be an actual HTTP call to SendGrid/SES/etc,
    instrumented by AddHttpClientInstrumentation() or the requests-otel library.
    """
    tracer = get_tracer()
    with tracer.start_as_current_span("notification.send_email") as span:
        delay_ms = random.randint(100, 500)
        span.set_attribute("email.order_id", order_id)
        span.set_attribute("email.delay_ms", delay_ms)
        time.sleep(delay_ms / 1000)
        email_hist.record(delay_ms)
        logger.info("Mock email sent for order %d (simulated %dms)", order_id, delay_ms)


def start_consumer() -> None:
    """
    Connect to RabbitMQ and start blocking consumption.

    Called in a daemon thread from main.py lifespan so it doesn't
    block the FastAPI event loop.  The caller wraps this in a
    retry loop — if RabbitMQ is temporarily unavailable the thread
    sleeps and reconnects automatically.
    """
    host = os.getenv("RABBITMQ_HOST", "rabbitmq")
    port = int(os.getenv("RABBITMQ_PORT", "5672"))
    user = os.getenv("RABBITMQ_USER")
    password = os.getenv("RABBITMQ_PASSWORD")
    if not user or not password:
        raise ValueError("RABBITMQ_USER and RABBITMQ_PASSWORD environment variables are required.")

    params = pika.ConnectionParameters(
        host=host,
        port=port,
        credentials=pika.PlainCredentials(user, password),
        # 30s heartbeat detects dead TCP connections 2× faster than the 60s default.
        heartbeat=30,
        # Raise after 60s if the broker blocks (memory/disk alarm) so the retry
        # loop reconnects rather than hanging indefinitely.
        blocked_connection_timeout=60,
    )

    connection = pika.BlockingConnection(params)
    channel = connection.channel()

    # Idempotent declarations — safe to call on every reconnect.
    # DLQ setup: declare the dead-letter exchange + queue first, then bind
    # the main queue to it via x-dead-letter-exchange.  Any message that is
    # NACK'd with requeue=False is automatically routed to DLQ_QUEUE.
    channel.exchange_declare(DLQ_EXCHANGE, exchange_type="fanout", durable=True)
    channel.queue_declare(DLQ_QUEUE, durable=True)
    channel.queue_bind(DLQ_QUEUE, DLQ_EXCHANGE, "")

    channel.exchange_declare(EXCHANGE, exchange_type="topic", durable=True)
    channel.queue_declare(
        QUEUE,
        durable=True,
        arguments={"x-dead-letter-exchange": DLQ_EXCHANGE},
    )
    channel.queue_bind(QUEUE, EXCHANGE, ROUTING_KEY)

    # prefetch_count=1: process one message at a time.
    # This provides back-pressure and ensures that if processing is slow
    # (e.g. mock email taking 500ms), we don't accumulate a large in-memory
    # backlog of unacked messages.
    channel.basic_qos(prefetch_count=1)
    channel.basic_consume(QUEUE, on_message_callback=handle_order_created)

    logger.info("RabbitMQ consumer started on queue '%s'", QUEUE)
    channel.start_consuming()
