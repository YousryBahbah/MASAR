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
                booking.Id, workspace.Id, workspace.Name, date, startTime, endTime, booking.Status));
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
