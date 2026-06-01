"""
Route tests for notification-svc FastAPI endpoints.
Uses fakeredis so no real Redis connection is needed.
"""

import pytest


def test_health_returns_200(client):
    resp = client.get("/healthz")
    assert resp.status_code == 200
    assert resp.json() == {"status": "healthy"}


def test_list_notifications_empty(client):
    resp = client.get("/notifications")
    assert resp.status_code == 200
    assert resp.json() == []


def test_list_notifications_returns_stored(client, fake_redis):
    notif_id = "notif-42"
    fake_redis.hset(
        f"notifications:{notif_id}",
        mapping={
            "id": notif_id,
            "order_id": "42",
            "project_id": "1",
            "message": "Order #42 created",
            "status": "sent",
            "created_at": "2026-01-01T00:00:00+00:00",
            "trace_id": "abc123",
        },
    )
    fake_redis.lpush("notification_ids", notif_id)

    resp = client.get("/notifications")
    assert resp.status_code == 200
    data = resp.json()
    assert len(data) == 1
    assert data[0]["id"] == notif_id
    assert data[0]["order_id"] == "42"


def test_list_notifications_limited_to_100(client, fake_redis):
    for i in range(150):
        nid = f"notif-{i}"
        fake_redis.hset(f"notifications:{nid}", mapping={"id": nid, "order_id": str(i)})
        fake_redis.lpush("notification_ids", nid)

    resp = client.get("/notifications")
    assert resp.status_code == 200
    assert len(resp.json()) == 100


def test_get_notification_found(client, fake_redis):
    notif_id = "notif-99"
    fake_redis.hset(
        f"notifications:{notif_id}",
        mapping={
            "id": notif_id,
            "order_id": "99",
            "project_id": "5",
            "message": "Test",
            "status": "sent",
            "created_at": "2026-01-01T00:00:00+00:00",
        },
    )

    resp = client.get(f"/notifications/{notif_id}")
    assert resp.status_code == 200
    assert resp.json()["id"] == notif_id
    assert resp.json()["order_id"] == "99"


def test_get_notification_not_found(client):
    resp = client.get("/notifications/notif-does-not-exist")
    assert resp.status_code == 404
    assert "not found" in resp.json()["detail"].lower()
