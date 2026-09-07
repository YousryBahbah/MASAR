namespace Masar.Api.DTOs;

/// <summary>
/// Standard error envelope used across the API, per the Step 2 locked
/// convention: { "code": "...", "message": "..." }.
/// </summary>
public record ErrorResponse(string Code, string Message);
