namespace Masar.Domain.Entities;

/// <summary>
/// A physical site containing one or more workspaces.
/// V1 is Egypt-only, so no TimeZoneId field is needed.
/// </summary>
public class Location
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;           // nvarchar(150), unique
    public string? Description { get; set; }             // nvarchar(500)
    public string? Address { get; set; }                 // nvarchar(300)
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public ICollection<Workspace> Workspaces { get; set; } = new List<Workspace>();
}
