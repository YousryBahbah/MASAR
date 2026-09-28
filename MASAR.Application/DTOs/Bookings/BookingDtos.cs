using Masar.Domain.Enums;

namespace Masar.Application.DTOs.Bookings;

// Nullable, not plain DateOnly/TimeOnly — a JSON body missing one of
// these fields would otherwise silently deserialize to default(TimeOnly)
// (00:00:00) rather than something the validator can detect as absent.
// Nullable makes "missing" and "midnight" genuinely distinguishable.
public record CreateBookingRequest(int WorkspaceId, DateOnly? Date, TimeOnly? StartTime, TimeOnly? EndTime);

public record BookingResponse(
    int Id,
    int WorkspaceId,
    string WorkspaceName,
    DateOnly Date,
    TimeOnly StartTime,
    TimeOnly EndTime,
    BookingStatus Status,
    // Raw UTC, unlike Date/StartTime/EndTime above — those represent the
    // booking's intended Egypt-local wall-clock slot (worth converting
    // back for the caller); this is an audit-style "this event happened
    // at instant X" timestamp, same category as CreatedAt, where the
    // instant itself is what matters, not a particular timezone's
    // reading of it.
    DateTime? CheckedInAt
);
