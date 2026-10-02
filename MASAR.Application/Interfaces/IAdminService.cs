using Masar.Application.Common;
using Masar.Application.DTOs.Admin;

namespace Masar.Application.Interfaces;

public interface IAdminService
{
    Task<Result<AdminUserListResponse>> GetUsersAsync(AdminUserListRequest request);
    Task<Result<AdminUserResponse>> GetUserByIdAsync(string userId);
    Task<Result<AdminUserResponse>> UpdateUserRolesAsync(
        string actorUserId, string targetUserId, UpdateUserRolesRequest request);
    Task<Result<AdminUserResponse>> SetUserActiveAsync(
        string actorUserId, string targetUserId, bool isActive);
}
