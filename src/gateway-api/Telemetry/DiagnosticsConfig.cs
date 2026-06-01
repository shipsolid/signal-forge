// ============================================================
// DiagnosticsConfig — gateway-api custom OTel instruments
// ============================================================
// Centralises all ActivitySource and Meter definitions so every
// part of the codebase refers to the same instances.
//
// Design notes:
//   • ActivitySource name = service name. This is the value that appears as
//     "instrumentation library" in Jaeger / Grafana Tempo. Keeping it the
//     same as OTEL_SERVICE_NAME makes queries intuitive.
//   • Meter name = service name. Same reasoning — Prometheus labels include
//     otel_scope_name by default; matching service.name reduces join
//     complexity in PromQL.
//   • Instruments use the OTel semantic conventions where applicable:
//     - UpDownCounter with unit "{request}" matches the HTTP active-requests
//       semantic convention (http.server.active_requests).
//     - Histogram unit is "ms" (milliseconds). Alternative: "s" (seconds).
//       We use ms here because the span metrics connector also uses ms buckets,
//       making it easier to compare against RED metrics from traces.
// ============================================================

using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace GatewayApi.Telemetry;

public static class DiagnosticsConfig
{
    // Must match OTEL_SERVICE_NAME env var so manual spans appear under the
    // same service as auto-instrumented spans.
    public const string ServiceName = "gateway-api";

    // ActivitySource is the .NET equivalent of an OTel Tracer.
    // Every span created with this source carries instrumentationLibrary.name = "gateway-api".
    public static readonly ActivitySource ActivitySource = new(ServiceName);

    // Meter is the .NET equivalent of an OTel Meter.
    // It must be registered via AddMeter(ServiceName) in the SDK setup.
    public static readonly Meter Meter = new(ServiceName);

    // ── Instruments ───────────────────────────────────────────────────────────

    /// <summary>
    /// Real-time count of requests being processed concurrently.
    /// Reported as a gauge (UpDownCounter). Incremented on request entry,
    /// decremented on completion (success or error).
    ///
    /// Prometheus metric name: gateway_requests_inflight
    /// (dots in OTel metric names become underscores in Prometheus).
    ///
    /// Validation target: "Inflight Requests" stat panel in Service Overview dashboard.
    /// </summary>
    public static readonly UpDownCounter<long> InflightRequests =
        Meter.CreateUpDownCounter<long>(
            "gateway.requests.inflight",
            unit: "{request}",
            description: "Number of concurrent in-flight requests");

    /// <summary>
    /// Latency of calls to downstream services, labeled by service name
    /// and operation. Recorded after every gRPC and HTTP fan-out call.
    ///
    /// Prometheus metric name: gateway_downstream_duration
    ///
    /// KEY FEATURE: When ExemplarFilterType.TraceBased is configured on the
    /// MeterProvider, each histogram observation that occurs within an active
    /// sampled span automatically attaches the traceId + spanId as an exemplar.
    /// In Grafana, enabling the "Exemplars" toggle on this panel shows scatter
    /// dots on the histogram that link directly to the causing trace in Jaeger.
    ///
    /// Validation target: "gateway.downstream.duration" histogram present in
    /// Prometheus; exemplar dots visible and clickable in Grafana.
    /// </summary>
    public static readonly Histogram<double> DownstreamDuration =
        Meter.CreateHistogram<double>(
            "gateway.downstream.duration",
            unit: "ms",
            description: "Latency of downstream service calls per service and operation");
}
