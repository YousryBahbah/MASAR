namespace Masar.Domain.Entities;

/// <summary>
/// Many-to-many join between Workspace and Amenity.
/// Composite primary key: (WorkspaceId, AmenityId) — configured in the DbContext,
/// no surrogate Id.
/// Delete behavior: cascades from either Workspace or Amenity, per the locked design.
/// </summary>
public class WorkspaceAmenity
{
    public int WorkspaceId { get; set; }
    public int AmenityId { get; set; }

    // Navigation
    public Workspace Workspace { get; set; } = null!;
    public Amenity Amenity { get; set; } = null!;
}
