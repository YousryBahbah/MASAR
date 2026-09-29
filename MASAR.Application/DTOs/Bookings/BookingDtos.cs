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

// Step 14 — bound via [FromQuery], same reasoning as
// WorkspaceSearchRequest (Step 10): a plain class with settable
// properties binds optional scalars more predictably than a positional
// record constructor does for query strings.
public class BookingHistoryRequest
{
    // Unfiltered by default (every one of the caller's own bookings,
    // any status). Filtering to one specific status is the only
    // filter Step 14 locks in — no date-range filter, per the
    // roadmap's own list of what still needed deciding; add one later
    // if it turns out to be needed, rather than guessing at its shape
    // now.
    public BookingStatus? Status { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

// Same page/pageSize/totalCount shape as WorkspaceSearchResponse
// (Step 10) — no reason for two different pagination envelopes to
// exist in one API.
public record BookingHistoryResponse(
    List<BookingResponse> Items,
    int Page,
    int PageSize,
    int TotalCount
);
