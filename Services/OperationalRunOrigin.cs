using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves depot-origin execution evidence for an operational run. Lake Lane is not
/// a planned customer stop: leaving its DOT/Falcon geofence proves the vehicle has
/// started the first leg toward stop 1.
/// </summary>
public static class OperationalRunOrigin
{
    private static readonly TimeSpan EarliestDepartureBeforeFirstStop = TimeSpan.FromHours(4);
    private static readonly TimeSpan LatestDepartureAfterFirstStop = TimeSpan.FromHours(1);

    public static DerivedVisit? LakeLaneDepartureFor(EmbeddedGeofenceSnapshot snapshot, Load load)
    {
        if (load.VehicleId is not Guid vehicleId) return null;

        var departures = snapshot.Visits
            .Where(visit => visit.VehicleId == vehicleId &&
                            visit.ExitedAtUtc is not null &&
                            GeofencePlanningMatch.IsLakeLaneFence(visit.Fence))
            .OrderByDescending(visit => visit.ExitedAtUtc)
            .ToList();
        if (departures.Count == 0) return null;

        var firstPlannedArrival = (load.Stops ?? [])
            .OrderBy(stop => stop.Sequence)
            .Select(stop => stop.PlannedArrivalUtc)
            .FirstOrDefault(value => value is not null);
        if (firstPlannedArrival is null) return null;

        var earliest = firstPlannedArrival.Value - EarliestDepartureBeforeFirstStop;
        var latest = firstPlannedArrival.Value + LatestDepartureAfterFirstStop;
        return departures
            .Where(visit => visit.ExitedAtUtc >= earliest && visit.ExitedAtUtc <= latest)
            .OrderBy(visit => Math.Abs((firstPlannedArrival.Value - visit.ExitedAtUtc!.Value).TotalMinutes))
            .FirstOrDefault();
    }

    public static (decimal Longitude, decimal Latitude)? FenceCentre(EmbeddedFence fence)
    {
        if (fence.Points.Count == 0) return null;
        return ((decimal)fence.Points.Average(point => point.Longitude),
                (decimal)fence.Points.Average(point => point.Latitude));
    }
}