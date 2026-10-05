using Masar.Application.DTOs;
using Masar.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics;

namespace Masar.Api.ExceptionHandling;

// Single place where an exception that escaped a controller/service
// becomes an HTTP response. Services keep returning Result<T> for EXPECTED
// outcomes (validation, business conflicts); this handler is only for the
// unexpected, plus infrastructure failures that are not the caller's fault.
//
//   deadlock (SQL 1205)        -> 503 CONCURRENT_WRITE_CONFLICT (retryable)
//   unique index violation     -> 409 CONFLICT
//   BadHttpRequestException    -> its own status (oversized / unreadable body)
//   anything else              -> 500 INTERNAL_ERROR
//
// The 500 body never contains exception text — only a reference (the
// request's trace id) that matches the structured log entry written here.
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (statusCode, body) = Classify(httpContext, exception);

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(body, cancellationToken);
        return true;
    }

    private (int StatusCode, ErrorResponse Body) Classify(HttpContext httpContext, Exception exception)
    {
        var method = httpContext.Request.Method;
        var path = httpContext.Request.Path.Value;
        var traceId = httpContext.TraceIdentifier;

        if (SqlServerErrors.IsDeadlock(exception))
        {
            _logger.LogWarning(exception,
                "Deadlock detected handling {Method} {Path} (trace {TraceId}).", method, path, traceId);

            // The error is explicitly retryable; tell clients how soon.
            httpContext.Response.Headers["Retry-After"] = "1";

            return (StatusCodes.Status503ServiceUnavailable, new ErrorResponse(
                "CONCURRENT_WRITE_CONFLICT",
                "A temporary conflict occurred while processing the request. Please try again."));
        }

        if (SqlServerErrors.IsUniqueViolation(exception))
        {
            _logger.LogWarning(exception,
                "Unique constraint violation handling {Method} {Path} (trace {TraceId}).", method, path, traceId);

            return (StatusCodes.Status409Conflict, new ErrorResponse(
                "CONFLICT", "The request conflicts with data that already exists."));
        }

        if (exception is BadHttpRequestException badRequest)
        {
            _logger.LogInformation(
                "Bad HTTP request {Method} {Path} (trace {TraceId}): {Reason}",
                method, path, traceId, badRequest.Message);

            var body = badRequest.StatusCode == StatusCodes.Status400BadRequest
                ? new ErrorResponse("VALIDATION_FAILED", "The request could not be read.")
                : StatusCodeResponses.For(badRequest.StatusCode);

            return (badRequest.StatusCode, body);
        }

        _logger.LogError(exception,
            "Unhandled exception handling {Method} {Path} (trace {TraceId}).", method, path, traceId);

        return (StatusCodes.Status500InternalServerError, new ErrorResponse(
            "INTERNAL_ERROR", $"An unexpected error occurred. Reference: {traceId}."));
    }
}
