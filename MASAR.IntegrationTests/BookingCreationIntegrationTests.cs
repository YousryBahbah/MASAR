using Masar.Application.Common;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.IntegrationTests.Support;
using Masar.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Masar.IntegrationTests;

// Roadmap 17.2 — valid bookings are created, invalid ones are rejected,
// maintenance blocks booking, and cancelled bookings stop blocking.
// Everything runs through the real BookingService against real SQL Server.
[Collection(SqlServerCollection.Name)]
public class BookingCreationIntegrationTests : IAsyncLifetime
{
    public Task InitializeAsync() => TestDatabase.EnsureReadyAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_valid_booking_is_created_as_Confirmed_with_utc_times_stored()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, date));

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(BookingStatus.Confirmed, result.Response!.Status);

        var stored = await TestData.ReadBookingAsync(result.Response.Id);
        Assert.Equal(member.Id, stored.UserId);
        Assert.Equal(BookingStatus.Confirmed, stored.Status);
        Assert.Equal(TestData.SlotStartUtc(date, 10), stored.StartTime);
        Assert.Equal(TestData.SlotStartUtc(date, 11), stored.EndTime);
    }

    [Fact]
    public async Task A_booking_that_starts_in_the_past_is_rejected()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var yesterday = TestData.FutureDate(-2);

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, yesterday));

        Assert.False(result.Succeeded);
        Assert.Equal("BOOKING_IN_PAST", result.ErrorCode);
    }

    [Fact]
    public async Task A_booking_outside_operating_hours_is_rejected()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync(opening: new TimeOnly(8, 0), closing: new TimeOnly(20, 0));

        var late = await TestData.CreateBookingViaServiceAsync(
            member.Id, TestData.SlotRequest(workspace.Id, TestData.FutureDate(), 21, 22));
        var early = await TestData.CreateBookingViaServiceAsync(
            member.Id, TestData.SlotRequest(workspace.Id, TestData.FutureDate(), 6, 7));

        Assert.Equal("OUTSIDE_OPERATING_HOURS", late.ErrorCode);
        Assert.Equal("OUTSIDE_OPERATING_HOURS", early.ErrorCode);
    }

    [Fact]
    public async Task A_booking_exactly_filling_the_operating_hours_is_allowed()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync(opening: new TimeOnly(8, 0), closing: new TimeOnly(20, 0));

        var result = await TestData.CreateBookingViaServiceAsync(
            member.Id, TestData.SlotRequest(workspace.Id, TestData.FutureDate(), 8, 20));

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task An_unknown_workspace_is_reported_as_not_found()
    {
        var member = await TestData.CreateMemberAsync();

        var result = await TestData.CreateBookingViaServiceAsync(
            member.Id, TestData.SlotRequest(int.MaxValue, TestData.FutureDate()));

        Assert.Equal("WORKSPACE_NOT_FOUND", result.ErrorCode);
    }

    [Fact]
    public async Task An_inactive_workspace_cannot_be_booked()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync(status: WorkspaceStatus.Inactive);

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, TestData.FutureDate()));

        Assert.Equal("WORKSPACE_INACTIVE", result.ErrorCode);
    }

    [Fact]
    public async Task A_workspace_in_an_inactive_location_cannot_be_booked()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync(locationActive: false);

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, TestData.FutureDate()));

        Assert.Equal("LOCATION_INACTIVE", result.ErrorCode);
    }

    // ------------------------------------------------------------ overlaps

    [Fact]
    public async Task An_overlapping_booking_by_another_member_is_rejected()
    {
        var first = await TestData.CreateMemberAsync();
        var second = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var firstResult = await TestData.CreateBookingViaServiceAsync(first.Id, TestData.SlotRequest(workspace.Id, date, 10, 12));
        var overlapping = await TestData.CreateBookingViaServiceAsync(second.Id, TestData.SlotRequest(workspace.Id, date, 11, 13));

        Assert.True(firstResult.Succeeded, firstResult.ErrorMessage);
        Assert.Equal("WORKSPACE_UNAVAILABLE", overlapping.ErrorCode);
        Assert.Equal(1, await TestData.CountBlockingBookingsAsync(workspace.Id));
    }

    // Overlap formula is strict: Existing.Start < New.End AND Existing.End > New.Start.
    // Back-to-back bookings touch at one instant and must both be allowed.
    [Fact]
    public async Task Back_to_back_bookings_touching_at_one_instant_are_both_allowed()
    {
        var first = await TestData.CreateMemberAsync();
        var second = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var morning = await TestData.CreateBookingViaServiceAsync(first.Id, TestData.SlotRequest(workspace.Id, date, 10, 11));
        var next = await TestData.CreateBookingViaServiceAsync(second.Id, TestData.SlotRequest(workspace.Id, date, 11, 12));

        Assert.True(morning.Succeeded, morning.ErrorMessage);
        Assert.True(next.Succeeded, next.ErrorMessage);
    }

    [Fact]
    public async Task A_cancelled_booking_no_longer_blocks_its_slot()
    {
        var first = await TestData.CreateMemberAsync();
        var second = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var original = await TestData.CreateBookingViaServiceAsync(first.Id, TestData.SlotRequest(workspace.Id, date));
        var cancel = await TestData.CancelAsync(first.Id, original.Response!.Id);
        var reuse = await TestData.CreateBookingViaServiceAsync(second.Id, TestData.SlotRequest(workspace.Id, date));

        Assert.True(cancel.Succeeded, cancel.ErrorMessage);
        Assert.True(reuse.Succeeded, reuse.ErrorMessage);
    }

    // --------------------------------------------------------- maintenance

    [Fact]
    public async Task A_booking_inside_a_maintenance_period_is_rejected()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await AddMaintenanceAsync(workspace.Id, TestData.SlotStartUtc(date, 9), TestData.SlotStartUtc(date, 13));

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, date, 10, 11));

        Assert.Equal("MAINTENANCE_CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task A_booking_partially_overlapping_a_maintenance_period_is_rejected()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await AddMaintenanceAsync(workspace.Id, TestData.SlotStartUtc(date, 11), TestData.SlotStartUtc(date, 14));

        // 10:00-12:00 overlaps the first hour of the 11:00-14:00 maintenance.
        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, date, 10, 12));

        Assert.Equal("MAINTENANCE_CONFLICT", result.ErrorCode);
    }

    [Fact]
    public async Task A_booking_ending_exactly_when_maintenance_starts_is_allowed()
    {
        var member = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await AddMaintenanceAsync(workspace.Id, TestData.SlotStartUtc(date, 11), TestData.SlotStartUtc(date, 14));

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspace.Id, date, 10, 11));

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    [Fact]
    public async Task Maintenance_on_one_workspace_does_not_block_another_workspace()
    {
        var member = await TestData.CreateMemberAsync();
        var underMaintenance = await TestData.CreateWorkspaceAsync();
        var other = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        await AddMaintenanceAsync(underMaintenance.Id, TestData.SlotStartUtc(date, 9), TestData.SlotStartUtc(date, 13));

        var result = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(other.Id, date));

        Assert.True(result.Succeeded, result.ErrorMessage);
    }

    // -------------------------------------------------------------- limits

    [Fact]
    public async Task A_third_active_booking_is_rejected_and_a_cancelled_one_stops_counting()
    {
        var member = await TestData.CreateMemberAsync();
        var date = TestData.FutureDate();
        var workspaces = new[]
        {
            await TestData.CreateWorkspaceAsync(),
            await TestData.CreateWorkspaceAsync(),
            await TestData.CreateWorkspaceAsync()
        };

        var one = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspaces[0].Id, date));
        var two = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspaces[1].Id, date));
        var three = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspaces[2].Id, date));

        Assert.True(one.Succeeded && two.Succeeded);
        Assert.Equal("ACTIVE_BOOKING_LIMIT_EXCEEDED", three.ErrorCode);

        await TestData.CancelAsync(member.Id, one.Response!.Id);
        var retry = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(workspaces[2].Id, date));

        Assert.True(retry.Succeeded, retry.ErrorMessage);
        Assert.Equal(2, await TestData.CountActiveBookingsAsync(member.Id));
    }

    [Fact]
    public async Task The_limit_is_per_user_not_global()
    {
        var busy = await TestData.CreateMemberAsync();
        var other = await TestData.CreateMemberAsync();
        var date = TestData.FutureDate();
        var a = await TestData.CreateWorkspaceAsync();
        var b = await TestData.CreateWorkspaceAsync();
        var c = await TestData.CreateWorkspaceAsync();

        await TestData.CreateBookingViaServiceAsync(busy.Id, TestData.SlotRequest(a.Id, date));
        await TestData.CreateBookingViaServiceAsync(busy.Id, TestData.SlotRequest(b.Id, date));
        var otherUser = await TestData.CreateBookingViaServiceAsync(other.Id, TestData.SlotRequest(c.Id, date));

        Assert.True(otherUser.Succeeded, otherUser.ErrorMessage);
    }

    // ------------------------------------------------------------- helpers

    private static Task AddMaintenanceAsync(int workspaceId, DateTime startUtc, DateTime endUtc) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var creator = await TestData.CreateUserAsync(Roles.Admin);

            db.MaintenancePeriods.Add(new MaintenancePeriod
            {
                WorkspaceId = workspaceId,
                CreatedByUserId = creator.Id,
                StartTime = startUtc,
                EndTime = endUtc,
                Reason = "test",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        });
}
