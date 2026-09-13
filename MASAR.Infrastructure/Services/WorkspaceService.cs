using FluentValidation;
using Masar.Application.DTOs.Workspaces;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Services;

public class WorkspaceService : IWorkspaceService
{
    private readonly ApplicationDbContext _db;
    private readonly IValidator<CreateWorkspaceRequest> _createValidator;
    private readonly IValidator<UpdateWorkspaceRequest> _updateValidator;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(
        ApplicationDbContext db,
        IValidator<CreateWorkspaceRequest> createValidator,
        IValidator<UpdateWorkspaceRequest> updateValidator,
        ILogger<WorkspaceService> logger)
    {
        _db = db;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _logger = logger;
    }

    public async Task<WorkspaceResult> CreateAsync(CreateWorkspaceRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return WorkspaceResult.Failure("VALIDATION_FAILED", message);
        }

        var location = await _db.Locations.FindAsync(request.LocationId);
        if (location is null)
        {
            return WorkspaceResult.Failure("LOCATION_NOT_FOUND", "Location not found.");
        }

        if (!location.IsActive)
        {
            return WorkspaceResult.Failure(
                "LOCATION_INACTIVE", "Cannot create a workspace under a deactivated location.");
        }

        var nameExists = await _db.Workspaces
            .AnyAsync(w => w.LocationId == request.LocationId && w.Name == request.Name);
        if (nameExists)
        {
            return WorkspaceResult.Failure(
                "WORKSPACE_NAME_ALREADY_EXISTS_IN_LOCATION",
                "A workspace with this name already exists in this location.");
        }

        var workspace = new Workspace
        {
            LocationId = request.LocationId,
            Name = request.Name,
            Type = request.Type,
            Capacity = request.Capacity,
            Floor = request.Floor,
            Status = WorkspaceStatus.Available,
            OpeningTime = request.OpeningTime,
            ClosingTime = request.ClosingTime,
            CreatedAt = DateTime.UtcNow
        };

        _db.Workspaces.Add(workspace);
        await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Workspace {WorkspaceId} created under Location {LocationId}.",
            workspace.Id, workspace.LocationId);

        // location is already loaded above — pass its name explicitly
        // rather than relying on EF Core's automatic navigation-property
        // fixup to have populated workspace.Location by this point.
        return WorkspaceResult.Success(ToResponse(workspace, location.Name));
    }

    public async Task<WorkspaceResponse?> GetByIdAsync(int id)
    {
        var workspace = await _db.Workspaces
            .Include(w => w.Location)
            .FirstOrDefaultAsync(w => w.Id == id);

        return workspace is null ? null : ToResponse(workspace, workspace.Location.Name);
    }

    public async Task<WorkspaceResult> UpdateAsync(int id, UpdateWorkspaceRequest request)
    {
        var validation = await _updateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return WorkspaceResult.Failure("VALIDATION_FAILED", message);
        }

        var workspace = await _db.Workspaces
            .Include(w => w.Location)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (workspace is null)
        {
            return WorkspaceResult.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        // LocationId can't change (see UpdateWorkspaceRequest) — uniqueness
        // is only re-checked within the workspace's existing location,
        // excluding itself, same self-exclusion pattern as Location.
        var nameTakenByAnother = await _db.Workspaces
            .AnyAsync(w => w.Id != id
                        && w.LocationId == workspace.LocationId
                        && w.Name == request.Name);
        if (nameTakenByAnother)
        {
            return WorkspaceResult.Failure(
                "WORKSPACE_NAME_ALREADY_EXISTS_IN_LOCATION",
                "A workspace with this name already exists in this location.");
        }

        // Deliberately does not touch Status or LocationId — activation
        // state only changes through Activate/Deactivate, and LocationId
        // is immutable after creation (locked Step 8 plan).
        //
        // Also deliberately does NOT re-check the parent Location's
        // IsActive here. LOCATION_INACTIVE only blocks creating a NEW
        // workspace under an inactive location — since LocationId can't
        // change on update, there's no new conflict being introduced by
        // an edit. Blocking an unrelated fix (like a typo in the name)
        // because the location was separately deactivated afterward would
        // be a stricter rule than anything actually locked for this step.
        workspace.Name = request.Name;
        workspace.Type = request.Type;
        workspace.Capacity = request.Capacity;
        workspace.Floor = request.Floor;
        workspace.OpeningTime = request.OpeningTime;
        workspace.ClosingTime = request.ClosingTime;
        workspace.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("Workspace {WorkspaceId} updated.", workspace.Id);

        return WorkspaceResult.Success(ToResponse(workspace, workspace.Location.Name));
    }

    public async Task<WorkspaceResult> ActivateAsync(int id)
    {
        var workspace = await _db.Workspaces
            .Include(w => w.Location)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (workspace is null)
        {
            return WorkspaceResult.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        // Idempotent, same as Location — activating an already-available
        // workspace is a no-op success, not an error.
        if (workspace.Status != WorkspaceStatus.Available)
        {
            workspace.Status = WorkspaceStatus.Available;
            workspace.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogInformation("Workspace {WorkspaceId} activated.", workspace.Id);
        }

        return WorkspaceResult.Success(ToResponse(workspace, workspace.Location.Name));
    }

    public async Task<WorkspaceResult> DeactivateAsync(int id)
    {
        var workspace = await _db.Workspaces
            .Include(w => w.Location)
            .FirstOrDefaultAsync(w => w.Id == id);
        if (workspace is null)
        {
            return WorkspaceResult.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        if (workspace.Status != WorkspaceStatus.Inactive)
        {
            workspace.Status = WorkspaceStatus.Inactive;
            workspace.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogInformation("Workspace {WorkspaceId} deactivated.", workspace.Id);
        }

        return WorkspaceResult.Success(ToResponse(workspace, workspace.Location.Name));
    }

    private static WorkspaceResponse ToResponse(Workspace w, string locationName) => new(
        w.Id, w.LocationId, locationName, w.Name, w.Type, w.Capacity, w.Floor,
        w.Status, w.OpeningTime, w.ClosingTime, w.CreatedAt, w.UpdatedAt);
}
