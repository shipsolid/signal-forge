from pydantic import BaseModel


class OrderCreatedEvent(BaseModel):
    order_id: int
    project_id: int
    description: str
    # float mirrors the wire payload order-api's OutboxRelayWorker serializes
    # (itself matching orders.proto's double amount — see that file's comment).
    # Accepted lab-scale tradeoff, not an oversight; this service only reads
    # the value for display, never for ledger-accurate arithmetic.
    amount: float
    created_at: str


class Notification(BaseModel):
    id: str
    order_id: int
    project_id: int
    message: str
    status: str
    created_at: str
    trace_id: str | None = None
