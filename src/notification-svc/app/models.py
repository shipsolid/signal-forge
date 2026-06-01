from pydantic import BaseModel


class OrderCreatedEvent(BaseModel):
    order_id: int
    project_id: int
    description: str
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
