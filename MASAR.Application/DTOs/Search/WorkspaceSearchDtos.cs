using Masar.Domain.Enums;

namespace Masar.Application.DTOs.Search;

// Bound via [FromQuery] — a plain class with settable properties, not a
// record, since query-string binding of optional scalars and a repeated
// list parameter (amenityIds=1&amenityIds=2) is more predictable this way
// than through a positional record constructor.
public class WorkspaceSearchRequest
{
    public int? LocationId { get; set; }
    public DateOnly? Date { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public int? MinCapacity { get; set; }
    public WorkspaceType? Type { get; set; }
    public List<int>? AmenityIds { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public record WorkspaceSearchItem(
    int Id,
    string Name,
    int LocationId,
    string LocationName,
    int Capacity,
    WorkspaceType Type,
    List<string> Amenities,
    TimeOnly OpeningTime,
    TimeOnly ClosingTime
);

// Page/pageSize/totalCount metadata isn't spelled out in the locked plan
// beyond "add pagination" — this is the ordinary shape a paginated list
// needs so a caller can tell whether there's a next page, not an
// invented feature beyond what was asked.
public record WorkspaceSearchResponse(
    List<WorkspaceSearchItem> Items,
    int Page,
    int PageSize,
    int TotalCount
);
