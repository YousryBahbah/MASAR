namespace Masar.Domain.Enums;

/// <summary>
/// Identity role names used across the API (seeded into AspNetRoles).
/// Not an enum backed by a column — ASP.NET Core Identity manages roles
/// as strings via UserManager/RoleManager, this is just a typo-proof
/// reference to the three role names.
/// </summary>
public static class Roles
{
    public const string Member = nameof(Member);
    public const string WorkspaceManager = nameof(WorkspaceManager);
    public const string Admin = nameof(Admin);
}
