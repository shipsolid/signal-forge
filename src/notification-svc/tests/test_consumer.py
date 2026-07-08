"""
Unit tests for handle_order_created consumer callback.

Dependencies are fully mocked: fakeredis for Redis, MagicMock for pika
channel/method/properties, and MagicMock for OTel metric instruments.
No real RabbitMQ or Redis connection is required.
"""

import json
from unittest.mock import MagicMock, patch

import fakeredis
import pytest
import redis
from app.consumer import handle_order_created
from opentelemetry.sdk.trace import TracerProvider
from opentelemetry.sdk.trace.export import SimpleSpanProcessor
from opentelemetry.sdk.trace.export.in_memory_span_exporter import InMemorySpanExporter

# ── Helpers ───────────────────────────────────────────────────────────────────


def _body(order_id=1, project_id=2, description="Test order", amount=99.99):
    return json.dumps(
        {
            "order_id": order_id,
            "project_id": project_id,
            "description": description,
            "amount": amount,
            "created_at": "2026-01-01T00:00:00+00:00",
        }
    ).encode()


def _pika_args(headers=None, delivery_tag=1):
    ch = MagicMock()
    method = MagicMock()
    method.delivery_tag = delivery_tag
    props = MagicMock()
    props.headers = headers or {}
    return ch, method, props


@pytest.fixture
def fr():
    return fakeredis.FakeRedis(decode_responses=True)


@pytest.fixture
def mock_instruments():
    return MagicMock(), MagicMock(), MagicMock()


@pytest.fixture
def captured_spans():
    """A standalone TracerProvider + in-memory exporter, patched in as
    app.consumer's tracer for one test, so span attributes can be asserted on
    without touching the session-scoped provider other tests share."""
    exporter = InMemorySpanExporter()
    provider = TracerProvider()
    provider.add_span_processor(SimpleSpanProcessor(exporter))
    tracer = provider.get_tracer("test")

    with patch("app.consumer.get_tracer", return_value=tracer):
        yield exporter


# ── Happy path ────────────────────────────────────────────────────────────────


def test_happy_path_acks_message(fr, mock_instruments):
    ch, method, props = _pika_args(delivery_tag=7)

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=42))

    ch.basic_ack.assert_called_once_with(delivery_tag=7)
    ch.basic_nack.assert_not_called()


def test_happy_path_stores_notification_hash(fr, mock_instruments):
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=42))

    assert fr.exists("notifications:notif-42")
    stored = fr.hgetall("notifications:notif-42")
    assert stored["order_id"] == "42"
    assert stored["project_id"] == "2"
    assert stored["status"] == "sent"
    assert "message" in stored
    assert "created_at" in stored


def test_happy_path_pushes_to_notification_ids_list(fr, mock_instruments):
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=42))

    assert "notif-42" in fr.lrange("notification_ids", 0, -1)


def test_happy_path_sets_dedup_key_with_ttl(fr, mock_instruments):
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=5))

    assert fr.exists("dedup:5")
    assert 0 < fr.ttl("dedup:5") <= 3600


def test_happy_path_sets_notification_ttl(fr, mock_instruments):
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=3))

    assert 0 < fr.ttl("notifications:notif-3") <= 86400


def test_happy_path_increments_success_counter(fr, mock_instruments):
    counter, proc_hist, _ = mock_instruments
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=10))

    counter.add.assert_called_with(1, {"status": "success"})


# ── Deduplication ─────────────────────────────────────────────────────────────


def test_duplicate_message_skips_storage_and_acks(fr, mock_instruments):
    fr.set("dedup:42", "1", ex=3600)
    ch, method, props = _pika_args(delivery_tag=3)

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, _body(order_id=42))

    ch.basic_ack.assert_called_once_with(delivery_tag=3)
    ch.basic_nack.assert_not_called()
    assert not fr.exists("notifications:notif-42")


def test_duplicate_message_sets_span_attribute(fr, mock_instruments, captured_spans):
    fr.set("dedup:42", "1", ex=3600)
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, _body(order_id=42))

    spans = captured_spans.get_finished_spans()
    assert len(spans) == 1
    assert spans[0].attributes["notification.duplicate"] is True


def test_duplicate_message_increments_duplicate_counter(fr, mock_instruments):
    counter, _, _ = mock_instruments
    fr.set("dedup:42", "1", ex=3600)
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, _body(order_id=42))

    counter.add.assert_called_with(1, {"status": "duplicate"})


def test_dedup_uses_atomic_set_nx_not_exists(mock_instruments):
    """
    Regression guard for the TOCTOU race: dedup must be a single `SET ... NX` call,
    not a separate exists() check followed by a later set(). Two near-simultaneous
    deliveries of the same order_id can both pass a non-atomic exists() check before
    either sets the key; SET NX closes that window by making the check-and-set one
    round trip Redis serializes.
    """
    mock_redis = MagicMock()
    mock_redis.set.return_value = True  # NX succeeds — not a duplicate
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=mock_redis),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=77))

    mock_redis.exists.assert_not_called()
    mock_redis.set.assert_any_call("dedup:77", "1", nx=True, ex=3600)


def test_second_of_two_rapid_deliveries_is_deduped(fr, mock_instruments):
    ch1, method1, props1 = _pika_args(delivery_tag=1)
    ch2, method2, props2 = _pika_args(delivery_tag=2)

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch1, method1, props1, _body(order_id=88))
        handle_order_created(ch2, method2, props2, _body(order_id=88))

    ch1.basic_ack.assert_called_once_with(delivery_tag=1)
    ch2.basic_ack.assert_called_once_with(delivery_tag=2)
    # Only the first delivery's write should have happened.
    assert fr.hget("notifications:notif-88", "status") == "sent"


# ── Error handling ────────────────────────────────────────────────────────────


def test_invalid_json_nacks_to_dlq(fr, mock_instruments):
    ch, method, props = _pika_args(delivery_tag=9)

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, b"not-valid-json")

    ch.basic_nack.assert_called_once_with(delivery_tag=9, requeue=False)
    ch.basic_ack.assert_not_called()


def test_invalid_json_increments_failed_counter(fr, mock_instruments):
    counter, _, _ = mock_instruments
    ch, method, props = _pika_args()

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, b"bad")

    counter.add.assert_called_with(1, {"status": "failed"})


def test_redis_failure_requeues_instead_of_dlq(mock_instruments):
    """
    A Redis outage (e.g. pod restart during a routine deploy) is transient,
    not a bad message — it must be requeued for retry, not dead-lettered.
    Dead-lettering live traffic on every Redis blip was the original bug.
    """
    ch, method, props = _pika_args(delivery_tag=2)
    exploding_redis = MagicMock()
    exploding_redis.set.side_effect = redis.ConnectionError("Redis down")

    with (
        patch("app.consumer.get_redis", return_value=exploding_redis),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, _body(order_id=1))

    ch.basic_nack.assert_called_once_with(delivery_tag=2, requeue=True)
    ch.basic_ack.assert_not_called()


def test_redis_failure_increments_transient_failure_counter(mock_instruments):
    counter, _, _ = mock_instruments
    ch, method, props = _pika_args()
    exploding_redis = MagicMock()
    exploding_redis.set.side_effect = redis.ConnectionError("Redis down")

    with (
        patch("app.consumer.get_redis", return_value=exploding_redis),
        patch("app.consumer._instruments", return_value=mock_instruments),
    ):
        handle_order_created(ch, method, props, _body(order_id=1))

    counter.add.assert_called_with(1, {"status": "failed_transient"})


def test_unexpected_error_nacks_to_dlq(fr, mock_instruments):
    """Non-Redis, non-validation errors still go to the DLQ (not requeued
    indefinitely) since we can't assume they're safe to retry."""
    ch, method, props = _pika_args(delivery_tag=2)

    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send", side_effect=RuntimeError("boom")),
    ):
        handle_order_created(ch, method, props, _body(order_id=1))

    ch.basic_nack.assert_called_once_with(delivery_tag=2, requeue=False)
    ch.basic_ack.assert_not_called()


# ── Notification IDs list capping ─────────────────────────────────────────────


def test_notification_ids_list_capped_at_1000(fr, mock_instruments):
    for i in range(1000):
        fr.rpush("notification_ids", f"notif-old-{i}")

    ch, method, props = _pika_args()
    with (
        patch("app.consumer.get_redis", return_value=fr),
        patch("app.consumer._instruments", return_value=mock_instruments),
        patch("app.consumer._mock_email_send"),
    ):
        handle_order_created(ch, method, props, _body(order_id=9999))

    assert fr.llen("notification_ids") == 1000
