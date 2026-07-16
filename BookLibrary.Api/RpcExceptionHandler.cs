using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BookLibrary.Api;

/// <summary>
/// Translates gRPC failures from the Catalog backend into HTTP ProblemDetails at the REST edge:
/// NotFound → 404, InvalidArgument → 400, FailedPrecondition/AlreadyExists → 409, cancellation →
/// 499, everything else → 500. This is the single seam where backend status codes become HTTP
/// semantics.
/// </summary>
public sealed partial class RpcExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<RpcExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not RpcException rpc)
            return false;

        var status = rpc.StatusCode switch
        {
            StatusCode.NotFound => StatusCodes.Status404NotFound,
            StatusCode.InvalidArgument => StatusCodes.Status400BadRequest,
            StatusCode.FailedPrecondition => StatusCodes.Status409Conflict,
            StatusCode.AlreadyExists => StatusCodes.Status409Conflict,
            StatusCode.Cancelled => 499, // client closed request
            _ => StatusCodes.Status500InternalServerError,
        };

        if (status >= 500)
            Log.Unexpected(logger, rpc.StatusCode, rpc);
        else
            Log.Rejected(logger, rpc.StatusCode, rpc.Status.Detail);

        context.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = ReasonFor(status),
                Detail = rpc.Status.Detail,
            },
        });
    }

    private static string ReasonFor(int status) => status switch
    {
        StatusCodes.Status404NotFound => "Not Found",
        StatusCodes.Status400BadRequest => "Bad Request",
        StatusCodes.Status409Conflict => "Conflict",
        499 => "Client Closed Request",
        _ => "Internal Server Error",
    };

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message = "Backend rejected request ({Status}): {Detail}")]
        public static partial void Rejected(ILogger logger, StatusCode status, string detail);

        [LoggerMessage(Level = LogLevel.Error, Message = "Backend call failed ({Status}).")]
        public static partial void Unexpected(ILogger logger, StatusCode status, Exception exception);
    }
}
