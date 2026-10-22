using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

// SLH TMS owns the Samsara schedule (recomputeScheduledTimes=false), so the exact stop times we
// send must satisfy Samsara's two hard rules: strictly-increasing scheduled arrivals across stops
// that carry external IDs, and a scheduled departure that is never before its own arrival. These
// cover the 400s seen in production ("multiple stops have the same scheduled arrival time" and
// "scheduled departure time before its scheduled arrival time").
public sealed class SamsaraDispatchStopTimingTests
{
    private static readonly DateTimeOffset Base = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    private static SamsaraRouteStopRequest Stop(int seq, DateTimeOffset? arrival, DateTimeOffset? departure) =>
        new(Guid.NewGuid(), seq, $"Stop {seq}", $"addr-{seq}", "Address", null, null, 250, arrival, departure, null);

    [Fact]
    public void Colliding_arrivals_are_bumped_to_strictly_increasing()
    {
        var stops = new List<SamsaraRouteStopRequest>
        {
            Stop(1, Base, null),
            Stop(2, Base, null),
            Stop(3, Base, null)
        };

        SamsaraDispatchController.EnsureStrictlyIncreasingArrivals(stops);

        Assert.Equal(Base, stops[0].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(1), stops[1].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(2), stops[2].ScheduledArrivalTime);
    }

    [Fact]
    public void Departure_before_arrival_is_clamped_up_to_arrival()
    {
        var stops = new List<SamsaraRouteStopRequest>
        {
            Stop(1, Base, Base.AddMinutes(5)),
            // Arrival after the first, but a departure recorded before this stop's own arrival.
            Stop(2, Base.AddMinutes(30), Base.AddMinutes(20))
        };

        SamsaraDispatchController.EnsureStrictlyIncreasingArrivals(stops);

        Assert.Equal(Base.AddMinutes(30), stops[1].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(30), stops[1].ScheduledDepartureTime);
    }

    [Fact]
    public void Bumping_an_arrival_also_pulls_its_departure_forward()
    {
        var stops = new List<SamsaraRouteStopRequest>
        {
            Stop(1, Base.AddMinutes(10), null),
            // Collides with stop 1; its departure equals the pre-bump arrival and must follow it.
            Stop(2, Base.AddMinutes(10), Base.AddMinutes(10))
        };

        SamsaraDispatchController.EnsureStrictlyIncreasingArrivals(stops);

        Assert.Equal(Base.AddMinutes(11), stops[1].ScheduledArrivalTime);
        Assert.True(stops[1].ScheduledDepartureTime >= stops[1].ScheduledArrivalTime);
    }

    [Fact]
    public void Following_stops_cannot_arrive_before_the_origin_departs()
    {
        var stops = new List<SamsaraRouteStopRequest>
        {
            Stop(1, null, Base.AddMinutes(10)), // origin: departure only (yard sign-on/walkaround)
            Stop(2, Base, null),                // fallback-timed earlier than the origin departure
            Stop(3, Base, null)
        };

        SamsaraDispatchController.EnsureStrictlyIncreasingArrivals(stops);

        Assert.Null(stops[0].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(10), stops[0].ScheduledDepartureTime);
        // Both following stops are pushed strictly after the origin departure, in order.
        Assert.Equal(Base.AddMinutes(11), stops[1].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(12), stops[2].ScheduledArrivalTime);
    }

    [Fact]
    public void Missing_stop_arrival_times_remain_null_while_valid_times_are_preserved()
    {
        var stops = new List<SamsaraRouteStopRequest>
        {
            Stop(1, null, Base),
            Stop(2, null, null),
            Stop(3, Base.AddMinutes(45), null)
        };

        SamsaraDispatchController.EnsureStrictlyIncreasingArrivals(stops);

        Assert.Null(stops[0].ScheduledArrivalTime);
        Assert.Null(stops[1].ScheduledArrivalTime);
        Assert.Null(stops[1].ScheduledDepartureTime);
        Assert.Equal(Base.AddMinutes(45), stops[2].ScheduledArrivalTime);
    }

    [Fact]
    public void Already_valid_times_are_left_unchanged()
    {
        var stops = new List<SamsaraRouteStopRequest>
        {
            Stop(1, Base, Base.AddMinutes(5)),
            Stop(2, Base.AddMinutes(30), Base.AddMinutes(35)),
            Stop(3, Base.AddMinutes(60), Base.AddMinutes(60))
        };

        SamsaraDispatchController.EnsureStrictlyIncreasingArrivals(stops);

        Assert.Equal(Base, stops[0].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(30), stops[1].ScheduledArrivalTime);
        Assert.Equal(Base.AddMinutes(35), stops[1].ScheduledDepartureTime);
        Assert.Equal(Base.AddMinutes(60), stops[2].ScheduledArrivalTime);
    }
}
