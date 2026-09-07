namespace Masar.Application.DTOs.Auth;

public record AuthResponse(
    string UserId,
    string Email,
    string FirstName,
    string LastName,
    IReadOnlyList<string> Roles,
    string AccessToken,
    DateTime ExpiresAtUtc
);
