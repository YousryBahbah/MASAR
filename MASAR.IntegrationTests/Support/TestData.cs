using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;
using Masar.Application.Interfaces;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.Infrastructure.Jobs;
using Masar.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Masar.IntegrationTests.Support;

// Builders for test data. Every name is unique (Guid) so tests never collide
// with each other or with leftovers, and nothing needs cleaning up: the whole
// database is recreated at the start of every run.
internal static class TestData
{
    // Satisfies Identity's default password policy.
    public const string Password = "Passw0rd!Test1";

    // ---------------------------------------------------------------- users

    public static Task<ApplicationUser> CreateUserAsync(params string[] roles) =>
        TestServices.InScopeAsync(async services =>
        {
            var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
            var email = $"user_{Guid.NewGuid():N}@test.local";

            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FirstName = "Test",
                LastName = "User",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var created = await userManager.CreateAsync(user, Password);
            if (!created.Succeeded)
            {
                throw new InvalidOperationException(
                    "Could not create test user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
            }

            foreach (var role in roles)
            {
                var added = await userManager.AddToRoleAsync(user, role);
                if (!added.Succeeded)
                {
                    throw new InvalidOperationException(
                        $"Could not add role {role}: " + string.Join("; ", added.Errors.Select(e => e.Description)));
                }
            }

            return user;
        });

    public static Task<ApplicationUser> CreateMemberAsync() => CreateUserAsync(Roles.Member);

    // Makes exactly `keepActive` the only active Admins. The last-active-admin
    // rules are global ("how many active admins exist?"), so tests about them
    // need to control that number. Other tests create their own admins, so
    // deactivating leftovers here cannot affect them.
    public static Task IsolateAdminsAsync(params string[] keepActive) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var adminRoleId = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).SingleAsync();

            var otherAdminIds = await db.UserRoles
                .Where(ur => ur.RoleId == adminRoleId && !keepActive.Contains(ur.UserId))
                .Select(ur => ur.UserId)
                .ToListAsync();

            await db.Users
                .Where(u => otherAdminIds.Contains(u.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        });

    public static Task<ApplicationUser> ReloadUserAsync(string userId) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            return await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        });

    public static Task<List<string>> RolesOfAsync(string userId) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            return await db.UserRoles
                .Where(ur => ur.UserId == userId)
                .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => r.Name!)
                .OrderBy(name => name)
                .ToListAsync();
        });

    // ----------------------------------------------------- locations/workspaces

    public static Task<Workspace> CreateWorkspaceAsync(
        bool locationActive = true,
        WorkspaceStatus status = WorkspaceStatus.Available,
        TimeOnly? opening = null,
        TimeOnly? closing = null) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();

            var location = new Location
            {
                Name = $"TestLocation_{Guid.NewGuid():N}",
                Address = "Test Address",
                IsActive = locationActive,
                CreatedAt = DateTime.UtcNow
            };

            var workspace = new Workspace
            {
                Name = $"TestRoom_{Guid.NewGuid():N}",
                Type = WorkspaceType.MeetingRoom,
                Capacity = 4,
                Status = status,
                OpeningTime = opening ?? new TimeOnly(0, 0),
                ClosingTime = closing ?? new TimeOnly(23, 59),
                Location = location,
                CreatedAt = DateTime.UtcNow
            };

            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync();
            return workspace;
        });

    // ------------------------------------------------------------- bookings

    // Direct insert, bypassing every business rule — used to put a booking
    // into a state or time window the API would never let you create.
    public static Task<Booking> CreateBookingAsync(
        string userId,
        int workspaceId,
        DateTime startUtc,
        DateTime endUtc,
        BookingStatus status = BookingStatus.Confirmed,
        DateTime? checkedInAt = null) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();

            var booking = new Booking
            {
                UserId = userId,
                WorkspaceId = workspaceId,
                StartTime = startUtc,
                EndTime = endUtc,
                Status = status,
                CheckedInAt = checkedInAt,
                CreatedAt = DateTime.UtcNow
            };

            db.Bookings.Add(booking);
            await db.SaveChangesAsync();
            return booking;
        });

    public static Task<Booking> ReadBookingAsync(int bookingId) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            return await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        });

    public static Task<int> CountActiveBookingsAsync(string userId) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            var now = DateTime.UtcNow;
            return await db.Bookings.CountAsync(b =>
                b.UserId == userId
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
                && b.EndTime > now);
        });

    public static Task<int> CountBlockingBookingsAsync(int workspaceId) =>
        TestServices.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<ApplicationDbContext>();
            return await db.Bookings.CountAsync(b =>
                b.WorkspaceId == workspaceId
                && (b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn));
        });

    // ------------------------------------------------------------ time slots

    // A date comfortably in the future. 10:00-11:00 local never touches a DST
    // transition (those happen around midnight), so slots below are safe.
    public static DateOnly FutureDate(int daysAhead = 5) =>
        DateOnly.FromDateTime(DateTime.UtcNow.AddDays(daysAhead));

    public static CreateBookingRequest SlotRequest(
        int workspaceId, DateOnly date, int startHour = 10, int endHour = 11) =>
        new(workspaceId, date, new TimeOnly(startHour, 0), new TimeOnly(endHour, 0));

    public static DateTime SlotStartUtc(DateOnly date, int hour = 10) =>
        EgyptTime.ToUtc(date, new TimeOnly(hour, 0));

    // ------------------------------------------------------ service shortcuts
    // Each call runs in a fresh scope, like a separate HTTP request would.

    public static Task<Result<BookingResponse>> CreateBookingViaServiceAsync(
        string userId, CreateBookingRequest request) =>
        TestServices.InScopeAsync(services =>
            services.GetRequiredService<IBookingService>().CreateAsync(userId, request));

    public static Task<Result<BookingResponse>> CheckInAsync(string userId, int bookingId) =>
        TestServices.InScopeAsync(services =>
            services.GetRequiredService<IBookingService>().CheckInAsync(userId, bookingId));

    public static Task<Result<BookingResponse>> CancelAsync(string userId, int bookingId) =>
        TestServices.InScopeAsync(services =>
            services.GetRequiredService<IBookingService>().CancelAsync(userId, bookingId));

    public static Task SweepNoShowsAsync() =>
        TestServices.InScopeAsync(services =>
            services.GetRequiredService<BookingLifecycleJobs>().SweepNoShowsAsync());

    public static Task SweepCompletionsAsync() =>
        TestServices.InScopeAsync(services =>
            services.GetRequiredService<BookingLifecycleJobs>().SweepCompletionsAsync());
}
