namespace Masar.Domain.Enums;

/// <summary>
/// Whether a workspace is currently bookable.
/// Inactive means administratively disabled from booking — it does NOT
/// represent a temporarily busy or under-maintenance state.
/// Temporary unavailability is modeled separately via MaintenancePeriod.
/// </summary>
public enum WorkspaceStatus
{
    Available = 0,
    Inactive = 1
}
