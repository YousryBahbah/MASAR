using Masar.Application.Common;
using Masar.Application.DTOs.Amenities;

namespace Masar.Application.Interfaces;

public interface IAmenityService
{
    Task<Result<AmenityResponse>> CreateAsync(CreateAmenityRequest request);

    Task<List<AmenityResponse>> GetAllAsync();

    // Returns WORKSPACE_NOT_FOUND if the workspace doesn't exist, checked
    // before any amenity ID is validated — see the locked Step 9 plan.
    Task<Result<List<AmenityResponse>>> UpdateWorkspaceAmenitiesAsync(
        int workspaceId, UpdateWorkspaceAmenitiesRequest request);
}
