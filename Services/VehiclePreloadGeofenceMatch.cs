using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Safely projects an unallocated geofence visit from the same vehicle onto a
/// current operating-day collection stop. Driver identity is deliberately not
/// part of the match because SLH vehicles are often preloaded by another driver.
/// </summary>
public static class VehiclePreloadGeofenceMatch
{
    public const int HistoryLookbackHours = 16;
    public const int HistoryCarryForwardHours = 12;
    private const double MaximumPlannedDistanceHours = 18;
    private const double AmbiguityToleranceMinutes = 30;

    public static VehiclePreloadMatch? Match(GeofenceVisit visit, EmbeddedFence fence, IReadOnlyCollection<Load> loads)
    {
        // Never steal evidence already owned by another run. This is only for a
        // physical visit that could not be assigned when Falcon first supplied it.
        if (visit.LoadId is not null || visit.VehicleId is not Guid vehicleId) return null;

        var candidates = new List<Candidate>();
        foreach (var sourceLoad in loads.Where(load => load.VehicleId == vehicleId && load.Status != LoadStatus.Cancelled))
        {
            var load = CloneForOperatingDay(sourceLoad);
            var ordered = OperationalStopOrdering.Order(load.Stops);
            for (var index = 0; index < ordered.Count; index++)
            {
                var stop = ordered[index];
                if (!OperationalStopOrdering.IsCollection(stop.Name)) continue;
                if (!GeofencePlanningMatch.SamePhysicalSite(stop, fence)) continue;

                var distanceMinutes = stop.PlannedArrivalUtc is DateTimeOffset planned
                    ? Math.Abs((planned - visit.EnteredAtUtc).TotalMinutes)
                    : double.MaxValue;
                if (distanceMinutes != double.MaxValue && distanceMinutes > MaximumPlannedDistanceHours * 60) continue;
                candidates.Add(new Candidate(sourceLoad, stop, index + 1, distanceMinutes));
            }
        }

        if (candidates.Count == 0) return null;
        var timed = candidates.Where(candidate => candidate.DistanceMinutes != double.MaxValue)
            .OrderBy(candidate => candidate.DistanceMinutes)
            .ThenBy(candidate => candidate.Load.Reference)
            .ThenBy(candidate => candidate.Sequence)
            .ToList();
        if (timed.Count > 0)
        {
            var best = timed[0];
            // Several order lines can legitimately share one collection visit on the
            // same run. That is not ambiguous: choose the first operational line and
            // the normal same-site completion projection will carry the evidence.
            var competingRun = timed.Skip(1).FirstOrDefault(candidate => candidate.Load.Id != best.Load.Id);
            if (competingRun is not null && competingRun.DistanceMinutes - best.DistanceMinutes < AmbiguityToleranceMinutes) return null;
            return new VehiclePreloadMatch(best.Load, best.Stop, best.Sequence);
        }

        // Untimed duplicates on the same run are also one physical collection. More
        // than one run for the vehicle remains ambiguous and is deliberately not guessed.
        var runIds = candidates.Select(candidate => candidate.Load.Id).Distinct().ToList();
        if (runIds.Count != 1) return null;
        var only = candidates.OrderBy(candidate => candidate.Sequence).First();
        return new VehiclePreloadMatch(only.Load, only.Stop, only.Sequence);
    }

    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) HistoryWindow(DateOnly planningDate)
    {
        var (start, end) = OperatingWindow(planningDate);
        return (start.AddHours(-HistoryLookbackHours), end.AddHours(HistoryCarryForwardHours));
    }

    private static Load CloneForOperatingDay(Load source)
    {
        var clone = new Load
        {
            Id = source.Id,
            Reference = source.Reference,
            PlanningDate = source.PlanningDate,
            Status = source.Status,
            VehicleId = source.VehicleId,
            DriverId = source.DriverId,
            TrailerId = source.TrailerId,
            CreatedAtUtc = source.CreatedAtUtc,
            Stops = (source.Stops ?? []).Select(stop => new LoadStop
            {
                Id = stop.Id,
                LoadId = stop.LoadId,
                OrderId = stop.OrderId,
                Sequence = stop.Sequence,
                Name = stop.Name,
                Address = stop.Address,
                Latitude = stop.Latitude,
                Longitude = stop.Longitude,
                PlannedArrivalUtc = stop.PlannedArrivalUtc
            }).ToList()
        };
        OvernightRunContinuity.Apply(clone);
        return clone;
    }

    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) OperatingWindow(DateOnly date)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var localStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var localEnd = date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return (
                new DateTimeOffset(localStart, zone.GetUtcOffset(localStart)).ToUniversalTime(),
                new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd)).ToUniversalTime());
        }
        catch (TimeZoneNotFoundException)
        {
            return (
                new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
                new DateTimeOffset(date.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        }
    }

    private sealed record Candidate(Load Load, LoadStop Stop, int Sequence, double DistanceMinutes);
}

public sealed record VehiclePreloadMatch(Load Load, LoadStop Stop, int Sequence);
