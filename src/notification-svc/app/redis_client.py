import logging
import os

import redis

_client: redis.Redis | None = None
logger = logging.getLogger(__name__)


def get_redis() -> redis.Redis:
    """
    Return the singleton Redis client, reconnecting automatically if the
    connection has gone stale (e.g. after a Redis pod restart).

    RedisInstrumentation is registered once in setup_telemetry() (telemetry.py),
    not here — instrumentation must be attached to the global TracerProvider
    before the first client call, and setup_telemetry() guarantees that ordering.
    """
    global _client
    if _client is None:
        host = os.getenv("REDIS_HOST", "redis")
        port = int(os.getenv("REDIS_PORT", "6379"))
        _client = redis.Redis(
            host=host,
            port=port,
            decode_responses=True,
            socket_connect_timeout=5,
            socket_timeout=5,  # read/write timeout per command
            socket_keepalive=True,
            health_check_interval=30,
        )

    try:
        _client.ping()
    except redis.ConnectionError:
        logger.warning("Redis connection lost, reconnecting")
        _client = None
        return get_redis()

    return _client
