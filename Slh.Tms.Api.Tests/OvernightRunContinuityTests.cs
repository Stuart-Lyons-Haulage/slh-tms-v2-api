using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OvernightRunContinuityTests
{
    [Fact]
    public void Evening_collection_before_morning_continuation_is_shifted_to_previous_day()
    {
        var load = new Load
        {
            Reference = "PLAN-20260828-2",
            PlanningDate = new DateOnly(2026, 8, 28),
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · GHS-Greenhouse Growers", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 28, 16, 0, 0, TimeSpan.Zero) },
                new LoadStop { Sequence = 2, Name = "Collect · NWF-Runcton", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.Zero) },
                new LoadStop { Sequence = 3, Name = "Deliver · Aldi-Darlington", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 28, 11, 0, 0, TimeSpan.Zero) },
                new LoadStop { Sequence = 4, Name = "Deliver · Morrisons-Stockton", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 28, 13, 0, 0, TimeSpan.Zero) }
            ]
        };

        Assert.True(OvernightRunContinuity.Apply(load));
        Assert.Equal(new DateTimeOffset(2026, 8, 27, 16, 0, 0, TimeSpan.Zero), load.Stops[0].PlannedArrivalUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 5, 0, 0, TimeSpan.Zero), load.Stops[1].PlannedArrivalUtc);
        Assert.True(OvernightRunContinuity.IsCarryIn(load));
    }

    [Fact]
    public void Ordinary_same_day_evening_run_is_not_shifted()
    {
        var original = new DateTimeOffset(2026, 8, 28, 16, 0, 0, TimeSpan.Zero);
        var load = new Load
        {
            Reference = "PLAN-20260828-8",
            PlanningDate = new DateOnly(2026, 8, 28),
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collect · Site A", PlannedArrivalUtc = original },
                new LoadStop { Sequence = 2, Name = "Deliver · Site B", PlannedArrivalUtc = new DateTimeOffset(2026, 8, 28, 19, 0, 0, TimeSpan.Zero) }
            ]
        };

        Assert.False(OvernightRunContinuity.Apply(load));
        Assert.Equal(original, load.Stops[0].PlannedArrivalUtc);
        Assert.False(OvernightRunContinuity.IsCarryIn(load));
    }
}
