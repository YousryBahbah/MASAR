using System.Data;
using Masar.Application.Common;
using Masar.Application.DTOs.Admin;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Services;

public class AdminService : IAdminService
{
    private static readonly HashSet<string> AllowedRoles =
        [Roles.Member, Roles.WorkspaceManager, Roles.Admin];

    private readonly ApplicationDbContext _db;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<AdminService> _logger;

    public AdminService(
        ApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        ILogger<AdminService> logger)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task<Result<AdminUserListResponse>> GetUsersAsync(AdminUserListRequest request)
    {
        var page = request.Page <= 0 ? 1 : request.Page;
        var pageSize = request.PageSize <= 0 ? 20 : Math.Min(request.PageSize, 50);
        var skipLong = (long)(page - 1) * pageSize;
        var skip = skipLong > int.MaxValue ? int.MaxValue : (int)skipLong;

        var query = _db.Users.AsNoTracking();
        if (request.IsActive.HasValue)
        {
            query = query.Where(user => user.IsActive == request.IsActive.Value);
        }

        var totalCount = await query.CountAsync();
        var users = await query
            .OrderBy(user => user.Email)
            .ThenBy(user => user.Id)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync();

        // One query for the whole page's roles instead of one
        // GetRolesAsync round trip per user (up to 50 per page).
        var userIds = users.Select(user => user.Id).ToList();
        var roleRows = await (
            from userRole in _db.UserRoles.AsNoTracking()
            join role in _db.Roles.AsNoTracking() on userRole.RoleId equals role.Id
            where userIds.Contains(userRole.UserId)
            select new { userRole.UserId, RoleName = role.Name }).ToListAsync();

        var rolesByUser = roleRows
            .Where(row => row.RoleName is not null)
            .GroupBy(row => row.UserId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<string>)group
                    .Select(row => row.RoleName!)
                    .OrderBy(name => name, StringComparer.Ordinal)
                    .ToList());

        var items = users
            .Select(user => ToResponse(
                user,
                rolesByUser.TryGetValue(user.Id, out var roles) ? roles : Array.Empty<string>()))
            .ToList();

        return Result<AdminUserListResponse>.Success(
            new AdminUserListResponse(items, page, pageSize, totalCount));
    }

    public async Task<Result<AdminUserResponse>> GetUserByIdAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return Result<AdminUserResponse>.Failure("USER_NOT_FOUND", "User not found.");
        }

        return Result<AdminUserResponse>.Success(
            ToResponse(user, (await _userManager.GetRolesAsync(user)).ToList()));
    }

    public async Task<Result<AdminUserResponse>> UpdateUserRolesAsync(
        string actorUserId, string targetUserId, UpdateUserRolesRequest request)
    {
        if (actorUserId == targetUserId)
        {
            return Result<AdminUserResponse>.Failure(
                "SELF_SERVICE_NOT_ALLOWED", "Administrators cannot change their own roles.");
        }

        var requestedRoles = request?.Roles?.Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (requestedRoles.Count == 0 || requestedRoles.Any(role => !AllowedRoles.Contains(role)))
        {
            return Result<AdminUserResponse>.Failure(
                "VALIDATION_FAILED", "Provide one or more valid application roles.");
        }

        try
        {
            await using var transaction =
                await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var user = await _userManager.FindByIdAsync(targetUserId);
            if (user is null)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure("USER_NOT_FOUND", "User not found.");
            }

            var currentRoles = await _userManager.GetRolesAsync(user);

            // Nothing to change — return the current state untouched. Rotating
            // the security stamp here would log the user out for no reason.
            if (currentRoles.Count == requestedRoles.Count &&
                !currentRoles.Except(requestedRoles, StringComparer.Ordinal).Any())
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Success(ToResponse(user, currentRoles.ToList()));
            }

            if (user.IsActive && currentRoles.Contains(Roles.Admin) &&
                !requestedRoles.Contains(Roles.Admin) && await IsLastActiveAdminAsync())
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "LAST_ACTIVE_ADMIN_PROTECTED", "The final active administrator cannot lose the Admin role.");
            }

            var removeResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
            if (!removeResult.Succeeded)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "ROLE_UPDATE_FAILED", string.Join(" ", removeResult.Errors.Select(error => error.Description)));
            }

            var addResult = await _userManager.AddToRolesAsync(user, requestedRoles);
            if (!addResult.Succeeded)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "ROLE_UPDATE_FAILED", string.Join(" ", addResult.Errors.Select(error => error.Description)));
            }

            var stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "ROLE_UPDATE_FAILED", string.Join(" ", stampResult.Errors.Select(error => error.Description)));
            }

            await transaction.CommitAsync();

            _logger.LogInformation(
                "Roles for user {TargetUserId} changed by admin {ActorUserId}.", targetUserId, actorUserId);

            return Result<AdminUserResponse>.Success(
                ToResponse(user, requestedRoles.OrderBy(name => name, StringComparer.Ordinal).ToList()));
        }
        catch (Exception exception) when (SqlServerErrors.IsDeadlock(exception))
        {
            _logger.LogWarning(exception,
                "Deadlock detected changing roles of user {TargetUserId} (admin {ActorUserId}).",
                targetUserId, actorUserId);

            return Result<AdminUserResponse>.Failure(
                "CONCURRENT_ADMIN_CHANGE", "A concurrent administrative update occurred. Please try again.");
        }
    }

    public async Task<Result<AdminUserResponse>> SetUserActiveAsync(
        string actorUserId, string targetUserId, bool isActive)
    {
        if (actorUserId == targetUserId)
        {
            return Result<AdminUserResponse>.Failure(
                "SELF_SERVICE_NOT_ALLOWED", "Administrators cannot change their own account state.");
        }

        try
        {
            await using var transaction =
                await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var user = await _userManager.FindByIdAsync(targetUserId);
            if (user is null)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure("USER_NOT_FOUND", "User not found.");
            }

            var currentRoles = await _userManager.GetRolesAsync(user);

            // Already in the requested state — nothing to do. Checked before
            // the last-admin guard so re-deactivating an already-inactive
            // admin isn't wrongly blocked, and before the security-stamp
            // rotation so a no-op doesn't log the user out.
            if (user.IsActive == isActive)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Success(ToResponse(user, currentRoles.ToList()));
            }

            if (!isActive && currentRoles.Contains(Roles.Admin) && await IsLastActiveAdminAsync())
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "LAST_ACTIVE_ADMIN_PROTECTED", "The final active administrator cannot be deactivated.");
            }

            user.IsActive = isActive;
            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "USER_UPDATE_FAILED", string.Join(" ", updateResult.Errors.Select(error => error.Description)));
            }

            var stampResult = await _userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
            {
                await transaction.RollbackAsync();
                return Result<AdminUserResponse>.Failure(
                    "USER_UPDATE_FAILED", string.Join(" ", stampResult.Errors.Select(error => error.Description)));
            }

            // POLICY: deactivating an account cancels its future bookings
            // that haven't started yet, in the same transaction, so the
            // capacity is released immediately instead of sitting blocked by
            // someone who can no longer log in to use it.
            //
            // Deliberately narrow:
            //  - Only Confirmed bookings with StartTime in the future.
            //    A booking already in progress is left alone (the no-show
            //    sweep handles it), and a CheckedIn booking was actually
            //    attended, so it stays as-is.
            //  - Same atomic conditional ExecuteUpdateAsync as the user's own
            //    Cancel (Status = Confirmed is part of the WHERE), so it can't
            //    overwrite a transition that landed a moment earlier.
            //
            // To switch to "preserve bookings" instead, delete this block;
            // nothing else depends on it.
            var cancelledBookings = 0;
            if (!isActive)
            {
                var now = DateTime.UtcNow;
                cancelledBookings = await _db.Bookings
                    .Where(b => b.UserId == targetUserId
                        && b.Status == BookingStatus.Confirmed
                        && b.StartTime > now)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(b => b.Status, BookingStatus.Cancelled)
                        .SetProperty(b => b.UpdatedAt, now));
            }

            await transaction.CommitAsync();

            _logger.LogInformation(
                "User {TargetUserId} {Action} by admin {ActorUserId}; {CancelledCount} future booking(s) cancelled.",
                targetUserId, isActive ? "activated" : "deactivated", actorUserId, cancelledBookings);

            return Result<AdminUserResponse>.Success(ToResponse(user, currentRoles.ToList()));
        }
        catch (Exception exception) when (SqlServerErrors.IsDeadlock(exception))
        {
            _logger.LogWarning(exception,
                "Deadlock detected {Action} user {TargetUserId} (admin {ActorUserId}).",
                isActive ? "activating" : "deactivating", targetUserId, actorUserId);

            return Result<AdminUserResponse>.Failure(
                "CONCURRENT_ADMIN_CHANGE", "A concurrent administrative update occurred. Please try again.");
        }
    }

    private static AdminUserResponse ToResponse(ApplicationUser user, IReadOnlyList<string> roles) => new(
        user.Id,
        user.Email ?? string.Empty,
        user.FirstName,
        user.LastName,
        user.IsActive,
        roles,
        user.CreatedAt);

    private async Task<bool> IsLastActiveAdminAsync()
    {
        var activeAdminCount = await _db.UserRoles
            .Join(_db.Roles,
                userRole => userRole.RoleId,
                role => role.Id,
                (userRole, role) => new { userRole.UserId, RoleName = role.Name })
            .Join(_db.Users,
                userRole => userRole.UserId,
                user => user.Id,
                (userRole, user) => new { userRole.RoleName, user.IsActive })
            .CountAsync(item => item.RoleName == Roles.Admin && item.IsActive);

        return activeAdminCount <= 1;
    }

}
