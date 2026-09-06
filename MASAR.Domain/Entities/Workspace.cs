using Masar.Domain.Entities;
using Masar.Domain.Enums;

namespace Masar.Domain.Entities;

/// <summary>
/// A single bookable unit within a Location (room, pod, or office).
/// Status reflects only administrative availability (Available/Inactive);
/// temporary unavailability is modeled via MaintenancePeriod, not here.
/// </summary>
public class Workspace
{
    public int Id { get; set; }
    public int LocationId { get; set; }
    public string Name { get; set; } = null!;             // nvarchar(150), unique per LocationId
    public WorkspaceType Type { get; set; }
    public int Capacity { get; set; }                     // must be > 0
    public int? Floor { get; set; }
    public WorkspaceStatus Status { get; set; } = WorkspaceStatus.Available;
    public TimeOnly OpeningTime { get; set; }              // local time; must be < ClosingTime
    public TimeOnly ClosingTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public Location Location { get; set; } = null!;
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<MaintenancePeriod> MaintenancePeriods { get; set; } = new List<MaintenancePeriod>();
    public ICollection<WorkspaceAmenity> WorkspaceAmenities { get; set; } = new List<WorkspaceAmenity>();
}
