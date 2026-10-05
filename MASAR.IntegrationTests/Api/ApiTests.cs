using System.Net;
using System.Net.Http.Json;
using System.Text;
using Masar.Application.DTOs.Admin;
using Masar.Application.DTOs.Auth;
using Masar.Application.DTOs.Bookings;
using Masar.Application.DTOs.Maintenance;
using Masar.Application.DTOs.Search;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Masar.IntegrationTests.Support;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Masar.IntegrationTests.Api;

// Roadmap 17.3 — the real HTTP surface: real Program.cs, middleware, JWT
// validation and routing. A deliberately small set; the business rules
// themselves are proven in the service-level tests.
[Collection(SqlServerCollection.Name)]
public class ApiTests : IClassFixture<ApiFactory>, IAsyncLifetime
{
    private readonly ApiFactory _factory;

    public ApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => TestDatabase.EnsureReadyAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ===================================================================
    //  Full booking flow
    // ===================================================================

    [Fact]
    public async Task Full_flow_register_login_search_book_history_checkin_details_completion()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();
        var client = _factory.ClientFor(member.Token);

        // Search
        var search = await client.GetAsync(
            $"/api/workspaces/search?locationId={workspace.LocationId}&date={date:yyyy-MM-dd}&startTime=10:00:00&endTime=11:00:00");
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var found = await search.ReadAsync<WorkspaceSearchResponse>();
        Assert.Contains(found.Items, item => item.Id == workspace.Id);

        // Create booking
        var create = await client.PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, date));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var booking = await create.ReadAsync<BookingResponse>();
        Assert.Equal(BookingStatus.Confirmed, booking.Status);
        Assert.Equal(workspace.Id, booking.WorkspaceId);

        // The slot is no longer offered by search.
        var searchAgain = await client.GetAsync(
            $"/api/workspaces/search?locationId={workspace.LocationId}&date={date:yyyy-MM-dd}&startTime=10:00:00&endTime=11:00:00");
        Assert.DoesNotContain((await searchAgain.ReadAsync<WorkspaceSearchResponse>()).Items, item => item.Id == workspace.Id);

        // History
        var history = await client.GetAsync("/api/bookings");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        var page = await history.ReadAsync<BookingHistoryResponse>();
        Assert.Equal(1, page.TotalCount);
        Assert.Contains(page.Items, item => item.Id == booking.Id);

        // The real slot is days away, so move the booking into its check-in
        // window directly (there is no injectable clock to fast-forward).
        await MoveBookingAsync(booking.Id, DateTime.UtcNow.AddMinutes(5), DateTime.UtcNow.AddMinutes(65));

        // Check in
        var checkIn = await client.PostAsync($"/api/bookings/{booking.Id}/checkin", null);
        Assert.Equal(HttpStatusCode.OK, checkIn.StatusCode);
        Assert.Equal(BookingStatus.CheckedIn, (await checkIn.ReadAsync<BookingResponse>()).Status);

        // Details
        var details = await client.GetAsync($"/api/bookings/{booking.Id}");
        Assert.Equal(HttpStatusCode.OK, details.StatusCode);
        var detailBody = await details.ReadAsync<BookingResponse>();
        Assert.Equal(BookingStatus.CheckedIn, detailBody.Status);
        Assert.NotNull(detailBody.CheckedInAt);

        // Completion: the booking ends, the sweep runs.
        await MoveBookingAsync(booking.Id, DateTime.UtcNow.AddMinutes(-61), DateTime.UtcNow.AddMinutes(-1));
        await TestData.SweepCompletionsAsync();

        var completed = await client.GetAsync($"/api/bookings/{booking.Id}");
        Assert.Equal(BookingStatus.Completed, (await completed.ReadAsync<BookingResponse>()).Status);
    }

    // ===================================================================
    //  401 — not authenticated
    // ===================================================================

    [Fact]
    public async Task A_protected_endpoint_without_a_token_returns_401_in_the_standard_envelope()
    {
        var response = await _factory.ClientFor().GetAsync("/api/bookings");

        await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task A_garbage_token_returns_401_in_the_standard_envelope()
    {
        var response = await _factory.ClientFor("this.is.not-a-jwt").GetAsync("/api/bookings");

        await response.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Wrong_password_returns_401_and_unknown_user_looks_identical()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var client = _factory.ClientFor();

        var wrongPassword = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(member.Email, "Wrong-Password1"));
        var unknownUser = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nobody@test.local", "Wrong-Password1"));

        await wrongPassword.AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
        await unknownUser.AssertErrorAsync(HttpStatusCode.Unauthorized, "INVALID_CREDENTIALS");
    }

    // ===================================================================
    //  403 — authenticated, wrong role
    // ===================================================================

    [Fact]
    public async Task A_member_cannot_use_admin_endpoints()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor(member.Token).GetAsync("/api/admin/users");

        await response.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task A_workspace_manager_cannot_use_admin_endpoints()
    {
        var manager = await _factory.CreateStaffAndLoginAsync(Roles.WorkspaceManager);

        var response = await _factory.ClientFor(manager.Token).GetAsync("/api/admin/users");

        await response.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task An_admin_can_use_admin_endpoints_so_the_403s_above_are_about_the_role_not_the_route()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);

        var response = await _factory.ClientFor(admin.Token).GetAsync("/api/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_member_cannot_schedule_maintenance()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var response = await _factory.ClientFor(member.Token).PostAsJsonAsync("/api/maintenance-periods",
            new CreateMaintenancePeriodRequest(workspace.Id, date, new TimeOnly(9, 0), date, new TimeOnly(10, 0), "x"));

        await response.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    [Fact]
    public async Task An_admin_cannot_create_bookings_because_booking_is_member_only()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);
        var workspace = await TestData.CreateWorkspaceAsync();

        var response = await _factory.ClientFor(admin.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, TestData.FutureDate()));

        await response.AssertErrorAsync(HttpStatusCode.Forbidden, "FORBIDDEN");
    }

    // ===================================================================
    //  422 — well-formed but impossible
    // ===================================================================

    [Fact]
    public async Task A_booking_in_the_past_returns_422()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();

        var response = await _factory.ClientFor(member.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, TestData.FutureDate(-2)));

        await response.AssertErrorAsync(HttpStatusCode.UnprocessableEntity, "BOOKING_IN_PAST");
    }

    [Fact]
    public async Task A_booking_outside_operating_hours_returns_422()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync(opening: new TimeOnly(8, 0), closing: new TimeOnly(20, 0));

        var response = await _factory.ClientFor(member.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, TestData.FutureDate(), 21, 22));

        await response.AssertErrorAsync(HttpStatusCode.UnprocessableEntity, "OUTSIDE_OPERATING_HOURS");
    }

    [Fact]
    public async Task Checking_in_far_outside_the_window_returns_422()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var client = _factory.ClientFor(member.Token);
        var booking = await (await client.PostAsJsonAsync("/api/bookings",
            TestData.SlotRequest(workspace.Id, TestData.FutureDate()))).ReadAsync<BookingResponse>();

        var response = await client.PostAsync($"/api/bookings/{booking.Id}/checkin", null);

        await response.AssertErrorAsync(HttpStatusCode.UnprocessableEntity, "OUTSIDE_CHECKIN_WINDOW");
    }

    // ===================================================================
    //  409 — conflicts with stored state
    // ===================================================================

    [Fact]
    public async Task Booking_an_already_booked_slot_returns_409()
    {
        var first = await _factory.RegisterAndLoginMemberAsync();
        var second = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var request = TestData.SlotRequest(workspace.Id, TestData.FutureDate());

        var firstResponse = await _factory.ClientFor(first.Token).PostAsJsonAsync("/api/bookings", request);
        var secondResponse = await _factory.ClientFor(second.Token).PostAsJsonAsync("/api/bookings", request);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        await secondResponse.AssertErrorAsync(HttpStatusCode.Conflict, "WORKSPACE_UNAVAILABLE");
    }

    [Fact]
    public async Task Booking_during_maintenance_returns_409()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var maintenance = await _factory.ClientFor(admin.Token).PostAsJsonAsync("/api/maintenance-periods",
            new CreateMaintenancePeriodRequest(workspace.Id, date, new TimeOnly(9, 0), date, new TimeOnly(13, 0), "Repainting"));
        Assert.Equal(HttpStatusCode.Created, maintenance.StatusCode);

        var response = await _factory.ClientFor(member.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, date));

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "MAINTENANCE_CONFLICT");
    }

    [Fact]
    public async Task Scheduling_maintenance_over_an_active_booking_returns_409()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var booked = await _factory.ClientFor(member.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, date));
        Assert.Equal(HttpStatusCode.Created, booked.StatusCode);

        var response = await _factory.ClientFor(admin.Token).PostAsJsonAsync("/api/maintenance-periods",
            new CreateMaintenancePeriodRequest(workspace.Id, date, new TimeOnly(9, 0), date, new TimeOnly(13, 0), "Repainting"));

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "MAINTENANCE_BOOKING_CONFLICT");
    }

    [Fact]
    public async Task A_third_active_booking_returns_409()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var client = _factory.ClientFor(member.Token);
        var date = TestData.FutureDate();

        for (var i = 0; i < 2; i++)
        {
            var workspace = await TestData.CreateWorkspaceAsync();
            var ok = await client.PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, date));
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var third = await TestData.CreateWorkspaceAsync();
        var response = await client.PostAsJsonAsync("/api/bookings", TestData.SlotRequest(third.Id, date));

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "ACTIVE_BOOKING_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task Cancelling_twice_returns_409_the_second_time()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var client = _factory.ClientFor(member.Token);
        var booking = await (await client.PostAsJsonAsync("/api/bookings",
            TestData.SlotRequest(workspace.Id, TestData.FutureDate()))).ReadAsync<BookingResponse>();

        var first = await client.PostAsync($"/api/bookings/{booking.Id}/cancel", null);
        var second = await client.PostAsync($"/api/bookings/{booking.Id}/cancel", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await second.AssertErrorAsync(HttpStatusCode.Conflict, "INVALID_BOOKING_STATUS");
    }

    [Fact]
    public async Task Registering_the_same_email_twice_returns_409()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor().PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("Dup", "User", member.Email, TestData.Password));

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "DUPLICATE_EMAIL");
    }

    // ===================================================================
    //  404 — not found / not yours
    // ===================================================================

    [Fact]
    public async Task Someone_elses_booking_looks_exactly_like_a_missing_one()
    {
        var owner = await _factory.RegisterAndLoginMemberAsync();
        var stranger = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var booking = await (await _factory.ClientFor(owner.Token).PostAsJsonAsync("/api/bookings",
            TestData.SlotRequest(workspace.Id, TestData.FutureDate()))).ReadAsync<BookingResponse>();
        var strangerClient = _factory.ClientFor(stranger.Token);

        var details = await strangerClient.GetAsync($"/api/bookings/{booking.Id}");
        var checkIn = await strangerClient.PostAsync($"/api/bookings/{booking.Id}/checkin", null);
        var cancel = await strangerClient.PostAsync($"/api/bookings/{booking.Id}/cancel", null);
        var missing = await strangerClient.GetAsync("/api/bookings/2147483647");

        await details.AssertErrorAsync(HttpStatusCode.NotFound, "BOOKING_NOT_FOUND");
        await checkIn.AssertErrorAsync(HttpStatusCode.NotFound, "BOOKING_NOT_FOUND");
        await cancel.AssertErrorAsync(HttpStatusCode.NotFound, "BOOKING_NOT_FOUND");
        await missing.AssertErrorAsync(HttpStatusCode.NotFound, "BOOKING_NOT_FOUND");
    }

    [Fact]
    public async Task Booking_an_unknown_workspace_returns_404()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor(member.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(2147483647, TestData.FutureDate()));

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "WORKSPACE_NOT_FOUND");
    }

    [Fact]
    public async Task An_unknown_route_returns_404_in_the_standard_envelope()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor(member.Token).GetAsync("/api/this-route-does-not-exist");

        await response.AssertErrorAsync(HttpStatusCode.NotFound, "NOT_FOUND");
    }

    // ===================================================================
    //  400 — the request could not even be read (Step 16: one central answer)
    // ===================================================================

    [Fact]
    public async Task An_empty_body_returns_400_for_endpoints_that_previously_failed_with_500()
    {
        // Auth had no per-action guard before Step 16, so a null body reached
        // the validator and surfaced as a 500.
        var response = await _factory.ClientFor().PostAsync("/api/auth/register",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task Malformed_json_returns_400()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor(member.Token).PostAsync("/api/bookings",
            new StringContent("{ \"workspaceId\": ", Encoding.UTF8, "application/json"));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task A_value_of_the_wrong_type_returns_400_without_echoing_serializer_internals()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor(member.Token).PostAsync("/api/bookings",
            new StringContent("{ \"workspaceId\": \"abc\", \"date\": \"not-a-date\" }", Encoding.UTF8, "application/json"));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("LineNumber", body);
        Assert.DoesNotContain("System.", body);
    }

    [Fact]
    public async Task An_invalid_query_value_returns_400()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        var response = await _factory.ClientFor(member.Token).GetAsync("/api/bookings?status=NotAStatus");

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    [Fact]
    public async Task A_field_that_fails_validation_returns_400()
    {
        var response = await _factory.ClientFor().PostAsJsonAsync("/api/auth/register",
            new RegisterRequest("", "User", "not-an-email", "x"));

        await response.AssertErrorAsync(HttpStatusCode.BadRequest, "VALIDATION_FAILED");
    }

    // ===================================================================
    //  Ownership
    // ===================================================================

    [Fact]
    public async Task Booking_history_only_ever_contains_the_callers_own_bookings()
    {
        var alice = await _factory.RegisterAndLoginMemberAsync();
        var bob = await _factory.RegisterAndLoginMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var created = await _factory.ClientFor(alice.Token)
            .PostAsJsonAsync("/api/bookings", TestData.SlotRequest(workspace.Id, TestData.FutureDate()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var aliceHistory = await (await _factory.ClientFor(alice.Token).GetAsync("/api/bookings")).ReadAsync<BookingHistoryResponse>();
        var bobHistory = await (await _factory.ClientFor(bob.Token).GetAsync("/api/bookings")).ReadAsync<BookingHistoryResponse>();

        Assert.Equal(1, aliceHistory.TotalCount);
        Assert.Equal(0, bobHistory.TotalCount);
        Assert.Empty(bobHistory.Items);
    }

    // ===================================================================
    //  Step 15 — a token dies when its account changes
    // ===================================================================

    [Fact]
    public async Task Deactivating_a_user_immediately_invalidates_their_existing_token()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);
        var member = await _factory.RegisterAndLoginMemberAsync();
        var memberClient = _factory.ClientFor(member.Token);
        Assert.Equal(HttpStatusCode.OK, (await memberClient.GetAsync("/api/bookings")).StatusCode);

        var deactivate = await _factory.ClientFor(admin.Token)
            .PatchAsync($"/api/admin/users/{member.UserId}/deactivate", null);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var after = await memberClient.GetAsync("/api/bookings");
        await after.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");
    }

    [Fact]
    public async Task Changing_a_users_roles_invalidates_their_old_token_and_a_new_login_carries_the_new_role()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);
        var member = await _factory.RegisterAndLoginMemberAsync();

        var change = await _factory.ClientFor(admin.Token).PutAsJsonAsync(
            $"/api/admin/users/{member.UserId}/roles", new UpdateUserRolesRequest([Roles.WorkspaceManager]));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        var oldToken = await _factory.ClientFor(member.Token).GetAsync("/api/bookings");
        await oldToken.AssertErrorAsync(HttpStatusCode.Unauthorized, "UNAUTHORIZED");

        var fresh = await _factory.LoginAsync(member.Email, TestData.Password);
        Assert.Contains(Roles.WorkspaceManager, fresh.Roles);
        Assert.DoesNotContain(Roles.Member, fresh.Roles);
    }

    [Fact]
    public async Task An_admin_cannot_demote_themselves_over_http()
    {
        var admin = await _factory.CreateStaffAndLoginAsync(Roles.Admin);

        var response = await _factory.ClientFor(admin.Token).PutAsJsonAsync(
            $"/api/admin/users/{admin.UserId}/roles", new UpdateUserRolesRequest([Roles.Member]));

        await response.AssertErrorAsync(HttpStatusCode.Conflict, "SELF_SERVICE_NOT_ALLOWED");
    }

    // ===================================================================
    //  Step 16 — the global exception handler, end to end
    // ===================================================================

    [Fact]
    public async Task An_unexpected_exception_becomes_a_500_that_leaks_nothing()
    {
        var member = await _factory.RegisterAndLoginMemberAsync();

        // Same app, same pipeline — only the booking service is swapped for
        // one that throws, to prove the real middleware wiring (not just the
        // handler class in isolation).
        using var throwing = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<Masar.Application.Interfaces.IBookingService>();
                services.AddScoped<Masar.Application.Interfaces.IBookingService, ThrowingBookingService>();
            }));

        var response = await throwing.ClientFor(member.Token).GetAsync("/api/bookings");

        await response.AssertErrorAsync(HttpStatusCode.InternalServerError, "INTERNAL_ERROR");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET-INTERNAL-DETAIL", body);
        Assert.DoesNotContain("InvalidOperationException", body);
        Assert.DoesNotContain(" at ", body); // no stack trace
    }

    // ------------------------------------------------------------- helpers

    private static Task MoveBookingAsync(int bookingId, DateTime startUtc, DateTime endUtc) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            await db.Bookings
                .Where(b => b.Id == bookingId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.StartTime, startUtc)
                    .SetProperty(b => b.EndTime, endUtc));
        });
}
