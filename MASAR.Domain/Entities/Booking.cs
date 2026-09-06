using Masar.Domain.Entities;
using Masar.Domain.Enums;

namespace Masar.Domain.Entities;

/// <summary>
/// Core reservation of a Workspace by a Member.
///
/// Only Confirmed and CheckedIn bookings block availability — used in the
/// overlap predicate during creation:
///   Existing.StartTime &lt; New.EndTime AND Existing.EndTime &gt; New.StartTime
///
/// Bookings are created directly as Confirmed (no Pending state). Cancellation
/// is a state transition (Confirmed -> Cancelled), never a row delete — the
/// row is preserved for history. Check-in data lives on the Booking itself;
/// there is no separate CheckIn table in v1. Bookings cannot cross midnight,
/// and all timestamps are stored/compared in UTC.
/// </summary>
public class Booking
{
    public int Id { get; set; }
    public string UserId { get; set; } = null!;             // FK to ApplicationUser (Identity string key)
    public int WorkspaceId { get; set; }
    public DateTime StartTime { get; set; }                  // UTC; must be < EndTime
    public DateTime EndTime { get; set; }                    // UTC
    public BookingStatus Status { get; set; } = BookingStatus.Confirmed;
    public DateTime? CheckedInAt { get; set; }                // UTC, set on check-in
    public CheckInMethod? CheckInMethod { get; set; }          // null until checked in; QR only in v1
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Navigation
    public ApplicationUser User { get; set; } = null!;
    public Workspace Workspace { get; set; } = null!;
}
