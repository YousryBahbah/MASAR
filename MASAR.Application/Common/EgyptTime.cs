namespace Masar.Application.Common;

// Single conversion point between Egypt-local wall-clock time (what a
// caller sends/sees) and UTC (what's actually stored). Uses a real IANA
// timezone lookup rather than a hardcoded UTC+2 offset, deliberately —
// Egypt's DST policy has changed more than once in recent years, and a
// hardcoded offset would silently break the next time that happens.
public static class EgyptTime
{
    private static readonly TimeZoneInfo Zone =
        TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");

    // Egypt observes DST, so a spring-forward night has a wall-clock hour
    // that never exists (e.g. 00:30 on the night clocks jump 00:00 -> 01:00).
    // ConvertTimeToUtc THROWS for such a time, which would surface as a 500.
    // Validators call this first so it becomes a clean 400 VALIDATION_FAILED.
    // Ambiguous times (the repeated hour when clocks fall back) are valid:
    // ConvertTimeToUtc resolves them using the standard offset.
    public static bool IsValidLocalTime(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return !Zone.IsInvalidTime(local);
    }

    // Built for Step 10/11 — Egypt-local date+time (as sent by a caller)
    // to the UTC DateTime actually stored on a row.
    public static DateTime ToUtc(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    // New for Step 13 — the reverse direction. CheckIn/Cancel responses
    // need to show a booking's already-stored UTC StartTime/EndTime back
    // in Egypt-local terms, matching the same response shape Create
    // already uses, rather than exposing raw UTC to the caller.
    public static (DateOnly Date, TimeOnly Time) FromUtc(DateTime utc)
    {
        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, Zone);
        return (DateOnly.FromDateTime(local), TimeOnly.FromDateTime(local));
    }
}
