using Microsoft.AspNetCore.Identity;
using Masar.Domain.Enums;

namespace Masar.Infrastructure.Persistence.Seed;

/// <summary>
/// Ensures the three application roles exist. Call once at startup
/// (see Program.cs) — idempotent, safe to run on every boot.
/// </summary>
public static class RoleSeeder
{
    private static readonly string[] AllRoles =
    {
        Roles.Member,
        Roles.WorkspaceManager,
        Roles.Admin
    };

    public static async Task SeedAsync(RoleManager<IdentityRole> roleManager)
    {
        foreach (var roleName in AllRoles)
        {
            if (await roleManager.RoleExistsAsync(roleName))
            {
                continue;
            }

            var result = await roleManager.CreateAsync(new IdentityRole(roleName));
            if (!result.Succeeded)
            {
                // Fail fast: a missing role silently breaks role-based
                // authorization much later, far from this actual cause.
                // Better to crash at startup with a clear reason.
                var message = string.Join(" ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException(
                    $"Failed to seed role '{roleName}': {message}");
            }
        }
    }
}
