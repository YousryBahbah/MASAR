using FluentValidation;
using Masar.Application.DTOs.Locations;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Services;

public class LocationService : ILocationService
{
    private readonly ApplicationDbContext _db;
    private readonly IValidator<CreateLocationRequest> _createValidator;
    private readonly IValidator<UpdateLocationRequest> _updateValidator;
    private readonly ILogger<LocationService> _logger;

    public LocationService(
        ApplicationDbContext db,
        IValidator<CreateLocationRequest> createValidator,
        IValidator<UpdateLocationRequest> updateValidator,
        ILogger<LocationService> logger)
    {
        _db = db;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
        _logger = logger;
    }

    public async Task<LocationResult> CreateAsync(CreateLocationRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return LocationResult.Failure("VALIDATION_FAILED", message);
        }

        // Location.Name has a real global unique index (Step 4) — this is
        // the friendly pre-check in front of that DB constraint, same
        // pattern as Workspace's capacity/hours checks in Step 8.
        var nameExists = await _db.Locations.AnyAsync(l => l.Name == request.Name);
        if (nameExists)
        {
            return LocationResult.Failure(
                "LOCATION_NAME_ALREADY_EXISTS", "A location with this name already exists.");
        }

        var location = new Location
        {
            Name = request.Name,
            Description = request.Description,
            Address = request.Address,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        _db.Locations.Add(location);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Location {LocationId} created.", location.Id);

        return LocationResult.Success(ToResponse(location));
    }

    public async Task<IReadOnlyList<LocationResponse>> GetAllAsync()
    {
        // Materialize first, then map — ToResponse is a plain C# method and
        // can't be translated into SQL if it's called inside the query
        // itself (i.e. before ToListAsync).
        var locations = await _db.Locations
            .OrderBy(l => l.Name)
            .ToListAsync();

        return locations.Select(ToResponse).ToList();
    }

    public async Task<LocationResponse?> GetByIdAsync(int id)
    {
        var location = await _db.Locations.FindAsync(id);
        return location is null ? null : ToResponse(location);
    }

    public async Task<LocationResult> UpdateAsync(int id, UpdateLocationRequest request)
    {
        var validation = await _updateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return LocationResult.Failure("VALIDATION_FAILED", message);
        }

        var location = await _db.Locations.FindAsync(id);
        if (location is null)
        {
            return LocationResult.Failure("LOCATION_NOT_FOUND", "Location not found.");
        }

        // Only check uniqueness against OTHER rows — a location keeping
        // its own current name must not trip its own uniqueness check.
        var nameTakenByAnother = await _db.Locations
            .AnyAsync(l => l.Id != id && l.Name == request.Name);
        if (nameTakenByAnother)
        {
            return LocationResult.Failure(
                "LOCATION_NAME_ALREADY_EXISTS", "A location with this name already exists.");
        }

        // Deliberately does not touch IsActive — activation state only
        // changes through ActivateAsync/DeactivateAsync, never through a
        // generic update, same rule already applied to Booking.Status.
        location.Name = request.Name;
        location.Description = request.Description;
        location.Address = request.Address;
        location.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        _logger.LogInformation("Location {LocationId} updated.", location.Id);

        return LocationResult.Success(ToResponse(location));
    }

    public async Task<LocationResult> ActivateAsync(int id)
    {
        var location = await _db.Locations.FindAsync(id);
        if (location is null)
        {
            return LocationResult.Failure("LOCATION_NOT_FOUND", "Location not found.");
        }

        // Idempotent: activating an already-active location is a no-op
        // success, not an error — same philosophy as the no-show job being
        // safe to run more than once.
        if (!location.IsActive)
        {
            location.IsActive = true;
            location.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogInformation("Location {LocationId} activated.", location.Id);
        }

        return LocationResult.Success(ToResponse(location));
    }

    public async Task<LocationResult> DeactivateAsync(int id)
    {
        var location = await _db.Locations.FindAsync(id);
        if (location is null)
        {
            return LocationResult.Failure("LOCATION_NOT_FOUND", "Location not found.");
        }

        if (location.IsActive)
        {
            location.IsActive = false;
            location.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            _logger.LogInformation("Location {LocationId} deactivated.", location.Id);
        }

        return LocationResult.Success(ToResponse(location));
    }

    private static LocationResponse ToResponse(Location l) => new(
        l.Id, l.Name, l.Description, l.Address, l.IsActive, l.CreatedAt, l.UpdatedAt);
}
