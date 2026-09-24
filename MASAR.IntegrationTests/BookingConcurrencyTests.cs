using System.Data;
using Masar.Application.DTOs.Bookings;
using Masar.Application.Validators.Bookings;
using Masar.Domain.Entities;
using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Masar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Masar.IntegrationTests;

// This is the test Step 3 explicitly requires and no zip can substitute
// for: "Must be verified with a real integration test against SQL
// Server, not assumed from the isolation level alone." It runs against
// your actual MASAR_DB (via appsettings.json in this project, same
// connection string as the API) — there is no in-memory or mocked
// version of this test, because the whole point is proving the
// SERIALIZABLE isolation level actually behaves as expected on your
// real SQL Server instance, not on an approximation of one.
//

public class BookingConcurrencyTests
{
    private static ApplicationDbContext NewDbContext()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .Build();

        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(config.GetConnectionString("DefaultConnection"))
            .Options;

        return new ApplicationDbContext(options);
    }

    private static BookingService NewBookingService(ApplicationDbContext db) =>
        new(db, new CreateBookingRequestValidator(), NullLogger<BookingService>.Instance);

    [Fact]
    public async Task Two_concurrent_bookings_for_the_same_slot_only_one_succeeds()
    {
        // Arrange — seed a real Location/Workspace/two Users directly,
        // bypassing the API entirely, and clean everything up afterward
        // so this test doesn't leave garbage rows in your real database.
        await using var setupDb = NewDbContext();

        var location = new Location
        {
            Name = $"ConcurrencyTest_{Guid.NewGuid():N}",
            Address = "Test Address",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var workspace = new Workspace
        {
            Name = $"ConcurrencyTestRoom_{Guid.NewGuid():N}",
            Type = WorkspaceType.MeetingRoom,
            Capacity = 4,
            Status = WorkspaceStatus.Available,
            OpeningTime = new TimeOnly(0, 0),
            ClosingTime = new TimeOnly(23, 59),
            Location = location,
            CreatedAt = DateTime.UtcNow
        };

        var userA = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"concurrencytest_a_{Guid.NewGuid():N}@test.local",
            NormalizedUserName = $"CONCURRENCYTEST_A_{Guid.NewGuid():N}@TEST.LOCAL",
            Email = $"concurrencytest_a_{Guid.NewGuid():N}@test.local",
            NormalizedEmail = $"CONCURRENCYTEST_A_{Guid.NewGuid():N}@TEST.LOCAL",
            EmailConfirmed = true,
            FirstName = "Test",
            LastName = "UserA",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var userB = new ApplicationUser
        {
            Id = Guid.NewGuid().ToString(),
            UserName = $"concurrencytest_b_{Guid.NewGuid():N}@test.local",
            NormalizedUserName = $"CONCURRENCYTEST_B_{Guid.NewGuid():N}@TEST.LOCAL",
            Email = $"concurrencytest_b_{Guid.NewGuid():N}@test.local",
            NormalizedEmail = $"CONCURRENCYTEST_B_{Guid.NewGuid():N}@TEST.LOCAL",
            EmailConfirmed = true,
            FirstName = "Test",
            LastName = "UserB",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        setupDb.Locations.Add(location);
        setupDb.Workspaces.Add(workspace);
        setupDb.Users.AddRange(userA, userB);
        await setupDb.SaveChangesAsync();

        try
        {
            // A slot comfortably in the future, well clear of "past
            // time" and "outside operating hours" rejections — the test
            // is about the overlap race specifically, not these other
            // checks.
            var date = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(7));
            var request = new CreateBookingRequest(
                workspace.Id, date, new TimeOnly(10, 0), new TimeOnly(11, 0));

            // Act — two separate DbContext instances (DbContext is not
            // thread-safe; each concurrent call needs its own connection
            // and its own transaction), firing genuinely concurrently via
            // Task.WhenAll, not sequentially.
            await using var dbA = NewDbContext();
            await using var dbB = NewDbContext();

            var taskA = NewBookingService(dbA).CreateAsync(userA.Id, request);
            var taskB = NewBookingService(dbB).CreateAsync(userB.Id, request);

            var results = await Task.WhenAll(taskA, taskB);

            // Assert — exactly one success, and the other genuinely did
            // NOT also succeed. The "loser" can land in one of two
            // clean states, both of which prove the SERIALIZABLE
            // boundary actually did its job (no double-booking, no
            // unhandled crash):
            //   - WORKSPACE_UNAVAILABLE: it waited, then correctly saw
            //     the already-committed booking on re-check.
            //   - CONCURRENT_WRITE_CONFLICT: SQL Server detected a
            //     deadlock (error 1205) — a realistic, known outcome of
            //     this exact "read a range, then insert into it" shape
            //     under SERIALIZABLE, caught and reported cleanly by
            //     BookingService rather than crashing the caller.
            // What must NEVER happen: two successes (an actual double
            // booking, the bug this whole mechanism exists to prevent),
            // or an unhandled exception escaping Task.WhenAll — either
            // would mean the SERIALIZABLE boundary or its error handling
            // isn't doing what Step 3/12 claims it does.
            var succeededCount = results.Count(r => r.Succeeded);
            var cleanLoserCount = results.Count(r =>
                !r.Succeeded &&
                (r.ErrorCode == "WORKSPACE_UNAVAILABLE" || r.ErrorCode == "CONCURRENT_WRITE_CONFLICT"));

            Assert.Equal(1, succeededCount);
            Assert.Equal(1, cleanLoserCount);
        }
        finally
        {
            // Cleanup — remove everything this test created, in FK-safe
            // order (Bookings before Workspace/Users, Workspace before
            // Location).
            await using var cleanupDb = NewDbContext();

            var bookings = await cleanupDb.Bookings
                .Where(b => b.WorkspaceId == workspace.Id)
                .ToListAsync();
            cleanupDb.Bookings.RemoveRange(bookings);

            var ws = await cleanupDb.Workspaces.FindAsync(workspace.Id);
            if (ws is not null) cleanupDb.Workspaces.Remove(ws);

            var users = await cleanupDb.Users
                .Where(u => u.Id == userA.Id || u.Id == userB.Id)
                .ToListAsync();
            cleanupDb.Users.RemoveRange(users);

            await cleanupDb.SaveChangesAsync();

            var loc = await cleanupDb.Locations.FindAsync(location.Id);
            if (loc is not null)
            {
                cleanupDb.Locations.Remove(loc);
                await cleanupDb.SaveChangesAsync();
            }
        }
    }
}
