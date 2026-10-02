namespace Masar.Application.DTOs.Admin;

public class AdminUserListRequest
{
    public bool? IsActive { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public record UpdateUserRolesRequest(List<string>? Roles);

public record AdminUserResponse(
    string Id,
    string Email,
    string FirstName,
    string LastName,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTime CreatedAt
);

public record AdminUserListResponse(
    List<AdminUserResponse> Items,
    int Page,
    int PageSize,
    int TotalCount
);
