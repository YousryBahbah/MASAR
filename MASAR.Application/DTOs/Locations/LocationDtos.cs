namespace Masar.Application.DTOs.Locations;

public record CreateLocationRequest(
    string Name,
    string? Description,
    string Address
);

public record UpdateLocationRequest(
    string Name,
    string? Description,
    string Address
);

public record LocationResponse(
    int Id,
    string Name,
    string? Description,
    string Address,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

// Same shape as AuthResult, deliberately — this is the second time this
// pattern is needed (per the earlier decision: build a shared generic
// Result<T> only once a second real use case justifies it, not before).
// A non-generic LocationResult mirroring AuthResult is enough here since
// every mutating Location operation returns the same LocationResponse
// type; a generic wrapper isn't earning its keep yet.
public class LocationResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public LocationResponse? Response { get; init; }

    public static LocationResult Success(LocationResponse response) =>
        new() { Succeeded = true, Response = response };

    public static LocationResult Failure(string errorCode, string errorMessage) =>
        new() { Succeeded = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
