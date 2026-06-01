using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace OrderApi.Telemetry;

public static class DiagnosticsConfig
{
    public const string ServiceName = "order-api";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    public static readonly Counter<long> OrdersCreated =
        Meter.CreateCounter<long>(
            "orders.created.total",
            unit: "{order}",
            description: "Total number of orders created, by project");

    public static readonly Counter<double> OrdersAmount =
        Meter.CreateCounter<double>(
            "orders.amount.total",
            unit: "USD",
            description: "Running total order value");

    public static readonly Histogram<double> ProcessingDuration =
        Meter.CreateHistogram<double>(
            "orders.processing.duration",
            unit: "ms",
            description: "Time from order create to RabbitMQ publish");
}
