namespace Masar.Domain.Entities;

/// <summary>
/// A workspace feature, e.g. Projector, Whiteboard, Monitor, AC.
/// </summary>
public class Amenity
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;   // nvarchar(100), unique

    // Navigation
    public ICollection<WorkspaceAmenity> WorkspaceAmenities { get; set; } = new List<WorkspaceAmenity>();
}
