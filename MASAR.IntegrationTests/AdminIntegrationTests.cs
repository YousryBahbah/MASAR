using Masar.Application.Common;
using Masar.Application.DTOs.Admin;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Masar.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Masar.IntegrationTests;

// Step 15 guarantees, proven against the real AdminService and real SQL
// Server: the rules about WHO may change WHAT, and that stale credentials die
// when an account changes.
[Collection(SqlServerCollection.Name)]
public class AdminIntegrationTests : IAsyncLifetime
{
    public Task InitializeAsync() => TestDatabase.EnsureReadyAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Task<Result<AdminUserResponse>> SetRolesAsync(string actorId, string targetId, params string[] roles) =>
        TestServices.InScopeAsync(services => services.GetRequiredService<IAdminService>()
            .UpdateUserRolesAsync(actorId, targetId, new UpdateUserRolesRequest(roles.ToList())));

    private static Task<Result<AdminUserResponse>> SetActiveAsync(string actorId, string targetId, bool isActive) =>
        TestServices.InScopeAsync(services => services.GetRequiredService<IAdminService>()
            .SetUserActiveAsync(actorId, targetId, isActive));

    // ------------------------------------------------------------ guard rails

    [Fact]
    public async Task An_admin_cannot_change_their_own_roles_or_deactivate_themselves()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);

        var roles = await SetRolesAsync(admin.Id, admin.Id, Roles.Member);
        var deactivate = await SetActiveAsync(admin.Id, admin.Id, false);

        Assert.Equal("SELF_SERVICE_NOT_ALLOWED", roles.ErrorCode);
        Assert.Equal("SELF_SERVICE_NOT_ALLOWED", deactivate.ErrorCode);
        Assert.Equal(new[] { Roles.Admin }, await TestData.RolesOfAsync(admin.Id));
        Assert.True((await TestData.ReloadUserAsync(admin.Id)).IsActive);
    }

    [Fact]
    public async Task Unknown_or_empty_role_lists_are_rejected_and_change_nothing()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();

        var unknown = await SetRolesAsync(admin.Id, member.Id, "SuperUser");
        var empty = await SetRolesAsync(admin.Id, member.Id);
        var mixed = await SetRolesAsync(admin.Id, member.Id, Roles.Admin, "Nope");

        Assert.Equal("VALIDATION_FAILED", unknown.ErrorCode);
        Assert.Equal("VALIDATION_FAILED", empty.ErrorCode);
        Assert.Equal("VALIDATION_FAILED", mixed.ErrorCode);
        Assert.Equal(new[] { Roles.Member }, await TestData.RolesOfAsync(member.Id));
    }

    [Fact]
    public async Task An_unknown_target_user_is_reported_as_not_found()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);

        var roles = await SetRolesAsync(admin.Id, "does-not-exist", Roles.Member);
        var active = await SetActiveAsync(admin.Id, "does-not-exist", false);

        Assert.Equal("USER_NOT_FOUND", roles.ErrorCode);
        Assert.Equal("USER_NOT_FOUND", active.ErrorCode);
    }

    // ----------------------------------------------------- security stamp

    [Fact]
    public async Task Changing_roles_replaces_them_and_invalidates_existing_tokens_by_rotating_the_security_stamp()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var stampBefore = (await TestData.ReloadUserAsync(member.Id)).SecurityStamp;

        var result = await SetRolesAsync(admin.Id, member.Id, Roles.WorkspaceManager);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(new[] { Roles.WorkspaceManager }, await TestData.RolesOfAsync(member.Id));
        Assert.NotEqual(stampBefore, (await TestData.ReloadUserAsync(member.Id)).SecurityStamp);
    }

    [Fact]
    public async Task Requesting_the_roles_a_user_already_has_is_a_no_op_that_does_not_log_them_out()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var stampBefore = (await TestData.ReloadUserAsync(member.Id)).SecurityStamp;

        var result = await SetRolesAsync(admin.Id, member.Id, Roles.Member);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(stampBefore, (await TestData.ReloadUserAsync(member.Id)).SecurityStamp);
    }

    [Fact]
    public async Task Deactivating_a_user_rotates_the_stamp_and_a_repeat_deactivation_does_not()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var original = (await TestData.ReloadUserAsync(member.Id)).SecurityStamp;

        var first = await SetActiveAsync(admin.Id, member.Id, false);
        var afterFirst = await TestData.ReloadUserAsync(member.Id);
        var second = await SetActiveAsync(admin.Id, member.Id, false);
        var afterSecond = await TestData.ReloadUserAsync(member.Id);

        Assert.True(first.Succeeded && second.Succeeded);
        Assert.False(afterFirst.IsActive);
        Assert.NotEqual(original, afterFirst.SecurityStamp);
        Assert.Equal(afterFirst.SecurityStamp, afterSecond.SecurityStamp);
    }

    // ------------------------------------------------- deactivation & bookings

    [Fact]
    public async Task Deactivating_a_user_cancels_only_their_future_confirmed_bookings()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();

        var future = await TestData.CreateBookingAsync(member.Id, workspace.Id,
            DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(2).AddHours(1));
        var alreadyStarted = await TestData.CreateBookingAsync(member.Id, workspace.Id,
            DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(50));
        var checkedIn = await TestData.CreateBookingAsync(member.Id, workspace.Id,
            DateTime.UtcNow.AddDays(3), DateTime.UtcNow.AddDays(3).AddHours(1),
            BookingStatus.CheckedIn, checkedInAt: DateTime.UtcNow);

        var result = await SetActiveAsync(admin.Id, member.Id, false);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(BookingStatus.Cancelled, (await TestData.ReadBookingAsync(future.Id)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(alreadyStarted.Id)).Status);
        Assert.Equal(BookingStatus.CheckedIn, (await TestData.ReadBookingAsync(checkedIn.Id)).Status);
    }

    [Fact]
    public async Task Reactivating_a_user_does_not_restore_cancelled_bookings()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var future = await TestData.CreateBookingAsync(member.Id, workspace.Id,
            DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(2).AddHours(1));

        await SetActiveAsync(admin.Id, member.Id, false);
        var reactivated = await SetActiveAsync(admin.Id, member.Id, true);

        Assert.True(reactivated.Succeeded, reactivated.ErrorMessage);
        Assert.True((await TestData.ReloadUserAsync(member.Id)).IsActive);
        Assert.Equal(BookingStatus.Cancelled, (await TestData.ReadBookingAsync(future.Id)).Status);
    }

    // --------------------------------------------------- last active admin

    [Fact]
    public async Task The_last_active_admin_cannot_be_deactivated()
    {
        var onlyAdmin = await TestData.CreateUserAsync(Roles.Admin);
        var bystander = await TestData.CreateMemberAsync(); // the service does not check the ACTOR's role
        await TestData.IsolateAdminsAsync(onlyAdmin.Id);

        var result = await SetActiveAsync(bystander.Id, onlyAdmin.Id, false);

        Assert.Equal("LAST_ACTIVE_ADMIN_PROTECTED", result.ErrorCode);
        Assert.True((await TestData.ReloadUserAsync(onlyAdmin.Id)).IsActive);
    }

    [Fact]
    public async Task The_last_active_admin_cannot_lose_the_admin_role()
    {
        var onlyAdmin = await TestData.CreateUserAsync(Roles.Admin);
        var bystander = await TestData.CreateMemberAsync();
        await TestData.IsolateAdminsAsync(onlyAdmin.Id);

        var result = await SetRolesAsync(bystander.Id, onlyAdmin.Id, Roles.Member);

        Assert.Equal("LAST_ACTIVE_ADMIN_PROTECTED", result.ErrorCode);
        Assert.Equal(new[] { Roles.Admin }, await TestData.RolesOfAsync(onlyAdmin.Id));
    }

    [Fact]
    public async Task An_admin_can_be_demoted_or_deactivated_while_another_active_admin_remains()
    {
        var keeper = await TestData.CreateUserAsync(Roles.Admin);
        var leaving = await TestData.CreateUserAsync(Roles.Admin);
        var demoted = await TestData.CreateUserAsync(Roles.Admin);
        await TestData.IsolateAdminsAsync(keeper.Id, leaving.Id, demoted.Id);

        var deactivate = await SetActiveAsync(keeper.Id, leaving.Id, false);
        var demote = await SetRolesAsync(keeper.Id, demoted.Id, Roles.Member);

        Assert.True(deactivate.Succeeded, deactivate.ErrorMessage);
        Assert.True(demote.Succeeded, demote.ErrorMessage);
    }

    // Two admins each try to remove the OTHER'S admin role at the same
    // instant. Each sees "two admins exist, so removing one is fine". If both
    // were allowed, the platform would be left with zero admins.
    [Fact]
    public async Task Two_admins_demoting_each_other_at_once_cannot_leave_the_platform_without_an_admin()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var a = await TestData.CreateUserAsync(Roles.Admin);
            var b = await TestData.CreateUserAsync(Roles.Admin);
            await TestData.IsolateAdminsAsync(a.Id, b.Id);

            var results = await Concurrency.RunAsync(
                () => SetRolesAsync(a.Id, b.Id, Roles.Member),
                () => SetRolesAsync(b.Id, a.Id, Roles.Member));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded),
                r => Assert.Contains(r.ErrorCode, new[] { "LAST_ACTIVE_ADMIN_PROTECTED", "CONCURRENT_ADMIN_CHANGE" }));

            var stillAdmins = (await TestData.RolesOfAsync(a.Id)).Count(role => role == Roles.Admin)
                            + (await TestData.RolesOfAsync(b.Id)).Count(role => role == Roles.Admin);
            Assert.Equal(1, stillAdmins);
        }
    }

    [Fact]
    public async Task Two_admins_deactivating_each_other_at_once_cannot_leave_the_platform_without_an_active_admin()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var a = await TestData.CreateUserAsync(Roles.Admin);
            var b = await TestData.CreateUserAsync(Roles.Admin);
            await TestData.IsolateAdminsAsync(a.Id, b.Id);

            var results = await Concurrency.RunAsync(
                () => SetActiveAsync(a.Id, b.Id, false),
                () => SetActiveAsync(b.Id, a.Id, false));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded),
                r => Assert.Contains(r.ErrorCode, new[] { "LAST_ACTIVE_ADMIN_PROTECTED", "CONCURRENT_ADMIN_CHANGE" }));

            var activeAdmins = (await TestData.ReloadUserAsync(a.Id)).IsActive ? 1 : 0;
            activeAdmins += (await TestData.ReloadUserAsync(b.Id)).IsActive ? 1 : 0;
            Assert.Equal(1, activeAdmins);
        }
    }
}
