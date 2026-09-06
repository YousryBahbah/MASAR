namespace Masar.Domain.Enums;

/// <summary>
/// Lifecycle state of a Booking.
///
/// Valid transitions:
///   Confirmed -> CheckedIn
///   Confirmed -> Cancelled
///   Confirmed -> NoShow
///   CheckedIn -> Completed
///
/// Completed, Cancelled, and NoShow are terminal states.
///
/// Only Confirmed and CheckedIn bookings block availability
/// (see the overlap predicate used during booking creation).
/// Bookings are created directly as Confirmed — there is no Pending state.
/// </summary>
public enum BookingStatus
{
    Confirmed = 0,
    CheckedIn = 1,
    Completed = 2,
    Cancelled = 3,
    NoShow = 4
}
