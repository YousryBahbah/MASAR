using Masar.Domain.Enums;

namespace Masar.Application.DTOs.Workspaces;

public record CreateWorkspaceRequest(
    int LocationId,
    string Name,
    WorkspaceType Type,
    int Capacity,
    int? Floor,
    TimeOnly OpeningTime,
    TimeOnly ClosingTime
);

// Deliberately excludes LocationId and Status — LocationId is immutable
// after creation, Status only changes via Activate/Deactivate. See the
// locked Step 8 plan for the reasoning.
public record UpdateWorkspaceRequest(
    string Name,
    WorkspaceType Type,
    int Capacity,
    int? Floor,
    TimeOnly OpeningTime,
    TimeOnly ClosingTime
);

public record WorkspaceResponse(
    int Id,
    int LocationId,
    string LocationName,
    string Name,
    WorkspaceType Type,
    int Capacity,
    int? Floor,
    WorkspaceStatus Status,
    TimeOnly OpeningTime,
    TimeOnly ClosingTime,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

// Same shape as AuthResult/LocationResult, deliberately — third use of
// the pattern now. Worth genuinely revisiting a shared generic Result<T>
// after this one, since three independent copies of an identical
// four-property shape is a stronger signal than two.
public class WorkspaceResult
{
    public bool Succeeded { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public WorkspaceResponse? Response { get; init; }

    public static WorkspaceResult Success(WorkspaceResponse response) =>
        new() { Succeeded = true, Response = response };

    public static WorkspaceResult Failure(string errorCode, string errorMessage) =>
        new() { Succeeded = false, ErrorCode = errorCode, ErrorMessage = errorMessage };
}
