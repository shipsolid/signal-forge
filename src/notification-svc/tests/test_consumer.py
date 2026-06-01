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
from app.consumer import handle_order_created

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


def test_redis_failure_nacks_to_dlq(mock_instruments):
    ch, method, props = _pika_args(delivery_tag=2)
    exploding_redis = MagicMock()
    exploding_redis.exists.side_effect = RuntimeError("Redis down")

    with (
        patch("app.consumer.get_redis", return_value=exploding_redis),
        patch("app.consumer._instruments", return_value=mock_instruments),
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
