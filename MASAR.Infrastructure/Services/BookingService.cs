using System.Data;
using FluentValidation;
using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Services;

public class BookingService : IBookingService
{
    // Active-booking limit is locked at 2 (Step 2 recap). "Active" means
    // Confirmed/CheckedIn AND not yet ended — see the Steps 11/12 plan
    // for why: no background completion job exists yet, so an unbounded
    // definition would count a user's already-finished bookings against
    // them forever.
    private const int ActiveBookingLimit = 2;

    // Public: BookingLifecycleJobs.SweepNoShowsAsync references this
    // directly so the check-in window's upper bound and the no-show
    // sweep's cutoff can never independently drift apart — see the
    // Step 13 roadmap for the exact contradiction that caused
    // ("EndTime as a stated upper bound was never actually reachable")
    // when these were two separately-set values instead of one.
    // Proposed value, not a locked business rule — see the roadmap.
    public static readonly TimeSpan CheckInGracePeriod = TimeSpan.FromMinutes(15);

    private readonly ApplicationDbContext _db;
    private readonly IValidator<CreateBookingRequest> _validator;
    private readonly ILogger<BookingService> _logger;

    public BookingService(
        ApplicationDbContext db,
        IValidator<CreateBookingRequest> validator,
        ILogger<BookingService> logger)
    {
        _db = db;
        _validator = validator;
        _logger = logger;
    }

    public async Task<Result<BookingResponse>> CreateAsync(string userId, CreateBookingRequest request)
    {
        var validation = await _validator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            var message = string.Join(" ", validation.Errors.Select(e => e.ErrorMessage));
            return Result<BookingResponse>.Failure("VALIDATION_FAILED", message);
        }

        var date = request.Date!.Value;
        var startTime = request.StartTime!.Value;
        var endTime = request.EndTime!.Value;

        var workspace = await _db.Workspaces
            .Include(w => w.Location)
            .FirstOrDefaultAsync(w => w.Id == request.WorkspaceId);

        if (workspace is null)
        {
            return Result<BookingResponse>.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        if (workspace.Status != WorkspaceStatus.Available)
        {
            return Result<BookingResponse>.Failure("WORKSPACE_INACTIVE", "Workspace is not available.");
        }

        // Same check Search already applies as a filter — here it's a
        // rejection of a specific request, not a list exclusion. Without
        // it, POST /api/bookings would let someone book a workspace
        // under a deactivated location directly.
        if (!workspace.Location.IsActive)
        {
            return Result<BookingResponse>.Failure("LOCATION_INACTIVE", "Location is inactive.");
        }

        var requestStartUtc = EgyptTime.ToUtc(date, startTime);
        var requestEndUtc = EgyptTime.ToUtc(date, endTime);

        // 422: impossible against the current clock — no other row
        // changing could make this exact request succeed later.
        if (requestStartUtc <= DateTime.UtcNow)
        {
            return Result<BookingResponse>.Failure(
                "BOOKING_IN_PAST", "Booking start time must be in the future.");
        }

        // 422: impossible against this workspace's operating hours —
        // only editing the workspace itself (a rare administrative
        // reconfiguration, not routine operation) would change this.
        if (startTime < workspace.OpeningTime || endTime > workspace.ClosingTime)
        {
            return Result<BookingResponse>.Failure(
                "OUTSIDE_OPERATING_HOURS", "Requested time is outside operating hours.");
        }

        // 409, ordinary pre-check, not inside the transaction — see the
        // locked Step 12 scope decision (explicit narrow walk-back) and
        // its named maintenance-vs-booking race.
        var maintenanceConflict = await _db.MaintenancePeriods.AnyAsync(mp =>
            mp.WorkspaceId == workspace.Id
            && mp.StartTime < requestEndUtc && mp.EndTime > requestStartUtc);

        if (maintenanceConflict)
        {
            return Result<BookingResponse>.Failure(
                "MAINTENANCE_CONFLICT", "Workspace is under maintenance during this time.");
        }

        // 409, ordinary pre-check, not inside the transaction — see the
        // locked Step 12 scope decision and its named same-user race.
        var activeBookingCount = await _db.Bookings.CountAsync(b =>
            b.UserId == userId
            && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
            && b.EndTime > DateTime.UtcNow);

        if (activeBookingCount >= ActiveBookingLimit)
        {
            return Result<BookingResponse>.Failure(
                "ACTIVE_BOOKING_LIMIT_EXCEEDED",
                "You have reached the maximum number of active bookings.");
        }

        // Concurrency boundary — SERIALIZABLE, scoped to only the
        // overlap re-check and the insert. This is the flagship
        // scenario: two Members racing for the exact same slot. See the
        // locked Step 12 plan for why this scope is deliberately
        // narrower than an earlier, wider version.
        try
        {
            await using var transaction =
                await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var overlapExists = await _db.Bookings.AnyAsync(b =>
                b.WorkspaceId == workspace.Id
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
                && b.StartTime < requestEndUtc && b.EndTime > requestStartUtc);

            if (overlapExists)
            {
                await transaction.RollbackAsync();
                return Result<BookingResponse>.Failure(
                    "WORKSPACE_UNAVAILABLE", "This workspace is already booked for the requested time.");
            }

            var booking = new Booking
            {
                UserId = userId,
                WorkspaceId = workspace.Id,
                StartTime = requestStartUtc,
                EndTime = requestEndUtc,
                Status = BookingStatus.Confirmed,
                CreatedAt = DateTime.UtcNow
            };

            _db.Bookings.Add(booking);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation(
                "Booking {BookingId} created for user {UserId} on workspace {WorkspaceId}.",
                booking.Id, userId, workspace.Id);

            return Result<BookingResponse>.Success(new BookingResponse(
                booking.Id, workspace.Id, workspace.Name, date, startTime, endTime,
                booking.Status, booking.CheckedInAt));
        }
        catch (Exception ex) when (IsDeadlock(ex))
        {
            // Genuine SQL Server deadlock (error 1205), not a business
            // conflict. This exact "read a range under SERIALIZABLE,
            // then insert into it" shape is a textbook cause of
            // deadlocks: both transactions can acquire a compatible
            // SHARED range lock during the overlap-check SELECT
            // simultaneously, then both need to upgrade to an EXCLUSIVE
            // lock for the insert — each blocked on the other's shared
            // lock. This is not a rare edge case for this code pattern.
            //
            // Caught as a bare SqlException OR as SqlException wrapped
            // inside DbUpdateException — the deadlock described above
            // happens at the INSERT, which goes through SaveChangesAsync,
            // and EF Core wraps provider failures from that path in
            // DbUpdateException rather than letting the raw SqlException
            // through. Catching only the bare SqlException would miss
            // the exact scenario this handler exists for.
            //
            // Per the locked Step 3E decision: infrastructure failure,
            // not retried automatically in v1, logged and surfaced
            // distinctly from WORKSPACE_UNAVAILABLE so a deadlock is
            // never mistaken for — or masked as — a real business
            // conflict.
            _logger.LogWarning(
                ex, "Deadlock detected creating a booking for workspace {WorkspaceId}.", workspace.Id);

            return Result<BookingResponse>.Failure(
                "CONCURRENT_WRITE_CONFLICT",
                "A temporary conflict occurred while processing this booking. Please try again.");
        }
    }

    public async Task<Result<BookingResponse>> CheckInAsync(string userId, int bookingId)
    {
        // Ownership filtered directly into the query, not checked after
        // a plain-by-id lookup — a booking that exists but belongs to
        // someone else and a booking that doesn't exist at all produce
        // the exact same query result (no row) and the exact same error
        // below, by construction, not by a separate comparison that
        // could accidentally diverge. Same enumeration-avoidance
        // reasoning as Auth's shared INVALID_CREDENTIALS.
        var booking = await _db.Bookings
            .Include(b => b.Workspace)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.UserId == userId);

        if (booking is null)
        {
            return Result<BookingResponse>.Failure("BOOKING_NOT_FOUND", "Booking not found.");
        }

        // 409: coherent request, but conflicts with this booking's
        // current mutable status — check-in only makes sense from
        // Confirmed. Already CheckedIn/Completed/Cancelled/NoShow all
        // land here rather than getting their own distinct codes; the
        // client doesn't need to know which wrong status it was in,
        // only that check-in isn't valid from wherever it currently is.
        if (booking.Status != BookingStatus.Confirmed)
        {
            return Result<BookingResponse>.Failure(
                "INVALID_BOOKING_STATUS", "This booking cannot be checked into from its current status.");
        }

        var now = DateTime.UtcNow;
        var windowStart = booking.StartTime - CheckInGracePeriod;
        // Capped at EndTime, not just Start + CheckInGracePeriod — a
        // booking shorter than the grace period (nothing currently rules
        // this out) would otherwise accept a check-in after it already
        // ended. Checking into something already over is incoherent
        // regardless of how the 15-minute figure itself gets set later.
        var windowEnd = booking.StartTime + CheckInGracePeriod < booking.EndTime
            ? booking.StartTime + CheckInGracePeriod
            : booking.EndTime;

        // 422: impossible against the current clock specifically — the
        // booking's status isn't the problem, its own StartTime/EndTime
        // are, same bucket as BOOKING_IN_PAST at creation time.
        if (now < windowStart || now >= windowEnd)
        {
            return Result<BookingResponse>.Failure(
                "OUTSIDE_CHECKIN_WINDOW",
                $"Check-in is only available within {CheckInGracePeriod.TotalMinutes:0} minutes of the booking's start time, and not after it has ended.");
        }

        // The actual state transition — a single conditional UPDATE, not
        // load-then-SaveChanges. Everything above (existence, ownership,
        // status, window) is an ordinary pre-check exactly like
        // CreateAsync's maintenance/limit checks; only this specific
        // write needs to be race-safe, because it's the one step
        // competing directly against SweepNoShowsAsync, which can flip
        // Confirmed -> NoShow at any instant with no coordination.
        // Matching the WHERE clause's Status = Confirmed against the
        // UPDATE itself (not just the earlier SELECT above) is what
        // actually closes the race — a plain tracked-entity SaveChanges
        // would generate `WHERE Id = X` with no status guard at all,
        // and could silently overwrite whatever the sweep just wrote.
        var rowsAffected = await _db.Bookings
            .Where(b => b.Id == bookingId && b.UserId == userId && b.Status == BookingStatus.Confirmed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(b => b.Status, BookingStatus.CheckedIn)
                .SetProperty(b => b.CheckedInAt, now)
                .SetProperty(b => b.UpdatedAt, now));
        // CheckInMethod deliberately untouched here — this endpoint takes
        // no QR token or other proof of method, so recording QR would be
        // false. Left null rather than invented; add a real value once
        // an actual check-in mechanism (QR or otherwise) exists to prove.

        if (rowsAffected == 0)
        {
            // Lost the race: the pre-check above saw Confirmed, but
            // something else (almost certainly the NoShow sweep landing
            // at the same instant) changed it first. Same error code a
            // straightforward wrong-status attempt gets — the caller
            // doesn't need to know it was specifically a race.
            return Result<BookingResponse>.Failure(
                "INVALID_BOOKING_STATUS", "This booking cannot be checked into from its current status.");
        }

        _logger.LogInformation("Booking {BookingId} checked in by user {UserId}.", booking.Id, userId);

        var (startDate, startLocal) = EgyptTime.FromUtc(booking.StartTime);
        var (_, endLocal) = EgyptTime.FromUtc(booking.EndTime);

        // Built from the values just written, not re-read from `booking`
        // — ExecuteUpdateAsync bypasses the change tracker entirely, so
        // the in-memory `booking` object still shows its pre-update
        // Status/CheckedInAt (stale) rather than what's now actually in
        // the database.
        return Result<BookingResponse>.Success(new BookingResponse(
            booking.Id, booking.WorkspaceId, booking.Workspace.Name,
            startDate, startLocal, endLocal, BookingStatus.CheckedIn, now));
    }

    public async Task<Result<BookingResponse>> CancelAsync(string userId, int bookingId)
    {
        // Same ownership-filtered-into-the-query reasoning as CheckInAsync.
        var booking = await _db.Bookings
            .Include(b => b.Workspace)
            .FirstOrDefaultAsync(b => b.Id == bookingId && b.UserId == userId);

        if (booking is null)
        {
            return Result<BookingResponse>.Failure("BOOKING_NOT_FOUND", "Booking not found.");
        }

        // Confirmed -> Cancelled only — deliberately NOT allowed from
        // CheckedIn (Step 13 roadmap). Once someone has actually shown
        // up and checked in, "cancel" no longer describes what would be
        // happening; that's a different, unmodeled operation, not a
        // wider version of this one.
        if (booking.Status != BookingStatus.Confirmed)
        {
            return Result<BookingResponse>.Failure(
                "INVALID_BOOKING_STATUS", "This booking cannot be cancelled from its current status.");
        }

        var now = DateTime.UtcNow;

        // Same atomic-conditional-UPDATE reasoning as CheckInAsync — this
        // races against SweepNoShowsAsync exactly the same way (a user
        // cancelling right as the sweep marks the same booking NoShow),
        // so the same fix applies: the WHERE clause's Status = Confirmed
        // has to be checked by the UPDATE itself, not just the read above.
        var rowsAffected = await _db.Bookings
            .Where(b => b.Id == bookingId && b.UserId == userId && b.Status == BookingStatus.Confirmed)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(b => b.Status, BookingStatus.Cancelled)
                .SetProperty(b => b.UpdatedAt, now));

        if (rowsAffected == 0)
        {
            return Result<BookingResponse>.Failure(
                "INVALID_BOOKING_STATUS", "This booking cannot be cancelled from its current status.");
        }

        _logger.LogInformation("Booking {BookingId} cancelled by user {UserId}.", booking.Id, userId);

        var (startDate, startLocal) = EgyptTime.FromUtc(booking.StartTime);
        var (_, endLocal) = EgyptTime.FromUtc(booking.EndTime);

        // CheckedInAt is guaranteed null here — only a booking that was
        // still Confirmed at the moment of this write can reach this
        // point, and a Confirmed booking has never been checked in.
        return Result<BookingResponse>.Success(new BookingResponse(
            booking.Id, booking.WorkspaceId, booking.Workspace.Name,
            startDate, startLocal, endLocal, BookingStatus.Cancelled, null));
    }

    // SQL Server error 1205 = deadlock victim. May arrive as a bare
    // SqlException (e.g. from a query outside SaveChanges) or wrapped in
    // DbUpdateException.InnerException (from SaveChanges itself) — check
    // both shapes rather than assuming which one a given failure takes.
    // Walks the FULL exception chain looking for SQL Server error 1205
    // (deadlock victim), rather than matching a fixed nesting shape.
    // A real run surfaced a third layer this didn't originally account
    // for: EF Core's default (non-retrying) execution strategy wraps a
    // transient-looking failure — deadlocks included — in its own
    // InvalidOperationException on top of DbUpdateException on top of
    // the actual SqlException. Matching only "bare SqlException" or
    // "DbUpdateException wrapping SqlException" missed this real case.
    // Walking .InnerException generically, rather than hardcoding a
    // specific depth, means this doesn't need to be revisited again if
    // EF Core or the SQL client adds yet another wrapper layer later.
    //
    // Do NOT "fix" this by adding EnableRetryOnFailure() to UseSqlServer
    // instead, even though that's what the wrapped exception's own
    // message suggests — EF Core's retrying execution strategy explicitly
    // refuses to run inside a manually-managed transaction like this
    // SERIALIZABLE one, and retries were already deliberately kept out
    // of v1's scope (Step 3E).
    private static bool IsDeadlock(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is Microsoft.Data.SqlClient.SqlException { Number: 1205 })
            {
                return true;
            }
        }

        return false;
    }
}
