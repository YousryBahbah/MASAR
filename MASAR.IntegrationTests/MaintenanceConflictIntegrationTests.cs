using Masar.Application.Common;
using Masar.Application.DTOs.Maintenance;
using Masar.Application.Interfaces;
using Masar.Domain.Enums;
using Masar.IntegrationTests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Masar.IntegrationTests;

// Step 15: a maintenance period may not be scheduled over an ACTIVE booking.
// (The mirror rule — a booking may not be made over maintenance — is covered
// in BookingCreationIntegrationTests.)
//
// Request times are Egypt-local, exactly as the API receives them, and
// bookings are placed with the same EgyptTime conversion the service uses, so
// these tests behave identically in winter (UTC+2) and summer (UTC+3).
[Collection(SqlServerCollection.Name)]
public class MaintenanceConflictIntegrationTests : IAsyncLifetime
{
    public Task InitializeAsync() => TestDatabase.EnsureReadyAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static Task<Result<MaintenancePeriodResponse>> CreateMaintenanceAsync(
        string createdBy, int workspaceId, DateOnly date, int startHour, int endHour) =>
        TestServices.InScopeAsync(services => services.GetRequiredService<IMaintenancePeriodService>()
            .CreateAsync(createdBy, new CreateMaintenancePeriodRequest(
                workspaceId, date, new TimeOnly(startHour, 0), date, new TimeOnly(endHour, 0), "test")));

    // A booking occupying [startHour, endHour) local time on `date`.
    private static async Task<int> SeedBookingAsync(
        string userId, int workspaceId, DateOnly date, int startHour, int endHour, BookingStatus status)
    {
        var start = TestData.SlotStartUtc(date, startHour);
        var end = TestData.SlotStartUtc(date, endHour);
        var booking = await TestData.CreateBookingAsync(userId, workspaceId, start, end, status);
        return booking.Id;
    }

    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.CheckedIn)]
    public async Task Maintenance_over_an_active_booking_is_rejected_and_names_the_booking(BookingStatus status)
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        var bookingId = await SeedBookingAsync(member.Id, workspace.Id, date, 14, 16, status);

        var result = await CreateMaintenanceAsync(admin.Id, workspace.Id, date, 15, 17);

        Assert.Equal("MAINTENANCE_BOOKING_CONFLICT", result.ErrorCode);
        Assert.Contains(bookingId.ToString(), result.ErrorMessage);
        // The message must not promise something no caller can do.
        Assert.DoesNotContain("Resolve or cancel", result.ErrorMessage);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.Completed)]
    public async Task Bookings_that_no_longer_hold_the_slot_do_not_block_maintenance(BookingStatus status)
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await SeedBookingAsync(member.Id, workspace.Id, date, 14, 16, status);

        var result = await CreateMaintenanceAsync(admin.Id, workspace.Id, date, 15, 17);

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task Maintenance_touching_a_booking_at_a_single_instant_is_allowed()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await SeedBookingAsync(member.Id, workspace.Id, date, 14, 16, BookingStatus.Confirmed);

        var after = await CreateMaintenanceAsync(admin.Id, workspace.Id, date, 16, 18);
        var before = await CreateMaintenanceAsync(admin.Id, workspace.Id, date, 12, 14);

        Assert.True(after.Succeeded, after.ErrorMessage);
        Assert.True(before.Succeeded, before.ErrorMessage);
    }

    [Fact]
    public async Task A_booking_on_a_different_workspace_does_not_block_maintenance()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var booked = await TestData.CreateWorkspaceAsync();
        var other = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await SeedBookingAsync(member.Id, booked.Id, date, 14, 16, BookingStatus.Confirmed);

        var result = await CreateMaintenanceAsync(admin.Id, other.Id, date, 14, 16);

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task Moving_an_existing_maintenance_period_onto_a_booking_is_rejected()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await SeedBookingAsync(member.Id, workspace.Id, date, 14, 16, BookingStatus.Confirmed);
        var period = await CreateMaintenanceAsync(admin.Id, workspace.Id, date, 8, 10);
        Assert.True(period.Succeeded, period.ErrorMessage);

        var moved = await TestServices.InScopeAsync(services => services.GetRequiredService<IMaintenancePeriodService>()
            .UpdateAsync(period.Response!.Id, new UpdateMaintenancePeriodRequest(
                date, new TimeOnly(15, 0), date, new TimeOnly(17, 0), "moved")));

        Assert.Equal("MAINTENANCE_BOOKING_CONFLICT", moved.ErrorCode);
    }

    [Fact]
    public async Task Maintenance_for_an_unknown_workspace_is_reported_as_not_found()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);

        var result = await CreateMaintenanceAsync(admin.Id, int.MaxValue, TestData.FutureDate(), 9, 10);

        Assert.Equal("WORKSPACE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task A_reversed_interval_is_rejected_as_a_validation_failure()
    {
        var admin = await TestData.CreateUserAsync(Roles.Admin);
        var workspace = await TestData.CreateWorkspaceAsync();

        var result = await CreateMaintenanceAsync(admin.Id, workspace.Id, TestData.FutureDate(), 17, 15);

        Assert.Equal("VALIDATION_FAILED", result.ErrorCode);
    }
}
