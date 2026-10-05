using FluentValidation;
using Masar.Application.Common;
using Masar.Application.DTOs.Maintenance;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Masar.Infrastructure.Services;

public class MaintenancePeriodService : IMaintenancePeriodService
{
    // How many conflicting booking ids to list in the 409 message — enough
    // for a manager to act on without turning an error into a data dump.
    private const int MaxConflictIdsListed = 10;

    private readonly ApplicationDbContext _db;
    private readonly IValidator<CreateMaintenancePeriodRequest> _createValidator;
    private readonly IValidator<UpdateMaintenancePeriodRequest> _updateValidator;

    public MaintenancePeriodService(
        ApplicationDbContext db,
        IValidator<CreateMaintenancePeriodRequest> createValidator,
        IValidator<UpdateMaintenancePeriodRequest> updateValidator)
    {
        _db = db;
        _createValidator = createValidator;
        _updateValidator = updateValidator;
    }

    public async Task<Result<MaintenancePeriodResponse>> CreateAsync(
        string createdByUserId, CreateMaintenancePeriodRequest request)
    {
        var validation = await _createValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return Result<MaintenancePeriodResponse>.Failure(
                "VALIDATION_FAILED", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        var workspace = await _db.Workspaces.FindAsync(request.WorkspaceId);
        if (workspace is null)
        {
            return Result<MaintenancePeriodResponse>.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        var startUtc = EgyptTime.ToUtc(request.StartDate!.Value, request.StartTime!.Value);
        var endUtc = EgyptTime.ToUtc(request.EndDate!.Value, request.EndTime!.Value);

        // The local-time check in the validator can still pass while the UTC
        // instants collapse (the ambiguous fall-back hour). The table's
        // CK_MaintenancePeriod_StartBeforeEnd would then throw — a 500 —
        // so catch it here as the malformed interval it is.
        if (endUtc <= startUtc)
        {
            return Result<MaintenancePeriodResponse>.Failure(
                "VALIDATION_FAILED", "The maintenance start must be before its end.");
        }

        var conflict = await FindActiveBookingConflictAsync(workspace.Id, startUtc, endUtc);
        if (conflict is not null)
        {
            return Result<MaintenancePeriodResponse>.Failure("MAINTENANCE_BOOKING_CONFLICT", conflict);
        }

        var period = new MaintenancePeriod
        {
            WorkspaceId = workspace.Id,
            CreatedByUserId = createdByUserId,
            StartTime = startUtc,
            EndTime = endUtc,
            Reason = request.Reason,
            CreatedAt = DateTime.UtcNow
        };

        _db.MaintenancePeriods.Add(period);
        await _db.SaveChangesAsync();
        return Result<MaintenancePeriodResponse>.Success(ToResponse(period, workspace.Name));
    }

    public async Task<Result<MaintenancePeriodResponse>> UpdateAsync(
        int maintenancePeriodId, UpdateMaintenancePeriodRequest request)
    {
        var validation = await _updateValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return Result<MaintenancePeriodResponse>.Failure(
                "VALIDATION_FAILED", string.Join(" ", validation.Errors.Select(error => error.ErrorMessage)));
        }

        var period = await _db.MaintenancePeriods
            .Include(item => item.Workspace)
            .FirstOrDefaultAsync(item => item.Id == maintenancePeriodId);
        if (period is null)
        {
            return Result<MaintenancePeriodResponse>.Failure(
                "MAINTENANCE_PERIOD_NOT_FOUND", "Maintenance period not found.");
        }

        var startUtc = EgyptTime.ToUtc(request.StartDate!.Value, request.StartTime!.Value);
        var endUtc = EgyptTime.ToUtc(request.EndDate!.Value, request.EndTime!.Value);

        if (endUtc <= startUtc)
        {
            return Result<MaintenancePeriodResponse>.Failure(
                "VALIDATION_FAILED", "The maintenance start must be before its end.");
        }

        var conflict = await FindActiveBookingConflictAsync(period.WorkspaceId, startUtc, endUtc);
        if (conflict is not null)
        {
            return Result<MaintenancePeriodResponse>.Failure("MAINTENANCE_BOOKING_CONFLICT", conflict);
        }

        period.StartTime = startUtc;
        period.EndTime = endUtc;
        period.Reason = request.Reason;
        await _db.SaveChangesAsync();
        return Result<MaintenancePeriodResponse>.Success(ToResponse(period, period.Workspace.Name));
    }

    public async Task<Result<MaintenancePeriodResponse>> GetByIdAsync(int maintenancePeriodId)
    {
        var period = await _db.MaintenancePeriods
            .AsNoTracking()
            .Include(item => item.Workspace)
            .FirstOrDefaultAsync(item => item.Id == maintenancePeriodId);
        if (period is null)
        {
            return Result<MaintenancePeriodResponse>.Failure(
                "MAINTENANCE_PERIOD_NOT_FOUND", "Maintenance period not found.");
        }

        return Result<MaintenancePeriodResponse>.Success(ToResponse(period, period.Workspace.Name));
    }

    public async Task<Result<List<MaintenancePeriodResponse>>> GetByWorkspaceAsync(int workspaceId)
    {
        if (workspaceId <= 0)
        {
            return Result<List<MaintenancePeriodResponse>>.Failure(
                "VALIDATION_FAILED", "workspaceId must be a positive integer.");
        }

        var workspace = await _db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workspaceId);
        if (workspace is null)
        {
            return Result<List<MaintenancePeriodResponse>>.Failure("WORKSPACE_NOT_FOUND", "Workspace not found.");
        }

        var periods = await _db.MaintenancePeriods
            .AsNoTracking()
            .Where(item => item.WorkspaceId == workspaceId)
            .OrderBy(item => item.StartTime)
            .ThenBy(item => item.Id)
            .ToListAsync();
        return Result<List<MaintenancePeriodResponse>>.Success(
            periods.Select(item => ToResponse(item, workspace.Name)).ToList());
    }

    public async Task<Result<bool>> DeleteAsync(int maintenancePeriodId)
    {
        var period = await _db.MaintenancePeriods.FindAsync(maintenancePeriodId);
        if (period is null)
        {
            return Result<bool>.Failure(
                "MAINTENANCE_PERIOD_NOT_FOUND", "Maintenance period not found.");
        }

        _db.MaintenancePeriods.Remove(period);
        await _db.SaveChangesAsync();
        return Result<bool>.Success(true);
    }

    // Mirror image of the maintenance check BookingService.CreateAsync runs:
    // booking creation refuses to land inside a maintenance period, so
    // maintenance must refuse to land on top of a live booking — otherwise
    // the workspace ends up "under maintenance" and "booked" at once.
    //
    // Only Confirmed/CheckedIn bookings that haven't ended yet count. A past
    // or finished booking is history, not a live reservation, so it must not
    // block recording maintenance that (for example) already happened.
    //
    // This is an ordinary pre-check, not a SERIALIZABLE transaction — the
    // same deliberate scope as the maintenance pre-check in BookingService
    // (the named maintenance-vs-booking race from Step 12). A booking created
    // in the instant between this check and the insert is the one gap left.
    //
    // It never cancels bookings itself: an administrative operation should
    // not silently mutate another user's reservation as a side effect.
    private async Task<string?> FindActiveBookingConflictAsync(
        int workspaceId, DateTime startUtc, DateTime endUtc)
    {
        var now = DateTime.UtcNow;

        var ids = await _db.Bookings
            .AsNoTracking()
            .Where(b => b.WorkspaceId == workspaceId
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
                && b.EndTime > now
                && b.StartTime < endUtc && b.EndTime > startUtc)
            .OrderBy(b => b.StartTime)
            .ThenBy(b => b.Id)
            .Select(b => b.Id)
            .Take(MaxConflictIdsListed + 1)
            .ToListAsync();

        if (ids.Count == 0)
        {
            return null;
        }

        var listed = string.Join(", ", ids.Take(MaxConflictIdsListed));
        var more = ids.Count > MaxConflictIdsListed ? " and more" : string.Empty;
        return $"This maintenance period overlaps active bookings (booking ids: {listed}{more}). " +
               "Those bookings must be cancelled by their owners before maintenance can be scheduled.";
    }

    private static MaintenancePeriodResponse ToResponse(MaintenancePeriod period, string workspaceName)
    {
        var (startDate, startTime) = EgyptTime.FromUtc(period.StartTime);
        var (endDate, endTime) = EgyptTime.FromUtc(period.EndTime);
        return new MaintenancePeriodResponse(
            period.Id,
            period.WorkspaceId,
            workspaceName,
            startDate,
            startTime,
            endDate,
            endTime,
            period.Reason,
            period.CreatedAt);
    }
}
