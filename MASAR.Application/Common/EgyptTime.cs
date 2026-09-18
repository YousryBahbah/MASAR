namespace Masar.Application.Common;

// Masar is locked to Egypt-only (no per-location timezone field, per the
// Step 2 recap), and Booking/MaintenancePeriod both store UTC. Search
// needs to compare a caller-supplied Egypt-local date+time against those
// UTC rows, so *something* has to do the local->UTC conversion.
//
// This wasn't decided anywhere in the locked plan — Booking creation
// (Step 11) hasn't been built yet either, so there's no existing
// conversion logic to reuse. Using the real "Africa/Cairo" IANA zone
// (via TimeZoneInfo, which .NET resolves cross-platform) rather than a
// hardcoded UTC+2 offset, since Egypt's DST rules have actually changed
// more than once in the last decade — a hardcoded offset would silently
// go wrong the next time that happens, a real offset lookup won't.
//
// Step 11 will need this exact same conversion for booking creation —
// reuse this rather than re-deriving it.
public static class EgyptTime
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
}
