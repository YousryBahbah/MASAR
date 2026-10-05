using Masar.Application.Common;
using Xunit;

namespace Masar.UnitTests;

public class EgyptTimeTests
{
    [Fact]
    public void Winter_local_time_converts_to_utc_minus_two_hours()
    {
        // Mid-January is standard time in Egypt (UTC+2) in every DST regime
        // the country has used.
        var utc = EgyptTime.ToUtc(new DateOnly(2030, 1, 15), new TimeOnly(10, 0));

        Assert.Equal(new DateTime(2030, 1, 15, 8, 0, 0), utc);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Theory]
    [InlineData(2030, 1, 15, 10, 0)]
    [InlineData(2030, 7, 20, 14, 30)]
    [InlineData(2030, 3, 3, 23, 59)]
    public void Local_to_utc_and_back_round_trips_for_ordinary_times(int y, int m, int d, int hh, int mm)
    {
        var date = new DateOnly(y, m, d);
        var time = new TimeOnly(hh, mm);

        var (roundTripDate, roundTripTime) = EgyptTime.FromUtc(EgyptTime.ToUtc(date, time));

        Assert.Equal(date, roundTripDate);
        Assert.Equal(time, roundTripTime);
    }

    [Fact]
    public void A_time_inside_the_spring_forward_gap_is_reported_invalid()
    {
        var gap = DstTestSupport.FindSpringForwardGap();

        Assert.False(EgyptTime.IsValidLocalTime(gap.Date, gap.InvalidTime));
    }

    [Fact]
    public void Converting_a_time_inside_the_gap_throws_which_is_why_validators_check_first()
    {
        var gap = DstTestSupport.FindSpringForwardGap();

        // If this ever stops throwing, the validators' DST rule is no longer
        // needed; until then it is what keeps this from becoming a 500.
        Assert.ThrowsAny<ArgumentException>(() => EgyptTime.ToUtc(gap.Date, gap.InvalidTime));
    }

    [Fact]
    public void A_time_in_the_repeated_fall_back_hour_is_valid_and_converts_without_throwing()
    {
        var date = DstTestSupport.FindFallBackAmbiguousDate(out var time);

        Assert.True(EgyptTime.IsValidLocalTime(date, time));
        var utc = EgyptTime.ToUtc(date, time);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Ordinary_times_are_valid()
    {
        Assert.True(EgyptTime.IsValidLocalTime(new DateOnly(2030, 1, 15), new TimeOnly(10, 0)));
        Assert.True(EgyptTime.IsValidLocalTime(new DateOnly(2030, 8, 15), new TimeOnly(0, 0)));
    }
}
