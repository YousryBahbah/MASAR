using Masar.Domain.Entities;

namespace Masar.Domain.Entities;

/// <summary>
/// A time-bounded window during which a Workspace is unavailable.
/// Modeled as a time-based period, not as a Workspace status — a workspace
/// can have multiple past/future maintenance periods while remaining
/// otherwise Available.
/// Historical rows are protected via restrictive delete behavior on Workspace.
/// </summary>
public class MaintenancePeriod
{
    public int Id { get; set; }
    public int WorkspaceId { get; set; }
    public string CreatedByUserId { get; set; } = null!;   // FK to ApplicationUser (Identity string key)
    public DateTime StartTime { get; set; }                 // UTC
    public DateTime EndTime { get; set; }                   // UTC
    public string? Reason { get; set; }                     // nvarchar(500)
    public DateTime CreatedAt { get; set; }

    // Navigation
    public Workspace Workspace { get; set; } = null!;
    public ApplicationUser CreatedByUser { get; set; } = null!;
}
