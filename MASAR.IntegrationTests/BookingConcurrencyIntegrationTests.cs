using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;
using Masar.Domain.Enums;
using Masar.IntegrationTests.Support;
using Xunit;

namespace Masar.IntegrationTests;

// Roadmap 17.2 — "Double Booking" and "Booking Limits".
//
// These use two or more separate DbContexts (one DI scope per request, as in
// production), released at the same instant against real SQL Server. Mocking
// EF Core could not prove any of this: the guarantee lives in SERIALIZABLE
// locking inside the database engine.
//
// Under SERIALIZABLE, a losing request is not always told "unavailable" — it
// may instead be chosen as a deadlock victim (SQL error 1205), which the
// service reports as CONCURRENT_WRITE_CONFLICT. Both are correct outcomes for
// the loser. What is NEVER allowed is for both to win.
[Collection(SqlServerCollection.Name)]
public class BookingConcurrencyIntegrationTests : IAsyncLifetime
{
    public Task InitializeAsync() => TestDatabase.EnsureReadyAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly string[] AcceptableSlotLosses =
        ["WORKSPACE_UNAVAILABLE", "CONCURRENT_WRITE_CONFLICT"];

    private static readonly string[] AcceptableLimitLosses =
        ["ACTIVE_BOOKING_LIMIT_EXCEEDED", "CONCURRENT_WRITE_CONFLICT"];

    [Fact]
    public async Task Two_members_racing_for_the_same_slot_exactly_one_wins()
    {
        var userA = await TestData.CreateMemberAsync();
        var userB = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var request = TestData.SlotRequest(workspace.Id, TestData.FutureDate());

        var results = await Concurrency.RunAsync(
            () => TestData.CreateBookingViaServiceAsync(userA.Id, request),
            () => TestData.CreateBookingViaServiceAsync(userB.Id, request));

        AssertExactlyOneWinner(results, AcceptableSlotLosses);
        Assert.Equal(1, await TestData.CountBlockingBookingsAsync(workspace.Id));
    }

    [Fact]
    public async Task Overlapping_but_not_identical_slots_also_cannot_both_win()
    {
        var userA = await TestData.CreateMemberAsync();
        var userB = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var results = await Concurrency.RunAsync(
            () => TestData.CreateBookingViaServiceAsync(userA.Id, TestData.SlotRequest(workspace.Id, date, 10, 12)),
            () => TestData.CreateBookingViaServiceAsync(userB.Id, TestData.SlotRequest(workspace.Id, date, 11, 13)));

        AssertExactlyOneWinner(results, AcceptableSlotLosses);
        Assert.Equal(1, await TestData.CountBlockingBookingsAsync(workspace.Id));
    }

    [Fact]
    public async Task Six_members_racing_for_one_slot_exactly_one_wins()
    {
        var users = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            users.Add((await TestData.CreateMemberAsync()).Id);
        }

        var workspace = await TestData.CreateWorkspaceAsync();
        var request = TestData.SlotRequest(workspace.Id, TestData.FutureDate());

        var results = await Concurrency.RunAsync(
            users.Select(id => (Func<Task<Result<BookingResponse>>>)(() =>
                TestData.CreateBookingViaServiceAsync(id, request))).ToArray());

        AssertExactlyOneWinner(results, AcceptableSlotLosses);
        Assert.Equal(1, await TestData.CountBlockingBookingsAsync(workspace.Id));
    }

    [Fact]
    public async Task Racing_for_non_overlapping_slots_never_reports_the_slot_as_unavailable()
    {
        // Guards against the opposite failure: a correct request being
        // rejected as a business conflict. Note what this does NOT assert:
        // that both succeed. Under SERIALIZABLE a read that finds nothing
        // locks the whole gap up to the next existing index key, so two
        // non-overlapping inserts into the same empty gap can still collide
        // and one becomes a deadlock victim. That is reported as the
        // retry-able CONCURRENT_WRITE_CONFLICT (no automatic retry in v1) —
        // acceptable here; WORKSPACE_UNAVAILABLE would be a real bug.
        var userA = await TestData.CreateMemberAsync();
        var userB = await TestData.CreateMemberAsync();
        var workspace = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var results = await Concurrency.RunAsync(
            () => TestData.CreateBookingViaServiceAsync(userA.Id, TestData.SlotRequest(workspace.Id, date, 10, 11)),
            () => TestData.CreateBookingViaServiceAsync(userB.Id, TestData.SlotRequest(workspace.Id, date, 14, 15)));

        Assert.DoesNotContain(results, r => r.ErrorCode == "WORKSPACE_UNAVAILABLE");
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Equal("CONCURRENT_WRITE_CONFLICT", r.ErrorCode));
    }

    // ------------------------------------------------------------- limits

    // The one-user-racing-themselves case. The two requests target DIFFERENT
    // workspaces, so the per-workspace overlap check can never stop them: only
    // the in-transaction limit re-check can. The user already holds one active
    // booking and the limit is two, so at most ONE of the two may win.
    [Fact]
    public async Task One_member_racing_themselves_across_workspaces_cannot_exceed_the_limit()
    {
        var member = await TestData.CreateMemberAsync();
        var date = TestData.FutureDate();
        var existing = await TestData.CreateWorkspaceAsync();
        var first = await TestData.CreateWorkspaceAsync();
        var second = await TestData.CreateWorkspaceAsync();

        var seeded = await TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(existing.Id, date));
        Assert.True(seeded.Succeeded, seeded.ErrorMessage);

        var results = await Concurrency.RunAsync(
            () => TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(first.Id, date)),
            () => TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(second.Id, date)));

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, AcceptableLimitLosses));
        Assert.True(await TestData.CountActiveBookingsAsync(member.Id) <= 2,
            "The user ended up with more active bookings than the limit allows.");
    }

    [Fact]
    public async Task Five_concurrent_requests_from_one_member_never_exceed_the_limit()
    {
        var member = await TestData.CreateMemberAsync();
        var date = TestData.FutureDate();
        var workspaces = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            workspaces.Add((await TestData.CreateWorkspaceAsync()).Id);
        }

        var results = await Concurrency.RunAsync(
            workspaces.Select(id => (Func<Task<Result<BookingResponse>>>)(() =>
                TestData.CreateBookingViaServiceAsync(member.Id, TestData.SlotRequest(id, date)))).ToArray());

        // Deadlock victims may reduce the number of winners below the limit
        // (that is a retry-able outcome), but it must never exceed it.
        Assert.InRange(results.Count(r => r.Succeeded), 1, 2);
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.ErrorCode, AcceptableLimitLosses));
        Assert.True(await TestData.CountActiveBookingsAsync(member.Id) <= 2,
            "The user ended up with more active bookings than the limit allows.");
    }

    [Fact]
    public async Task Unrelated_members_on_unrelated_workspaces_never_get_a_business_rejection()
    {
        var userA = await TestData.CreateMemberAsync();
        var userB = await TestData.CreateMemberAsync();
        var workspaceA = await TestData.CreateWorkspaceAsync();
        var workspaceB = await TestData.CreateWorkspaceAsync();
        var date = TestData.FutureDate();

        var results = await Concurrency.RunAsync(
            () => TestData.CreateBookingViaServiceAsync(userA.Id, TestData.SlotRequest(workspaceA.Id, date)),
            () => TestData.CreateBookingViaServiceAsync(userB.Id, TestData.SlotRequest(workspaceB.Id, date)));

        // Fully independent requests share no business conflict, so neither
        // may be told "unavailable" or "limit exceeded". A retry-able
        // CONCURRENT_WRITE_CONFLICT is still possible if both ranges are
        // empty gaps in the same index region (see the test above).
        Assert.All(results, r =>
        {
            if (!r.Succeeded)
            {
                Assert.Equal("CONCURRENT_WRITE_CONFLICT", r.ErrorCode);
            }
        });
        Assert.Contains(results, r => r.Succeeded);
    }

    // ------------------------------------------------------------- helpers

    private static void AssertExactlyOneWinner(
        IReadOnlyCollection<Result<BookingResponse>> results, string[] acceptableLossCodes)
    {
        var winners = results.Count(r => r.Succeeded);
        var losers = results.Where(r => !r.Succeeded).ToList();

        Assert.True(winners == 1,
            $"Expected exactly one winner but {winners} succeeded. Outcomes: " +
            string.Join(", ", results.Select(r => r.Succeeded ? "OK" : r.ErrorCode)));

        Assert.All(losers, loser => Assert.Contains(loser.ErrorCode, acceptableLossCodes));
    }
}
