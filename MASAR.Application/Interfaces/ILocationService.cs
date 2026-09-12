namespace Masar.Application.Interfaces;

using Masar.Application.DTOs.Locations;

public interface ILocationService
{
    Task<LocationResult> CreateAsync(CreateLocationRequest request);
    Task<IReadOnlyList<LocationResponse>> GetAllAsync();
    Task<LocationResponse?> GetByIdAsync(int id);
    Task<LocationResult> UpdateAsync(int id, UpdateLocationRequest request);
    Task<LocationResult> ActivateAsync(int id);
    Task<LocationResult> DeactivateAsync(int id);
}
