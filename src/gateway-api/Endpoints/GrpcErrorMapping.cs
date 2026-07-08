using Grpc.Core;

namespace GatewayApi.Endpoints;

// Maps a downstream RpcException to the HTTP response gateway-api sends back,
// instead of every endpoint collapsing every RpcException into a blanket 502.
//
// order-api only ever sets Status.Detail to a caller-safe validation/not-found
// message for the "client-fixable" codes below (see OrderGrpcService.cs) — safe
// to relay verbatim. Everything else keeps the endpoint's own generic message
// so internal failure details never reach the client.
internal static class GrpcErrorMapping
{
    public static IResult ToProblem(this RpcException ex, string genericMessage) => ex.StatusCode switch
    {
        StatusCode.InvalidArgument or StatusCode.FailedPrecondition or StatusCode.OutOfRange =>
            Results.Problem(ex.Status.Detail, statusCode: StatusCodes.Status400BadRequest),
        StatusCode.Unauthenticated =>
            Results.Problem(ex.Status.Detail, statusCode: StatusCodes.Status401Unauthorized),
        StatusCode.PermissionDenied =>
            Results.Problem(ex.Status.Detail, statusCode: StatusCodes.Status403Forbidden),
        StatusCode.NotFound =>
            Results.Problem(ex.Status.Detail, statusCode: StatusCodes.Status404NotFound),
        StatusCode.AlreadyExists or StatusCode.Aborted =>
            Results.Problem(ex.Status.Detail, statusCode: StatusCodes.Status409Conflict),
        StatusCode.ResourceExhausted =>
            Results.Problem(ex.Status.Detail, statusCode: StatusCodes.Status429TooManyRequests),
        StatusCode.Unimplemented =>
            Results.Problem(genericMessage, statusCode: StatusCodes.Status501NotImplemented),
        StatusCode.Unavailable =>
            Results.Problem(genericMessage, statusCode: StatusCodes.Status503ServiceUnavailable),
        StatusCode.DeadlineExceeded =>
            Results.Problem(genericMessage, statusCode: StatusCodes.Status504GatewayTimeout),
        // Internal, Unknown, DataLoss, Cancelled: no more-specific HTTP status is safe to
        // infer — 502 matches the fallback used for non-RpcException failures below.
        _ => Results.Problem(genericMessage, statusCode: StatusCodes.Status502BadGateway),
    };
}
