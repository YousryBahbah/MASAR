using Microsoft.AspNetCore.Identity;
using Masar.Domain.Entities;

namespace Masar.Domain.Entities;

/// <summary>
/// Application user. Extends Identity's default user — keeps Identity's
/// string-based Id rather than switching to int, per the locked design.
/// Roles (Member, WorkspaceManager, Admin) are managed by Identity, not
/// stored as a field here.
/// </summary>
public class ApplicationUser : IdentityUser
{
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ICollection<Booking> Bookings { get; set; } = new List<Booking>();
    public ICollection<MaintenancePeriod> CreatedMaintenancePeriods { get; set; } = new List<MaintenancePeriod>();
}
