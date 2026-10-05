using Masar.Application.Common;
using Masar.Application.DTOs.Bookings;
using Masar.Application.Validators.Bookings;
using Xunit;

namespace Masar.UnitTests;

// Roadmap 17.1 "Booking validation" — the parts that are pure request
// validation. The other rules the roadmap lists under this heading are
// enforced against stored state in BookingService (past start, operating
// hours, cross-midnight/interval), so they are proven in the integration
// tests where the real service runs against a real database.
public class CreateBookingRequestValidatorTests
{
    private static readonly CreateBookingRequestValidator Validator = new();

    private static readonly DateOnly SomeDay = new(2030, 6, 15);

    private static CreateBookingRequest Valid() =>
        new(1, SomeDay, new TimeOnly(10, 0), new TimeOnly(11, 0));

    private static string[] ErrorMessages(CreateBookingRequest request) =>
        Validator.Validate(request).Errors.Select(e => e.ErrorMessage).ToArray();

    [Fact]
    public void Start_before_end_is_valid()
    {
        Assert.True(Validator.Validate(Valid()).IsValid);
    }

    [Fact]
    public void Start_equal_to_end_is_invalid()
    {
        var request = Valid() with { StartTime = new TimeOnly(10, 0), EndTime = new TimeOnly(10, 0) };

        Assert.Contains("startTime must be before endTime.", ErrorMessages(request));
    }

    [Fact]
    public void Start_after_end_is_invalid()
    {
        var request = Valid() with { StartTime = new TimeOnly(16, 0), EndTime = new TimeOnly(14, 0) };

        Assert.Contains("startTime must be before endTime.", ErrorMessages(request));
    }

    // A booking that would cross midnight cannot be expressed: it has a
    // single Date plus two times, so an "end" earlier than the "start"
    // reads as end-before-start and is rejected as an invalid interval.
    [Fact]
    public void Cross_midnight_request_is_rejected_as_an_invalid_interval()
    {
        var request = Valid() with { StartTime = new TimeOnly(23, 0), EndTime = new TimeOnly(1, 0) };

        Assert.Contains("startTime must be before endTime.", ErrorMessages(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Non_positive_workspace_id_is_invalid(int workspaceId)
    {
        var result = Validator.Validate(Valid() with { WorkspaceId = workspaceId });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBookingRequest.WorkspaceId));
    }

    [Fact]
    public void Missing_date_is_invalid()
    {
        var result = Validator.Validate(Valid() with { Date = null });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBookingRequest.Date));
    }

    [Fact]
    public void Missing_start_time_is_invalid_and_does_not_also_trigger_the_interval_rule()
    {
        var result = Validator.Validate(Valid() with { StartTime = null });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBookingRequest.StartTime));
        Assert.DoesNotContain(result.Errors, e => e.ErrorMessage.Contains("must be before"));
    }

    [Fact]
    public void Missing_end_time_is_invalid()
    {
        var result = Validator.Validate(Valid() with { EndTime = null });

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(CreateBookingRequest.EndTime));
    }

    // ----- Egypt DST gap (skipped hour on the spring-forward night) -----

    [Fact]
    public void Start_inside_the_dst_gap_is_rejected_instead_of_throwing_later()
    {
        var gap = DstTestSupport.FindSpringForwardGap();

        var request = new CreateBookingRequest(1, gap.Date, gap.InvalidTime, gap.InvalidTime.AddHours(2));
        var errors = ErrorMessages(request);

        Assert.Contains(errors, m => m.Contains("startTime") && m.Contains("daylight-saving gap"));
    }

    [Fact]
    public void End_inside_the_dst_gap_is_rejected()
    {
        var gap = DstTestSupport.FindSpringForwardGap();

        // The start time may itself fall in the gap on this date; this test
        // only asserts that the END rule fires on its own message.
        var request = new CreateBookingRequest(1, gap.Date, new TimeOnly(0, 0), gap.InvalidTime);
        var errors = ErrorMessages(request);

        Assert.Contains(errors, m => m.Contains("endTime") && m.Contains("daylight-saving gap"));
    }

    [Fact]
    public void An_ordinary_time_is_never_flagged_as_a_dst_gap()
    {
        Assert.DoesNotContain(ErrorMessages(Valid()), m => m.Contains("daylight-saving gap"));
    }
}

// Finds a real "skipped" local time by asking the zone data, instead of
// hardcoding a transition date. Egypt's DST rules have changed more than
// once; a hardcoded date would silently go stale. If the machine's tz data
// has no gap within the scan window the test FAILS with a clear message
// rather than passing without asserting anything.
internal static class DstTestSupport
{
    public static (DateOnly Date, TimeOnly InvalidTime) FindSpringForwardGap()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        var cursor = new DateTime(DateTime.UtcNow.Year, 1, 1);
        var limit = cursor.AddYears(3);

        for (; cursor < limit; cursor = cursor.AddDays(1))
        {
            for (var minuteOfDay = 0; minuteOfDay < 24 * 60; minuteOfDay += 30)
            {
                var candidate = cursor.AddMinutes(minuteOfDay);
                if (zone.IsInvalidTime(candidate))
                {
                    return (DateOnly.FromDateTime(candidate), TimeOnly.FromDateTime(candidate));
                }
            }
        }

        throw new InvalidOperationException(
            "No daylight-saving gap found in Africa/Cairo within the next three years. " +
            "Either this machine's time-zone data is outdated or Egypt has abolished DST; " +
            "the DST-gap tests cannot prove anything on this machine.");
    }

    public static DateOnly FindFallBackAmbiguousDate(out TimeOnly ambiguousTime)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo");
        var cursor = new DateTime(DateTime.UtcNow.Year, 1, 1);
        var limit = cursor.AddYears(3);

        for (; cursor < limit; cursor = cursor.AddDays(1))
        {
            for (var minuteOfDay = 0; minuteOfDay < 24 * 60; minuteOfDay += 30)
            {
                var candidate = cursor.AddMinutes(minuteOfDay);
                if (zone.IsAmbiguousTime(candidate))
                {
                    ambiguousTime = TimeOnly.FromDateTime(candidate);
                    return DateOnly.FromDateTime(candidate);
                }
            }
        }

        throw new InvalidOperationException("No ambiguous (fall-back) local time found in Africa/Cairo.");
    }
}
