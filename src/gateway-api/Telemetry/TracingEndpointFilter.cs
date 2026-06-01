// ============================================================
// TracingEndpointFilter — per-endpoint child span
// ============================================================
// Minimal API equivalent of the MVC TracingActionFilter from the ACA
// implementation guidelines.
//
// Creates a child span named "endpoint.{method}" under every matched HTTP
// endpoint.  This is distinct from the ASP.NET Core auto-instrumented root span:
//
//   Root span (auto):  HTTP GET /api/projects/{id}
//     └─ Child span (this filter): endpoint.get
//          └─ Child span (manual): gateway.get_project
//               └─ EF Core span: db.mysql SELECT ...
//
// The child span lets you:
//   • Add endpoint-specific tags cleanly (route template, content-type)
//   • Measure pure handler latency (excluding ASP.NET routing/middleware overhead)
//   • Mirror the TracingActionFilter contract expected by the ACA standards
//
// Usage — apply to a route group or individual endpoints:
//   group.AddEndpointFilter<TracingEndpointFilter>();
//   app.MapPost("/api/orders", handler).AddEndpointFilter<TracingEndpointFilter>();
// ============================================================

using Microsoft.AspNetCore.Routing;
using System.Diagnostics;

namespace GatewayApi.Telemetry;

public class TracingEndpointFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var method = httpContext.Request.Method.ToLowerInvariant();

        // Raw route template (e.g. "/api/projects/{id}") — available after routing resolves.
        var routeTemplate = (httpContext.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                            ?? httpContext.Request.Path.Value;

        using var activity = DiagnosticsConfig.ActivitySource.StartActivity($"endpoint.{method}");

        if (activity is not null)
        {
            activity.SetTag("http.route.template", routeTemplate);
            activity.SetTag("http.method", httpContext.Request.Method);
            // http.target: full path + query string (useful for debugging)
            activity.SetTag("http.target",
                httpContext.Request.Path.Value + httpContext.Request.QueryString.Value);
        }

        return await next(context);
    }
}
