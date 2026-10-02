namespace Masar.Application.DTOs.Maintenance;

public record CreateMaintenancePeriodRequest(
    int WorkspaceId,
    DateOnly? StartDate,
    TimeOnly? StartTime,
    DateOnly? EndDate,
    TimeOnly? EndTime,
    string? Reason
);

public record UpdateMaintenancePeriodRequest(
    DateOnly? StartDate,
    TimeOnly? StartTime,
    DateOnly? EndDate,
    TimeOnly? EndTime,
    string? Reason
);

public record MaintenancePeriodResponse(
    int Id,
    int WorkspaceId,
    string WorkspaceName,
    DateOnly StartDate,
    TimeOnly StartTime,
    DateOnly EndDate,
    TimeOnly EndTime,
    string? Reason,
    DateTime CreatedAt
);
