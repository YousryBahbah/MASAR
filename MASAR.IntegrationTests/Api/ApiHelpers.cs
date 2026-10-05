using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Masar.Application.DTOs;
using Masar.Application.DTOs.Auth;
using Masar.IntegrationTests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Masar.IntegrationTests.Api;

internal static class ApiHelpers
{
    // Mirrors the server: camelCase, enums as strings.
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    // Takes the BASE factory type on purpose: WithWebHostBuilder(...) returns a
    // plain WebApplicationFactory<Program>, not an ApiFactory, and the test
    // that swaps in a throwing service needs to call this on that result.
    // ApiFactory derives from it, so every other call site is unaffected.
    public static HttpClient ClientFor(this WebApplicationFactory<Program> factory, string? token = null)
    {
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(Json);
        return value ?? throw new InvalidOperationException("Response body was empty.");
    }

    // Asserts the status AND that the body is the standard {code, message} envelope.
    public static async Task AssertErrorAsync(
        this HttpResponseMessage response, HttpStatusCode expectedStatus, string expectedCode)
    {
        var raw = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expectedStatus,
            $"Expected {(int)expectedStatus} but got {(int)response.StatusCode}. Body: {raw}");

        ErrorResponse? error;
        try
        {
            error = JsonSerializer.Deserialize<ErrorResponse>(raw, Json);
        }
        catch (JsonException)
        {
            error = null;
        }

        Assert.True(error is not null && !string.IsNullOrWhiteSpace(error.Code) && !string.IsNullOrWhiteSpace(error.Message),
            $"Body is not the standard error envelope: {raw}");
        Assert.Equal(expectedCode, error!.Code);
    }

    public static async Task<AuthResponse> LoginAsync(this ApiFactory factory, string email, string password)
    {
        var response = await factory.ClientFor().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadAsync<AuthResponse>();
    }

    // Register -> Login through the real endpoints, like a real client.
    public static async Task<(string UserId, string Email, string Token)> RegisterAndLoginMemberAsync(this ApiFactory factory)
    {
        var email = $"api_{Guid.NewGuid():N}@test.local";

        var register = await factory.ClientFor().PostAsJsonAsync(
            "/api/auth/register", new RegisterRequest("Api", "Member", email, TestData.Password));
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        var login = await factory.LoginAsync(email, TestData.Password);
        return (login.UserId, email, login.AccessToken);
    }

    // Staff accounts cannot be self-registered (registration only ever grants
    // Member), so they are created directly and then log in via the API.
    public static async Task<(string UserId, string Token)> CreateStaffAndLoginAsync(this ApiFactory factory, string role)
    {
        var user = await TestData.CreateUserAsync(role);
        var login = await factory.LoginAsync(user.Email!, TestData.Password);
        return (user.Id, login.AccessToken);
    }
}
