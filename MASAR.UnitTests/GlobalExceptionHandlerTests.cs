using System.Text.Json;
using Masar.Api.ExceptionHandling;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Masar.UnitTests;

// Step 16. The handler is the last line of defence, so the properties that
// matter are negative ones: nothing about the exception may reach the client.
public class GlobalExceptionHandlerTests
{
    private static readonly GlobalExceptionHandler Handler =
        new(NullLogger<GlobalExceptionHandler>.Instance);

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
            TraceIdentifier = "trace-abc-123"
        };
        context.Request.Method = "GET";
        context.Request.Path = "/api/anything";
        context.Response.Body = body;
        return (context, body);
    }

    private static async Task<(string Code, string Message, string Raw)> ReadEnvelope(MemoryStream body)
    {
        body.Position = 0;
        var raw = await new StreamReader(body).ReadToEndAsync();
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        return (root.GetProperty("code").GetString()!, root.GetProperty("message").GetString()!, raw);
    }

    [Fact]
    public async Task An_unexpected_exception_becomes_a_500_in_the_standard_envelope()
    {
        var (context, body) = NewContext();

        var handled = await Handler.TryHandleAsync(context, new InvalidOperationException("boom"), CancellationToken.None);
        var (code, _, _) = await ReadEnvelope(body);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Equal("INTERNAL_ERROR", code);
        Assert.StartsWith("application/json", context.Response.ContentType);
    }

    [Fact]
    public async Task The_500_body_never_contains_exception_text_type_names_or_inner_exceptions()
    {
        var (context, body) = NewContext();
        var exception = new InvalidOperationException(
            "SECRET-OUTER: connection string Server=prod;Password=hunter2",
            new ArgumentException("SECRET-INNER"));

        await Handler.TryHandleAsync(context, exception, CancellationToken.None);
        var (_, _, raw) = await ReadEnvelope(body);

        Assert.DoesNotContain("SECRET", raw);
        Assert.DoesNotContain("hunter2", raw);
        Assert.DoesNotContain("InvalidOperationException", raw);
        Assert.DoesNotContain("ArgumentException", raw);
        Assert.DoesNotContain("   at ", raw); // no stack trace
    }

    [Fact]
    public async Task The_500_message_carries_the_trace_id_so_it_can_be_matched_to_the_log_entry()
    {
        var (context, body) = NewContext();

        await Handler.TryHandleAsync(context, new Exception("x"), CancellationToken.None);
        var (_, message, _) = await ReadEnvelope(body);

        Assert.Contains("trace-abc-123", message);
    }

    [Fact]
    public async Task A_bad_http_request_keeps_its_own_status_and_never_becomes_a_500()
    {
        var (context, body) = NewContext();

        await Handler.TryHandleAsync(context, new BadHttpRequestException("Request body too large.", 413), CancellationToken.None);
        var (code, _, _) = await ReadEnvelope(body);

        Assert.Equal(413, context.Response.StatusCode);
        Assert.Equal("HTTP_413", code);
    }

    [Fact]
    public async Task A_plain_bad_request_maps_to_validation_failed_without_echoing_the_framework_message()
    {
        var (context, body) = NewContext();

        await Handler.TryHandleAsync(context, new BadHttpRequestException("Unexpected end of request content.", 400), CancellationToken.None);
        var (code, _, raw) = await ReadEnvelope(body);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", code);
        Assert.DoesNotContain("Unexpected end", raw);
    }
}
