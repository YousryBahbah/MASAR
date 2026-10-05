namespace Masar.Application.Common;

// The check-in window rule as a pure function, so it can be unit-tested
// without a database. BookingService.CheckInAsync calls IsOpen; nothing
// else should re-derive the window.
//
// Window = [Start - Grace, min(Start + Grace, End)) — the upper bound is
// exclusive and is capped at the booking's own end, so a booking shorter
// than the grace period can never be checked into after it has ended.
public static class CheckInWindow
{
    public static (DateTime OpensAtUtc, DateTime ClosesAtUtc) Calculate(
        DateTime bookingStartUtc, DateTime bookingEndUtc, TimeSpan gracePeriod)
    {
        var opensAt = bookingStartUtc - gracePeriod;
        var graceEnd = bookingStartUtc + gracePeriod;
        var closesAt = graceEnd < bookingEndUtc ? graceEnd : bookingEndUtc;
        return (opensAt, closesAt);
    }

    public static bool IsOpen(
        DateTime nowUtc, DateTime bookingStartUtc, DateTime bookingEndUtc, TimeSpan gracePeriod)
    {
        var (opensAt, closesAt) = Calculate(bookingStartUtc, bookingEndUtc, gracePeriod);
        return nowUtc >= opensAt && nowUtc < closesAt;
    }
}
