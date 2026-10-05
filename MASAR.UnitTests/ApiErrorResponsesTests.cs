using System.Text.Json;
using Masar.Api.ExceptionHandling;
using Masar.Application.DTOs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Masar.UnitTests;

public class ApiErrorResponsesTests
{
    private static ActionContext ActionContextWith(ModelStateDictionary modelState) =>
        new(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), modelState);

    private static ErrorResponse BodyOf(IActionResult result)
    {
        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status400BadRequest, bad.StatusCode);
        return Assert.IsType<ErrorResponse>(bad.Value);
    }

    // ---------------- model binding failures ----------------

    [Fact]
    public void An_empty_body_gets_the_missing_or_malformed_message()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError(string.Empty, "A non-empty request body is required.");

        var body = BodyOf(ApiErrorResponses.InvalidModelState(ActionContextWith(modelState)));

        Assert.Equal("VALIDATION_FAILED", body.Code);
        Assert.Equal("Request body is missing or malformed.", body.Message);
    }

    [Fact]
    public void Only_field_names_are_echoed_never_the_serializer_message()
    {
        var modelState = new ModelStateDictionary();
        modelState.AddModelError("$.startTime",
            "The JSON value could not be converted to System.TimeOnly. Path: $.startTime | LineNumber: 3");
        modelState.AddModelError("status", "The value 'Nope' is not valid for BookingStatus.");

        var body = BodyOf(ApiErrorResponses.InvalidModelState(ActionContextWith(modelState)));

        Assert.Equal("VALIDATION_FAILED", body.Code);
        Assert.Contains("startTime", body.Message);
        Assert.Contains("status", body.Message);
        Assert.DoesNotContain("System.TimeOnly", body.Message);
        Assert.DoesNotContain("LineNumber", body.Message);
        Assert.DoesNotContain("BookingStatus", body.Message);
    }

    // ---------------- status-code envelope ----------------

    [Theory]
    [InlineData(401, "UNAUTHORIZED")]
    [InlineData(403, "FORBIDDEN")]
    [InlineData(404, "NOT_FOUND")]
    [InlineData(405, "METHOD_NOT_ALLOWED")]
    [InlineData(415, "UNSUPPORTED_MEDIA_TYPE")]
    [InlineData(400, "VALIDATION_FAILED")]
    [InlineData(500, "INTERNAL_ERROR")]
    [InlineData(503, "INTERNAL_ERROR")]
    public void Known_status_codes_map_to_their_error_code(int status, string expectedCode)
    {
        var response = StatusCodeResponses.For(status);

        Assert.Equal(expectedCode, response.Code);
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
    }

    [Fact]
    public void An_unlisted_status_still_gets_a_code_and_a_message()
    {
        var response = StatusCodeResponses.For(418);

        Assert.Equal("HTTP_418", response.Code);
        Assert.False(string.IsNullOrWhiteSpace(response.Message));
    }

    [Fact]
    public async Task WriteAsync_writes_the_envelope_for_the_current_response_status()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Response.Body = body;
        context.Response.StatusCode = StatusCodes.Status404NotFound;

        await StatusCodeResponses.WriteAsync(
            new StatusCodeContext(context, new StatusCodePagesOptions(), _ => Task.CompletedTask));

        body.Position = 0;
        using var document = JsonDocument.Parse(await new StreamReader(body).ReadToEndAsync());
        Assert.Equal("NOT_FOUND", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(404, context.Response.StatusCode);
    }
}
