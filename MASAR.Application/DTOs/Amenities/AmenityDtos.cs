namespace Masar.Application.DTOs.Amenities;

public record CreateAmenityRequest(string Name);

public record AmenityResponse(int Id, string Name);

// The full desired set of amenity IDs for a workspace — PUT replaces the
// join-table rows accordingly, it does not add/remove individually.
// See the locked Step 9 plan for why this is a full-set replace rather
// than separate add/remove endpoints.
public record UpdateWorkspaceAmenitiesRequest(List<int> AmenityIds);
