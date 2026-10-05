using Masar.Application.Common;
using Xunit;

namespace Masar.UnitTests;

// Roadmap 17.1 "Check-in window": before -> rejected, inside -> accepted,
// after -> rejected, boundary -> correct behavior.
//
//   window = [Start - Grace, min(Start + Grace, End))
//            inclusive lower bound, EXCLUSIVE upper bound
public class CheckInWindowTests
{
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(15);
    private static readonly DateTime Start = new(2030, 1, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = Start.AddHours(1);

    private static bool IsOpen(DateTime now) => CheckInWindow.IsOpen(now, Start, End, Grace);

    [Fact]
    public void Before_the_window_is_rejected()
    {
        Assert.False(IsOpen(Start - Grace - TimeSpan.FromSeconds(1)));
        Assert.False(IsOpen(Start.AddHours(-3)));
    }

    [Fact]
    public void Exactly_at_the_opening_boundary_is_accepted()
    {
        // Lower bound is inclusive.
        Assert.True(IsOpen(Start - Grace));
    }

    [Fact]
    public void Inside_the_window_is_accepted()
    {
        Assert.True(IsOpen(Start - TimeSpan.FromMinutes(5)));
        Assert.True(IsOpen(Start));
        Assert.True(IsOpen(Start + TimeSpan.FromMinutes(14)));
    }

    [Fact]
    public void One_tick_before_the_closing_boundary_is_accepted()
    {
        Assert.True(IsOpen(Start + Grace - TimeSpan.FromTicks(1)));
    }

    [Fact]
    public void Exactly_at_the_closing_boundary_is_rejected()
    {
        // Upper bound is exclusive. This is also the instant the no-show
        // sweep becomes eligible to take the booking (Start + Grace), so
        // the two rules meet without overlap or gap.
        Assert.False(IsOpen(Start + Grace));
    }

    [Fact]
    public void After_the_window_is_rejected()
    {
        Assert.False(IsOpen(Start + Grace + TimeSpan.FromSeconds(1)));
        Assert.False(IsOpen(End));
        Assert.False(IsOpen(End.AddHours(2)));
    }

    [Fact]
    public void Window_is_capped_at_the_booking_end_for_bookings_shorter_than_the_grace_period()
    {
        // A 10-minute booking with a 15-minute grace: the window must
        // close when the booking ends, not 15 minutes after it starts.
        var shortEnd = Start.AddMinutes(10);

        Assert.True(CheckInWindow.IsOpen(Start.AddMinutes(9), Start, shortEnd, Grace));
        Assert.False(CheckInWindow.IsOpen(shortEnd, Start, shortEnd, Grace));
        Assert.False(CheckInWindow.IsOpen(Start.AddMinutes(12), Start, shortEnd, Grace));
    }

    [Fact]
    public void Calculate_returns_the_documented_bounds()
    {
        var (opensAt, closesAt) = CheckInWindow.Calculate(Start, End, Grace);

        Assert.Equal(Start.AddMinutes(-15), opensAt);
        Assert.Equal(Start.AddMinutes(15), closesAt);
    }

    [Fact]
    public void Calculate_uses_the_booking_end_when_it_is_earlier_than_start_plus_grace()
    {
        var shortEnd = Start.AddMinutes(10);

        var (_, closesAt) = CheckInWindow.Calculate(Start, shortEnd, Grace);

        Assert.Equal(shortEnd, closesAt);
    }
}
