using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Small, deterministic rules used by the timesheet projection. Keeping these rules
/// independent of the provider clients makes the payroll evidence decisions testable.
/// </summary>
public static class TimesheetEvidenceRules
{
    public static MovementWindow SelectMovementWindow(
        IEnumerable<DotTelemetryRecord> events,
        IEnumerable<string> vehicleAliases,
        DateTimeOffset dutyStartUtc,
        DateTimeOffset? dutyEndUtc,
        TimeSpan openDutyMaximum = default)
    {
        var aliases = vehicleAliases
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(Normalise)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var end = dutyEndUtc ?? dutyStartUtc.Add(openDutyMaximum == default ? TimeSpan.FromHours(20) : openDutyMaximum);
        if (end <= dutyStartUtc) end = dutyStartUtc.AddHours(20);

        var movement = events
            .Where(item => item.EventTimeUtc >= dutyStartUtc && item.EventTimeUtc <= end)
            .Where(item => aliases.Contains(Normalise(item.VehicleIdentifier)))
            .Where(IsMovement)
            .OrderBy(item => item.EventTimeUtc)
            .ToList();

        return new MovementWindow(
            movement.Count == 0 ? null : movement[0].EventTimeUtc,
            movement.Count == 0 ? null : movement[^1].EventTimeUtc,
            movement.Select(item => item.VehicleIdentifier).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static NightOutAssessment AssessNightOut(
        DateTimeOffset? dutyEndUtc,
        DateTimeOffset? nextDutyStartUtc,
        DateTimeOffset? lastMovementUtc,
        decimal? lastLatitude,
        decimal? lastLongitude,
        IEnumerable<DepotPoint> depots,
        bool sameVehicle)
    {
        if (dutyEndUtc is null || nextDutyStartUtc is null || nextDutyStartUtc <= dutyEndUtc)
            return new("No Night Out", null, "No consecutive completed duties were available.");

        var restMinutes = (int)Math.Round((nextDutyStartUtc.Value - dutyEndUtc.Value).TotalMinutes);
        if (restMinutes < 9 * 60)
            return new("No Night Out", restMinutes, "The rest interval was shorter than 9 hours.");

        // A gap beyond one operational day is normally weekly/full rest (for example
        // Friday to Monday), not an overnight allowance. Do not infer a night out from
        // a long weekend absence even when the final RoadTech point is away from depot.
        if (restMinutes > 24 * 60)
            return new("No Night Out", restMinutes, "The rest interval exceeded 24 hours and is treated as full/weekly rest, not a night out.");

        if (lastMovementUtc is null || lastLatitude is null || lastLongitude is null)
            return new("Possible Night Out", restMinutes, "The duty gap is long enough, but the final away-from-depot location is incomplete.");

        var awayFromDepot = depots.Any(depot => DistanceMetres(lastLatitude.Value, lastLongitude.Value, depot.Latitude, depot.Longitude) <= depot.RadiusMetres);
        if (awayFromDepot)
            return new("No Night Out", restMinutes, "The vehicle was at a depot/home geofence during the rest interval.");

        if (!sameVehicle)
            return new("Possible Night Out", restMinutes, "The driver resumed in a different vehicle; planner confirmation is required.");

        return restMinutes >= 11 * 60
            ? new("Confirmed Night Out - Regular Rest", restMinutes, "Vehicle remained away from the depot through a completed 11-hour rest interval.")
            : new("Confirmed Night Out - Reduced Rest", restMinutes, "Vehicle remained away from the depot through a completed reduced rest interval.");
    }

    private static bool IsMovement(DotTelemetryRecord item) => item.IsMoving == true || (item.SpeedKph ?? 0m) > 0m;

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static double DistanceMetres(decimal latitude, decimal longitude, decimal otherLatitude, decimal otherLongitude)
    {
        const double earthRadiusMetres = 6_371_000;
        var lat1 = (double)latitude * Math.PI / 180;
        var lat2 = (double)otherLatitude * Math.PI / 180;
        var dLat = lat2 - lat1;
        var dLon = ((double)otherLongitude - (double)longitude) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusMetres * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public sealed record MovementWindow(DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc, IReadOnlyList<string> VehicleIdentifiers);
public sealed record DepotPoint(decimal Latitude, decimal Longitude, int RadiusMetres = 500);
public sealed record NightOutAssessment(string Status, int? RestMinutes, string Reason);
