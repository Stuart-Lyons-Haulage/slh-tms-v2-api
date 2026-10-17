using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class WallboardPlannedRunPreparationHardeningTests
{
    [Fact]
    public void DispatchFallback_DoesNotThrowWhenRunHasNoStops()
    {
        var load = Load("RUN 1");
        var start = DateTimeOffset.Parse("2026-09-09T03:30:00Z");

        var exception = Record.Exception(() => WallboardPlannedRunPreparation.ApplyDispatchStartFallback(load, start));

        Assert.Null(exception);
        Assert.Empty(load.Stops);
    }

    [Fact]
    public void DispatchFallback_FillsOnlyBlankFirstStopTime()
    {
        var load = Load("RUN 1");
        load.Stops.Add(new LoadStop { Sequence = 1, Name = "Selsey" });
        var start = DateTimeOffset.Parse("2026-09-09T03:30:00Z");

        WallboardPlannedRunPreparation.ApplyDispatchStartFallback(load, start);

        Assert.Equal(start, load.Stops[0].PlannedArrivalUtc);
    }

    [Fact]
    public void OperationalSort_UsesRecoveredStartTime()
    {
        var later = Load("RUN 2");
        later.Stops.Add(new LoadStop { Sequence = 1, Name = "Runcton", PlannedArrivalUtc = DateTimeOffset.Parse("2026-09-09T06:00:00Z") });
        var recoveredEarly = Load("RUN 1");
        recoveredEarly.Stops.Add(new LoadStop { Sequence = 1, Name = "Selsey" });
        WallboardPlannedRunPreparation.ApplyDispatchStartFallback(recoveredEarly, DateTimeOffset.Parse("2026-09-09T04:00:00Z"));

        var ordered = WallboardPlannedRunPreparation.OrderByOperationalStart([later, recoveredEarly]);

        Assert.Equal(new[] { "RUN 1", "RUN 2" }, ordered.Select(load => load.Reference));
    }

    private static Load Load(string reference) => new()
    {
        Reference = reference,
        PlanningDate = new DateOnly(2026, 9, 9),
        Status = LoadStatus.Planned
    };
}
