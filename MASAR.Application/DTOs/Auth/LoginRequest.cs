namespace Masar.Api.DTOs.Auth;

public record LoginRequest(
    string Email,
    string Password
);
