using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OrderApi.Telemetry;

public static class DiagnosticsConfig
{
    public const string ServiceName = "order-api";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    // No project_id dimension on any instrument below — project_id is
    // unbounded (grows with every project ever created), and this project's
    // own engineering principles flag unbounded/high-churn labels as an
    // automatic stop for metrics. Per-project drill-down uses the
    // order.project_id span attribute + trace-based exemplars instead, which
    // can safely carry high-cardinality IDs. Keep it that way — don't add a
    // project_id tag back onto these without a relabel/drop rule or a stated
    // cardinality bound.
    public static readonly Counter<long> OrdersCreated =
        Meter.CreateCounter<long>(
            "orders.created.total",
            unit: "{order}",
            description: "Total number of orders created");

    // Counter<double>: OTel's Counter<T> only supports long/double anyway, so this
    // was never going to be decimal-precise — same accepted lab-scale tradeoff as
    // the proto's `amount` field (see orders.proto). Metric aggregation doesn't
    // need decimal precision; the DB row (Order.Amount) is the source of truth
    // and is already `decimal`.
    public static readonly Counter<double> OrdersAmount =
        Meter.CreateCounter<double>(
            "orders.amount.total",
            unit: "USD",
            description: "Running total order value");

    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>(
            "orders.processing.duration",
            unit: "ms",
            // Stopwatch stops after the Order + OutboxMessage database commit.
            // Broker delivery is asynchronous work measured by relay tracing, not
            // part of the user-facing CreateOrder RPC latency.
            description: "Time to persist an order and its outbox message");
}
