namespace Masar.Application.DTOs.Auth;

public class AuthResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public AuthResponse? Response { get; init; }

    public static AuthResult Success(AuthResponse response) =>
        new() { Succeeded = true, Response = response };

    public static AuthResult Failure(string errorCode, string errorMessage) =>
        new() { Succeeded = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
