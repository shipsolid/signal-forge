import sys
import types
from unittest.mock import MagicMock, patch

import fakeredis
import pytest
from opentelemetry import trace
from opentelemetry.sdk.trace import TracerProvider

# Stub out app.telemetry before any app module is imported.
# The OTLP/gRPC exporter pulls in protobuf C extensions that are
# incompatible with Python 3.14's metaclass changes; tests don't
# need real exporters so we replace the whole module with a mock.
_telemetry_stub = types.ModuleType("app.telemetry")
# Return the real no-op tracer so span.get_span_context().trace_id is an int,
# not a MagicMock — format(MagicMock(), "032x") raises TypeError on Python 3.14.
_telemetry_stub.get_tracer = lambda name="": trace.get_tracer(name)
_telemetry_stub.notifications_processed_counter = MagicMock()
_telemetry_stub.processing_duration_histogram = MagicMock()
_telemetry_stub.email_send_duration_histogram = MagicMock()
_telemetry_stub.setup_telemetry = MagicMock()
sys.modules.setdefault("app.telemetry", _telemetry_stub)


@pytest.fixture(scope="session", autouse=True)
def setup_otel():
    """Minimal no-export OTel provider so spans don't fail in tests."""
    trace.set_tracer_provider(TracerProvider())


@pytest.fixture
def fake_redis():
    return fakeredis.FakeRedis(decode_responses=True)


@pytest.fixture
def mock_instruments():
    counter = MagicMock()
    proc_hist = MagicMock()
    email_hist = MagicMock()
    return counter, proc_hist, email_hist


@pytest.fixture
def client(fake_redis):
    with (
        patch("app.main._consumer_loop"),
        patch("app.redis_client.get_redis", return_value=fake_redis),
    ):
        from app.main import app

        with TestClient(app) as c:
            yield c


from fastapi.testclient import TestClient
