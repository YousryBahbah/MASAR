using Masar.Application.DTOs;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Masar.Api.ExceptionHandling;

// Every non-controller error path (model binding, bare 401/403/404,
// unhandled exceptions) goes through here so the whole API speaks one
// envelope: { "code": "...", "message": "..." }.
public static class ApiErrorResponses
{
    // Wired as ApiBehaviorOptions.InvalidModelStateResponseFactory.
    // Fires when [ApiController] model binding fails: empty body, malformed
    // JSON, a value that can't convert to its type (e.g. a bad enum or
    // TimeOnly), a non-nullable property missing from the body.
    //
    // Only field NAMES are echoed back, never the serializer's own message:
    // those contain paths and line numbers and are noise to a caller.
    public static IActionResult InvalidModelState(ActionContext context)
    {
        var fields = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .Select(entry => entry.Key.TrimStart('$', '.'))
            .Where(key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var message = fields.Count == 0
            ? "Request body is missing or malformed."
            : $"One or more fields are invalid: {string.Join(", ", fields)}.";

        return new BadRequestObjectResult(new ErrorResponse("VALIDATION_FAILED", message));
    }
}

public static class StatusCodeResponses
{
    public static ErrorResponse For(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest =>
            new ErrorResponse("VALIDATION_FAILED", "The request is invalid."),
        StatusCodes.Status401Unauthorized =>
            new ErrorResponse("UNAUTHORIZED",
                "Authentication is required, or the token is missing, expired or no longer valid."),
        StatusCodes.Status403Forbidden =>
            new ErrorResponse("FORBIDDEN", "You do not have permission to perform this action."),
        StatusCodes.Status404NotFound =>
            new ErrorResponse("NOT_FOUND", "The requested resource was not found."),
        StatusCodes.Status405MethodNotAllowed =>
            new ErrorResponse("METHOD_NOT_ALLOWED", "The HTTP method is not allowed for this resource."),
        StatusCodes.Status415UnsupportedMediaType =>
            new ErrorResponse("UNSUPPORTED_MEDIA_TYPE",
                "The request content type is not supported. Use application/json."),
        >= 500 =>
            new ErrorResponse("INTERNAL_ERROR", "The server could not complete the request."),
        _ =>
            new ErrorResponse($"HTTP_{statusCode}", ReasonOrDefault(statusCode))
    };

    // Wired into UseStatusCodePages. Only runs for responses that have a
    // 4xx/5xx status and NO body yet — every controller result that already
    // carries an ErrorResponse is left alone.
    public static Task WriteAsync(StatusCodeContext context)
    {
        var response = context.HttpContext.Response;
        return response.WriteAsJsonAsync(For(response.StatusCode));
    }

    private static string ReasonOrDefault(int statusCode)
    {
        var phrase = ReasonPhrases.GetReasonPhrase(statusCode);
        return string.IsNullOrEmpty(phrase) ? "The request failed." : phrase;
    }
}
