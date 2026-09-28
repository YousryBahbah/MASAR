using Masar.Domain.Enums;
using Masar.Infrastructure.Persistence;
using Masar.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Masar.Infrastructure.Jobs;

// Recurring, not per-booking scheduled — a deliberate choice, not the
// only option available. A per-booking approach (schedule a specific
// job at booking-creation time, targeting that exact booking) was
// considered and rejected: it couples BookingService to Hangfire at
// creation time, and needs its own reschedule-on-cancel logic (a
// scheduled job for a since-cancelled booking must still check current
// status before acting, or it'll wrongly flip a cancelled booking to
// NoShow). A recurring scan avoids both for free:
//
// - Idempotent by construction: each sweep's WHERE clause only matches
//   rows still in the source state, so a booking already transitioned
//   (by an earlier run, or by the user checking in/cancelling in the
//   meantime) simply won't match on a later run. No explicit locking or
//   dedup logic needed beyond that.
// - Cancellation interaction is handled implicitly: a cancelled booking
//   is no longer Confirmed, so it stops matching SweepNoShowsAsync's
//   filter with no special-case code required.
// - Job recovery on restart is automatic: because each sweep queries
//   *current* state rather than depending on a trigger firing at a
//   specific instant, a period where the app or Hangfire server was
//   down doesn't lose anything — the next successful run finds every
//   booking that became eligible at any point during the outage and
//   transitions them all, same as if nothing had been missed.
//
// Both sweeps below use ExecuteUpdateAsync — a single, server-side
// UPDATE ... WHERE ... statement — rather than the original
// load-into-memory / mutate / SaveChangesAsync shape. This isn't just
// an efficiency choice: it's what actually closes the race against a
// user's own CheckInAsync/CancelAsync (BookingService), which use the
// same ExecuteUpdateAsync pattern for the same reason. A tracked-entity
// SaveChangesAsync generates `UPDATE ... WHERE Id = X` with no status
// guard at all — it would blindly overwrite whatever a concurrent
// check-in/cancel had just written, last-write-wins, regardless of
// which one actually happened first. Filtering the UPDATE itself on
// `Status == Confirmed` (not just the SELECT that used to precede it)
// means each sweep only ever touches rows still genuinely eligible at
// the instant the UPDATE runs.
public class BookingLifecycleJobs
{
    private readonly ApplicationDbContext _db;
    private readonly ILogger<BookingLifecycleJobs> _logger;

    public BookingLifecycleJobs(ApplicationDbContext db, ILogger<BookingLifecycleJobs> logger)
    {
        _db = db;
        _logger = logger;
    }

    // Confirmed, past StartTime + CheckInGracePeriod, never checked in
    // -> NoShow. Uses the same CheckInGracePeriod constant BookingService
    // uses for the check-in window's upper bound — see BookingService for
    // why these two can't be allowed to drift into different values.
    public async Task SweepNoShowsAsync()
    {
        var cutoff = DateTime.UtcNow - BookingService.CheckInGracePeriod;
        var now = DateTime.UtcNow;

        var affected = await _db.Bookings
            .Where(b => b.Status == BookingStatus.Confirmed && b.StartTime <= cutoff)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(b => b.Status, BookingStatus.NoShow)
                .SetProperty(b => b.UpdatedAt, now));

        if (affected > 0)
        {
            _logger.LogInformation("Marked {Count} booking(s) as NoShow.", affected);
        }
    }

    // CheckedIn, past EndTime -> Completed.
    public async Task SweepCompletionsAsync()
    {
        var now = DateTime.UtcNow;

        var affected = await _db.Bookings
            .Where(b => b.Status == BookingStatus.CheckedIn && b.EndTime < now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(b => b.Status, BookingStatus.Completed)
                .SetProperty(b => b.UpdatedAt, now));

        if (affected > 0)
        {
            _logger.LogInformation("Marked {Count} booking(s) as Completed.", affected);
        }
    }
}
