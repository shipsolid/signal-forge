namespace GatewayApi.Endpoints;

// Forwards caller-identity headers from the inbound browser request onto the
// outbound gRPC call to order-api, so order-api's own EnrichWithHttpRequest
// callback (which reads these same header names) can tag its spans with them.
// HTTP headers don't auto-forward into gRPC calls — this has to be explicit.
internal static class GrpcCallContextExtensions
{
    public static Grpc.Core.Metadata PlantIdMetadata(this HttpContext httpContext)
    {
        var metadata = new Grpc.Core.Metadata();
        var plantId = httpContext.Request.Headers["X-Plant-Id"].FirstOrDefault();
        if (!string.IsNullOrEmpty(plantId))
            metadata.Add("X-Plant-Id", plantId);
        return metadata;
    }
}
