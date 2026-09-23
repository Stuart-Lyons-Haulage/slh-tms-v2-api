using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Separates execution progression from evidence completeness. A missed earlier geofence
/// remains an evidence gap, but once a later operational stop has credible completion evidence
/// the route must never point the vehicle backwards. Operational order is collection-first,
/// including compatibility for legacy runs that were persisted as Collect/Deliver pairs.
/// </summary>
public static class RunProgressionFrontier
{
    public static int Sequence(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var operationalStops = OperationalStopOrdering.Order(orderedStops);
        var frontier = 0;
        for (var index = 0; index < operationalStops.Count; index++)
        {
            var stop = operationalStops[index];
            if (completedStopIds.Contains(stop.Id) || activeStopId == stop.Id)
                frontier = Math.Max(frontier, index + 1);
        }
        return frontier;
    }

    public static LoadStop? NextOperationalStop(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var operationalStops = OperationalStopOrdering.Order(orderedStops);
        var frontier = Sequence(operationalStops, completedStopIds, activeStopId);
        return operationalStops.Skip(frontier).FirstOrDefault(stop => !completedStopIds.Contains(stop.Id));
    }

    public static IReadOnlyList<LoadStop> RemainingOperationalStops(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var operationalStops = OperationalStopOrdering.Order(orderedStops);
        var frontier = Sequence(operationalStops, completedStopIds, activeStopId);
        return operationalStops
            .Skip(frontier)
            .Where(stop => !completedStopIds.Contains(stop.Id))
            .ToList();
    }

    public static IReadOnlyList<LoadStop> EvidenceGapsBeforeFrontier(
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlySet<Guid> completedStopIds,
        Guid? activeStopId = null)
    {
        var operationalStops = OperationalStopOrdering.Order(orderedStops);
        var frontier = Sequence(operationalStops, completedStopIds, activeStopId);
        return operationalStops
            .Take(frontier)
            .Where(stop => !completedStopIds.Contains(stop.Id) && activeStopId != stop.Id)
            .ToList();
    }

    public static bool FinalStopCompleted(IReadOnlyList<LoadStop> orderedStops, IReadOnlySet<Guid> completedStopIds)
    {
        var final = OperationalStopOrdering.Order(orderedStops).LastOrDefault();
        return final is not null && completedStopIds.Contains(final.Id);
    }
}
