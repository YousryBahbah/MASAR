using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;
using Masar.Domain.Enums;
using Masar.Infrastructure.Services;
using Masar.IntegrationTests.Support;
using Xunit;

namespace Masar.IntegrationTests;

// Roadmap 17.1 "Booking lifecycle rules" (proven here against the real
// conditional UPDATEs, because that is where the rules actually live — there
// is no separate in-memory state machine to unit-test) and 17.2 "Lifecycle
// Race Conditions" / "Completion".
//
// Bookings are inserted directly in whatever state and time window a test
// needs; the API could never create, say, a booking that started 20 minutes
// ago.
[Collection(SqlServerCollection.Name)]
public class BookingLifecycleIntegrationTests : IAsyncLifetime
{
    public Task InitializeAsync() => TestDatabase.EnsureReadyAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly TimeSpan Grace = BookingService.CheckInGracePeriod;

    private static async Task<(string UserId, int BookingId)> SeedAsync(
        DateTime startUtc, BookingStatus status = BookingStatus.Confirmed,
        TimeSpan? length = null, DateTime? checkedInAt = null)
    {
        var user = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var booking = await TestData.CreateBookingAsync(
            user.Id, workspace.Id, startUtc, startUtc + (length ?? TimeSpan.FromHours(1)), status, checkedInAt);
        return (user.Id, booking.Id);
    }

    // ============================================================ valid transitions

    [Fact]
    public async Task Confirmed_to_CheckedIn_inside_the_window()
    {
        var (userId, bookingId) = await SeedAsync(DateTime.UtcNow.AddMinutes(5));

        var result = await TestData.CheckInAsync(userId, bookingId);
        var stored = await TestData.ReadBookingAsync(bookingId);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(BookingStatus.CheckedIn, stored.Status);
        Assert.NotNull(stored.CheckedInAt);
    }

    [Fact]
    public async Task Confirmed_to_Cancelled()
    {
        var (userId, bookingId) = await SeedAsync(DateTime.UtcNow.AddDays(1));

        var result = await TestData.CancelAsync(userId, bookingId);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(BookingStatus.Cancelled, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    [Fact]
    public async Task Confirmed_to_NoShow_by_the_sweep_once_the_grace_period_has_passed()
    {
        var (_, bookingId) = await SeedAsync(DateTime.UtcNow - Grace - TimeSpan.FromMinutes(5));

        await TestData.SweepNoShowsAsync();

        Assert.Equal(BookingStatus.NoShow, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    [Fact]
    public async Task CheckedIn_to_Completed_by_the_sweep_once_the_booking_has_ended()
    {
        var start = DateTime.UtcNow.AddMinutes(-90);
        var (_, bookingId) = await SeedAsync(start, BookingStatus.CheckedIn, checkedInAt: start);

        await TestData.SweepCompletionsAsync();

        Assert.Equal(BookingStatus.Completed, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    // ========================================================== invalid transitions

    // Roadmap: Cancelled -> CheckedIn, Completed -> CheckedIn are invalid
    // (also NoShow -> CheckedIn, and CheckedIn -> CheckedIn).
    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.CheckedIn)]
    public async Task Check_in_is_rejected_from_any_status_other_than_Confirmed(BookingStatus status)
    {
        var start = DateTime.UtcNow.AddMinutes(5); // inside the window, so only the STATUS can be at fault
        var (userId, bookingId) = await SeedAsync(
            start, status, checkedInAt: status is BookingStatus.CheckedIn or BookingStatus.Completed ? start : null);

        var result = await TestData.CheckInAsync(userId, bookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("INVALID_BOOKING_STATUS", result.ErrorCode);
        Assert.Equal(status, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    // Roadmap: NoShow -> Cancelled, CheckedIn -> Cancelled are invalid
    // (also Completed -> Cancelled, and Cancelled -> Cancelled).
    [Theory]
    [InlineData(BookingStatus.NoShow)]
    [InlineData(BookingStatus.CheckedIn)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task Cancel_is_rejected_from_any_status_other_than_Confirmed(BookingStatus status)
    {
        var start = DateTime.UtcNow.AddHours(2);
        var (userId, bookingId) = await SeedAsync(
            start, status, checkedInAt: status is BookingStatus.CheckedIn or BookingStatus.Completed ? start : null);

        var result = await TestData.CancelAsync(userId, bookingId);

        Assert.False(result.Succeeded);
        Assert.Equal("INVALID_BOOKING_STATUS", result.ErrorCode);
        Assert.Equal(status, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    [Fact]
    public async Task Check_in_before_the_window_opens_is_rejected_and_changes_nothing()
    {
        var (userId, bookingId) = await SeedAsync(DateTime.UtcNow + Grace + TimeSpan.FromMinutes(10));

        var result = await TestData.CheckInAsync(userId, bookingId);

        Assert.Equal("OUTSIDE_CHECKIN_WINDOW", result.ErrorCode);
        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    [Fact]
    public async Task Check_in_after_the_window_closes_is_rejected_and_changes_nothing()
    {
        var (userId, bookingId) = await SeedAsync(DateTime.UtcNow - Grace - TimeSpan.FromMinutes(5));

        var result = await TestData.CheckInAsync(userId, bookingId);

        Assert.Equal("OUTSIDE_CHECKIN_WINDOW", result.ErrorCode);
        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    [Fact]
    public async Task A_member_cannot_check_in_or_cancel_someone_elses_booking()
    {
        var (_, bookingId) = await SeedAsync(DateTime.UtcNow.AddMinutes(5));
        var stranger = await TestData.CreateMemberAsync();

        var checkIn = await TestData.CheckInAsync(stranger.Id, bookingId);
        var cancel = await TestData.CancelAsync(stranger.Id, bookingId);

        Assert.Equal("BOOKING_NOT_FOUND", checkIn.ErrorCode);
        Assert.Equal("BOOKING_NOT_FOUND", cancel.ErrorCode);
        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(bookingId)).Status);
    }

    // ================================================================ sweep scope

    [Fact]
    public async Task The_noshow_sweep_leaves_every_booking_it_should_not_touch_alone()
    {
        var upcoming = await SeedAsync(DateTime.UtcNow.AddHours(3));
        var insideGrace = await SeedAsync(DateTime.UtcNow - TimeSpan.FromMinutes(5));
        var checkedIn = await SeedAsync(DateTime.UtcNow - TimeSpan.FromMinutes(40), BookingStatus.CheckedIn,
            checkedInAt: DateTime.UtcNow - TimeSpan.FromMinutes(35));
        var cancelled = await SeedAsync(DateTime.UtcNow - TimeSpan.FromMinutes(40), BookingStatus.Cancelled);

        await TestData.SweepNoShowsAsync();

        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(upcoming.BookingId)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(insideGrace.BookingId)).Status);
        Assert.Equal(BookingStatus.CheckedIn, (await TestData.ReadBookingAsync(checkedIn.BookingId)).Status);
        Assert.Equal(BookingStatus.Cancelled, (await TestData.ReadBookingAsync(cancelled.BookingId)).Status);
    }

    [Fact]
    public async Task The_completion_sweep_leaves_every_booking_it_should_not_touch_alone()
    {
        var stillRunning = await SeedAsync(DateTime.UtcNow - TimeSpan.FromMinutes(10), BookingStatus.CheckedIn,
            checkedInAt: DateTime.UtcNow - TimeSpan.FromMinutes(9));
        var neverCheckedIn = await SeedAsync(DateTime.UtcNow - TimeSpan.FromHours(3));
        var noShow = await SeedAsync(DateTime.UtcNow - TimeSpan.FromHours(3), BookingStatus.NoShow);
        var cancelled = await SeedAsync(DateTime.UtcNow - TimeSpan.FromHours(3), BookingStatus.Cancelled);

        await TestData.SweepCompletionsAsync();

        Assert.Equal(BookingStatus.CheckedIn, (await TestData.ReadBookingAsync(stillRunning.BookingId)).Status);
        Assert.Equal(BookingStatus.Confirmed, (await TestData.ReadBookingAsync(neverCheckedIn.BookingId)).Status);
        Assert.Equal(BookingStatus.NoShow, (await TestData.ReadBookingAsync(noShow.BookingId)).Status);
        Assert.Equal(BookingStatus.Cancelled, (await TestData.ReadBookingAsync(cancelled.BookingId)).Status);
    }

    // ================================================================= completion

    // Roadmap: "Running the completion job again must not change anything."
    // UpdatedAt is part of "anything": a second run must not even rewrite it.
    [Fact]
    public async Task Running_the_completion_sweep_again_changes_nothing()
    {
        var start = DateTime.UtcNow.AddMinutes(-90);
        var (_, bookingId) = await SeedAsync(start, BookingStatus.CheckedIn, checkedInAt: start);

        await TestData.SweepCompletionsAsync();
        var afterFirst = await TestData.ReadBookingAsync(bookingId);

        await TestData.SweepCompletionsAsync();
        await TestData.SweepCompletionsAsync();
        var afterRepeats = await TestData.ReadBookingAsync(bookingId);

        Assert.Equal(BookingStatus.Completed, afterFirst.Status);
        Assert.Equal(afterFirst.Status, afterRepeats.Status);
        Assert.Equal(afterFirst.UpdatedAt, afterRepeats.UpdatedAt);
        Assert.Equal(afterFirst.CheckedInAt, afterRepeats.CheckedInAt);
    }

    [Fact]
    public async Task Running_the_noshow_sweep_again_changes_nothing()
    {
        var (_, bookingId) = await SeedAsync(DateTime.UtcNow - Grace - TimeSpan.FromMinutes(5));

        await TestData.SweepNoShowsAsync();
        var afterFirst = await TestData.ReadBookingAsync(bookingId);

        await TestData.SweepNoShowsAsync();
        var afterSecond = await TestData.ReadBookingAsync(bookingId);

        Assert.Equal(BookingStatus.NoShow, afterFirst.Status);
        Assert.Equal(afterFirst.UpdatedAt, afterSecond.UpdatedAt);
    }

    // ==================================================================== races

    // Roadmap: "Check-in vs No-show job — only one valid state transition
    // should win."
    //
    // By construction the check-in window closes at exactly the instant the
    // sweep becomes eligible (Start + Grace), so the two can only collide in
    // the thin slice around that instant. The booking is seeded so that
    // instant lies a few milliseconds ahead, and both operations are released
    // together; several offsets, repeated, to land on both sides of it.
    // The outcome differs run to run — the INVARIANTS must not.
    [Theory]
    [InlineData(0)]
    [InlineData(20)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(250)]
    public async Task Check_in_racing_the_noshow_sweep_never_produces_two_transitions(int millisecondsUntilWindowCloses)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var start = DateTime.UtcNow - Grace + TimeSpan.FromMilliseconds(millisecondsUntilWindowCloses);
            var (userId, bookingId) = await SeedAsync(start);

            Result<BookingResponse>? checkIn = null;
            await Concurrency.RunAllAsync(
                async () => checkIn = await TestData.CheckInAsync(userId, bookingId),
                () => TestData.SweepNoShowsAsync());

            var final = await TestData.ReadBookingAsync(bookingId);

            if (checkIn!.Succeeded)
            {
                // Check-in won: it must not have been overwritten by the sweep.
                Assert.Equal(BookingStatus.CheckedIn, final.Status);
                Assert.NotNull(final.CheckedInAt);
            }
            else
            {
                Assert.Contains(checkIn.ErrorCode, new[] { "OUTSIDE_CHECKIN_WINDOW", "INVALID_BOOKING_STATUS" });
                Assert.Contains(final.Status, new[] { BookingStatus.Confirmed, BookingStatus.NoShow });
                Assert.Null(final.CheckedInAt);

                // Lost the race to the sweep itself => it must already be NoShow.
                if (checkIn.ErrorCode == "INVALID_BOOKING_STATUS")
                {
                    Assert.Equal(BookingStatus.NoShow, final.Status);
                }
            }

            // Whatever happened, one more sweep must leave a consistent end
            // state: a successful check-in stays CheckedIn; otherwise NoShow.
            //
            // The sweep only treats the booking as a no-show once the grace
            // period has fully elapsed. With the larger offsets (120/250 ms)
            // the first sweep can run slightly BEFORE that instant, leaving
            // the booking Confirmed. So wait out the window first; otherwise
            // this final assertion would be racing the clock instead of
            // checking the invariant.
            var untilEligible = (start + Grace) - DateTime.UtcNow + TimeSpan.FromMilliseconds(50);
            if (untilEligible > TimeSpan.Zero)
            {
                await Task.Delay(untilEligible);
            }

            await TestData.SweepNoShowsAsync();
            var settled = await TestData.ReadBookingAsync(bookingId);
            Assert.Equal(checkIn.Succeeded ? BookingStatus.CheckedIn : BookingStatus.NoShow, settled.Status);
        }
    }

    // Roadmap: "Cancel vs No-show — the final state must be valid and there
    // must not be two successful transitions." Here BOTH are eligible the
    // whole time (a Confirmed booking that started 20 minutes ago), so this
    // is a true head-to-head on the same row.
    [Fact]
    public async Task Cancel_racing_the_noshow_sweep_produces_exactly_one_transition()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var (userId, bookingId) = await SeedAsync(DateTime.UtcNow - Grace - TimeSpan.FromMinutes(5));

            Result<BookingResponse>? cancel = null;
            await Concurrency.RunAllAsync(
                async () => cancel = await TestData.CancelAsync(userId, bookingId),
                () => TestData.SweepNoShowsAsync());

            var final = await TestData.ReadBookingAsync(bookingId);

            if (cancel!.Succeeded)
            {
                Assert.Equal(BookingStatus.Cancelled, final.Status);
            }
            else
            {
                Assert.Equal("INVALID_BOOKING_STATUS", cancel.ErrorCode);
                Assert.Equal(BookingStatus.NoShow, final.Status);
            }

            // A later sweep must never turn a cancelled booking into NoShow.
            await TestData.SweepNoShowsAsync();
            Assert.Equal(final.Status, (await TestData.ReadBookingAsync(bookingId)).Status);
        }
    }

    [Fact]
    public async Task Two_simultaneous_check_ins_of_the_same_booking_exactly_one_wins()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (userId, bookingId) = await SeedAsync(DateTime.UtcNow.AddMinutes(5));

            var results = await Concurrency.RunAsync(
                () => TestData.CheckInAsync(userId, bookingId),
                () => TestData.CheckInAsync(userId, bookingId));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal("INVALID_BOOKING_STATUS", r.ErrorCode));
            Assert.Equal(BookingStatus.CheckedIn, (await TestData.ReadBookingAsync(bookingId)).Status);
        }
    }

    [Fact]
    public async Task Two_simultaneous_cancellations_of_the_same_booking_exactly_one_wins()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (userId, bookingId) = await SeedAsync(DateTime.UtcNow.AddDays(1));

            var results = await Concurrency.RunAsync(
                () => TestData.CancelAsync(userId, bookingId),
                () => TestData.CancelAsync(userId, bookingId));

            Assert.Equal(1, results.Count(r => r.Succeeded));
            Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal("INVALID_BOOKING_STATUS", r.ErrorCode));
            Assert.Equal(BookingStatus.Cancelled, (await TestData.ReadBookingAsync(bookingId)).Status);
        }
    }

    [Fact]
    public async Task Check_in_and_cancel_racing_each_other_exactly_one_wins()
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var (userId, bookingId) = await SeedAsync(DateTime.UtcNow.AddMinutes(5));

            Result<BookingResponse>? checkIn = null;
            Result<BookingResponse>? cancel = null;
            await Concurrency.RunAllAsync(
                async () => checkIn = await TestData.CheckInAsync(userId, bookingId),
                async () => cancel = await TestData.CancelAsync(userId, bookingId));

            Assert.True(checkIn!.Succeeded ^ cancel!.Succeeded,
                $"Expected exactly one of check-in/cancel to win. check-in: {checkIn.Succeeded}, cancel: {cancel.Succeeded}");

            var final = await TestData.ReadBookingAsync(bookingId);
            Assert.Equal(checkIn.Succeeded ? BookingStatus.CheckedIn : BookingStatus.Cancelled, final.Status);
        }
    }
}
