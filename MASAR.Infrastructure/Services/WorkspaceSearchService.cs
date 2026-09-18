using Masar.Application.Common;
using Masar.Application.DTOs.Search;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Masar.Infrastructure.Services;

public class WorkspaceSearchService : IWorkspaceSearchService
{
    private readonly ApplicationDbContext _db;

    public WorkspaceSearchService(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Result<WorkspaceSearchResponse>> SearchAsync(WorkspaceSearchRequest request)
    {
        // date/startTime/endTime are required together — a partial time
        // filter is a malformed request, not "search without a time
        // filter" (locked Step 10 plan). Checked before anything else
        // touches the DB.
        var timeFieldsProvided = new[]
        {
            request.Date.HasValue, request.StartTime.HasValue, request.EndTime.HasValue
        };
        var providedCount = timeFieldsProvided.Count(p => p);
        if (providedCount != 0 && providedCount != 3)
        {
            return Result<WorkspaceSearchResponse>.Failure(
                "VALIDATION_FAILED",
                "date, startTime, and endTime must all be provided together, or not at all.");
        }

        var hasTimeFilter = providedCount == 3;

        // Deviates from the plan's literal text, deliberately: the plan
        // says 422 here, but Step 8 already corrected the identical
        // "interval bounds in the wrong order" check (OpeningTime vs
        // ClosingTime) from 422 to 400/VALIDATION_FAILED, specifically
        // citing Booking's own StartTime < EndTime as 400-level
        // precedent. Making this one 422 would recreate the exact
        // inconsistency that correction just closed. Flagged in the
        // wiring notes — the plan document itself still says 422 and
        // should be updated to match.
        if (hasTimeFilter && request.StartTime!.Value >= request.EndTime!.Value)
        {
            return Result<WorkspaceSearchResponse>.Failure(
                "VALIDATION_FAILED", "startTime must be before endTime.");
        }

        // A negative/zero locationId or minCapacity isn't a filter that
        // legitimately matches nothing — it's a malformed value someone
        // typed by mistake, and silently returning an empty page for it
        // hides that instead of telling them. Same reasoning applies to
        // a negative id inside amenityIds.
        if (request.LocationId is <= 0)
        {
            return Result<WorkspaceSearchResponse>.Failure(
                "VALIDATION_FAILED", "locationId must be a positive integer.");
        }

        if (request.MinCapacity is <= 0)
        {
            return Result<WorkspaceSearchResponse>.Failure(
                "VALIDATION_FAILED", "minCapacity must be a positive integer.");
        }

        if (request.AmenityIds is { Count: > 0 } && request.AmenityIds.Any(id => id <= 0))
        {
            return Result<WorkspaceSearchResponse>.Failure(
                "VALIDATION_FAILED", "amenityIds must all be positive integers.");
        }

        var page = request.Page <= 0 ? 1 : request.Page;
        var pageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, 50);

        // (page - 1) * pageSize overflows plain int arithmetic for a
        // large page (e.g. ?page=2147483647) — silently wraps instead
        // of throwing, and Skip() would receive garbage, likely a
        // negative OFFSET SQL Server rejects outright. Compute in long,
        // then clamp back to int range before Skip() (which only
        // accepts int) — no real dataset needs to skip more than
        // int.MaxValue rows, so the clamp is a safe no-op in practice.
        var skipLong = (long)(page - 1) * pageSize;
        var skip = skipLong > int.MaxValue ? int.MaxValue : (int)skipLong;

        var amenityIds = (request.AmenityIds ?? []).Distinct().ToList();

        var query = _db.Workspaces
            .Where(w => w.Status == WorkspaceStatus.Available)
            // Workspace.Status and Location.IsActive are independent —
            // a workspace under a deactivated location must not surface
            // in search results (locked Step 10 addition).
            .Where(w => w.Location.IsActive);

        if (request.LocationId.HasValue)
        {
            query = query.Where(w => w.LocationId == request.LocationId!.Value);
        }

        if (request.MinCapacity.HasValue)
        {
            query = query.Where(w => w.Capacity >= request.MinCapacity!.Value);
        }

        if (request.Type.HasValue)
        {
            query = query.Where(w => w.Type == request.Type!.Value);
        }

        if (amenityIds.Count > 0)
        {
            // Same "count matched against distinct requested" idiom as
            // AmenityService's all-or-nothing check — "has ALL of these
            // amenities", not "has any of them".
            query = query.Where(w =>
                amenityIds.Count == w.WorkspaceAmenities.Count(wa => amenityIds.Contains(wa.AmenityId)));
        }

        if (hasTimeFilter)
        {
            var startTime = request.StartTime!.Value;
            var endTime = request.EndTime!.Value;

            // Operating hours are plain local wall-clock times — no
            // timezone conversion needed for this comparison.
            query = query.Where(w => w.OpeningTime <= startTime && endTime <= w.ClosingTime);

            // MaintenancePeriod/Booking are stored in UTC; the request is
            // Egypt-local. See EgyptTime for why this isn't a hardcoded
            // offset.
            var requestStartUtc = EgyptTime.ToUtc(request.Date!.Value, startTime);
            var requestEndUtc = EgyptTime.ToUtc(request.Date!.Value, endTime);

            query = query.Where(w => !w.MaintenancePeriods.Any(mp =>
                mp.StartTime < requestEndUtc && mp.EndTime > requestStartUtc));

            // Exact same overlap predicate as the locked booking-overlap
            // rule, reused verbatim as a read-only filter (locked Step 10
            // plan) — only Confirmed/CheckedIn bookings block a slot.
            query = query.Where(w => !w.Bookings.Any(b =>
                (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
                && b.StartTime < requestEndUtc && b.EndTime > requestStartUtc));
        }

        var totalCount = await query.CountAsync();

        // Deterministic ordering before Skip/Take — SQL Server's
        // OFFSET/FETCH NEXT requires an ORDER BY, and an implicit one
        // isn't reliable across separate query executions (locked Step
        // 10 plan). Id is the obvious unique, stable tiebreaker.
        var workspaces = await query
            .Include(w => w.Location)
            .Include(w => w.WorkspaceAmenities)
                .ThenInclude(wa => wa.Amenity)
            .OrderBy(w => w.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        // Materialize first, then map — same reasoning as
        // LocationService/AmenityService.GetAllAsync.
        var items = workspaces.Select(w => new WorkspaceSearchItem(
            w.Id,
            w.Name,
            w.LocationId,
            w.Location.Name,
            w.Capacity,
            w.Type,
            w.WorkspaceAmenities.Select(wa => wa.Amenity.Name).OrderBy(n => n).ToList(),
            w.OpeningTime,
            w.ClosingTime
        )).ToList();

        return Result<WorkspaceSearchResponse>.Success(
            new WorkspaceSearchResponse(items, page, pageSize, totalCount));
    }
}
