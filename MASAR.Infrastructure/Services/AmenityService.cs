using FluentValidation;
using Masar.Application.Common;
using Masar.Application.DTOs.Amenities;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Services;

public class AmenityService : IAmenityService
{
    private readonly ApplicationDbContext _db;
    private readonly IValidator<CreateAmenityRequest> _createValidator;
    private readonly IValidator<UpdateWorkspaceAmenitiesRequest> _updateWorkspaceAmenitiesValidator;
    private readonly ILogger<AmenityService> _logger;

    public AmenityService(
        ApplicationDbContext db,
        IValidator<CreateAmenityRequest> createValidator,
        IValidator<UpdateWorkspaceAmenitiesRequest> updateWorkspaceAmenitiesValidator,
        ILogger<AmenityService> logger)
    {
        _db = db;
        _createValidator = createValidator;
        _updateWorkspaceAmenitiesValidator = updateWorkspaceAmenitiesValidator;
        _logger = logger;
    }

    public async Task<Result<AmenityResponse>> CreateAsync(CreateAmenityRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<AmenityResponse>.Failure("VALIDATION_FAILED", message);
        }

        var nameExists = await _db.Amenities.AnyAsync(a => a.Name == request.Name);
        if (nameExists)
        {
            return Result<AmenityResponse>.Failure(
                "AMENITY_NAME_ALREADY_EXISTS", "An amenity with this name already exists.");
        }

        var amenity = new Amenity { Name = request.Name };

        _db.Amenities.Add(amenity);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Amenity {AmenityId} created.", amenity.Id);

        return Result<AmenityResponse>.Success(ToResponse(amenity));
    }

    public async Task<List<AmenityResponse>> GetAllAsync()
    {
        // Materialize first, then map — same reasoning as
        // LocationService.GetAllAsync: ToResponse can't be translated
        // into SQL if called before ToListAsync.
        var amenities = await _db.Amenities
            .OrderBy(a => a.Name)
            .ToListAsync();

        return amenities.Select(ToResponse).ToList();
    }

    public async Task<Result<List<AmenityResponse>>> UpdateWorkspaceAmenitiesAsync(
        int workspaceId, UpdateWorkspaceAmenitiesRequest request)
    {
        var validation = await _updateWorkspaceAmenitiesValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<List<AmenityResponse>>.Failure("VALIDATION_FAILED", message);
        }

        // Workspace existence is checked before any amenity ID is
        // validated — see the locked Step 9 plan. No point validating a
        // set of amenity IDs against a workspace that isn't there.
        var workspaceExists = await _db.Workspaces.AnyAsync(w => w.Id == workspaceId);
        if (!workspaceExists)
        {
            return Result<List<AmenityResponse>>.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        // Deduplicate first — [1, 1, 2] must be treated identically to
        // [1, 2], or a request with an accidental repeat would look like
        // it's requesting more distinct amenities than it actually is.
        var requestedIds = request.AmenityIds.Distinct().ToList();

        var existingAmenities = await _db.Amenities
            .Where(a => requestedIds.Contains(a.Id))
            .ToListAsync();

        // All-or-nothing: if any requested ID doesn't exist, fail with
        // zero writes — don't touch the join table at all.
        if (existingAmenities.Count != requestedIds.Count)
        {
            return Result<List<AmenityResponse>>.Failure(
                "AMENITY_NOT_FOUND", "One or more amenity IDs do not exist.");
        }

        // Every ID is confirmed valid — now perform the actual replace
        // (delete existing rows, insert the new set) inside a single
        // transaction, so a failure partway through can't leave some old
        // rows deleted and some new ones missing.
        await using var transaction = await _db.Database.BeginTransactionAsync();

        var existingJoinRows = await _db.WorkspaceAmenities
            .Where(wa => wa.WorkspaceId == workspaceId)
            .ToListAsync();
        _db.WorkspaceAmenities.RemoveRange(existingJoinRows);

        foreach (var amenityId in requestedIds)
        {
            _db.WorkspaceAmenities.Add(new WorkspaceAmenity
            {
                WorkspaceId = workspaceId,
                AmenityId = amenityId
            });
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        _logger.LogInformation(
            "Workspace {WorkspaceId} amenities replaced with [{AmenityIds}].",
            workspaceId, string.Join(",", requestedIds));

        // existingAmenities already holds exactly the rows we just linked
        // (same requestedIds set, existence already confirmed above) —
        // no need for a second round-trip to re-read them.
        return Result<List<AmenityResponse>>.Success(
            existingAmenities.Select(ToResponse).ToList());
    }

    private static AmenityResponse ToResponse(Amenity a) => new(a.Id, a.Name);
}
