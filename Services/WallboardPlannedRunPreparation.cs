using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

internal static class WallboardPlannedRunPreparation
{
    public static void ApplyDispatchStartFallback(Load load, DateTimeOffset? dispatchPlannedStartUtc)
    {
        if (dispatchPlannedStartUtc is null) return;
        var firstStop = load.Stops.OrderBy(stop => stop.Sequence).FirstOrDefault();
        if (firstStop is not null && firstStop.PlannedArrivalUtc is null)
            firstStop.PlannedArrivalUtc = dispatchPlannedStartUtc;
    }

    public static List<Load> OrderByOperationalStart(IEnumerable<Load> loads) =>
        loads.OrderBy(load => load.Stops
                .Where(stop => stop.PlannedArrivalUtc is not null)
                .Select(stop => stop.PlannedArrivalUtc)
                .Min() ?? DateTimeOffset.MaxValue)
            .ThenBy(load => load.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
