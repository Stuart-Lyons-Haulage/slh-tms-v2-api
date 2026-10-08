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

        var vehicleContext = sameVehicle ? "The same vehicle is allocated to the next duty" : "Vehicle continuity is not confirmed";
        return new("Possible Night Out", restMinutes,
            $"The rest interval is long enough to review, but this system no longer infers overnight location from boundaries around depots or other sites. {vehicleContext}; planner confirmation is required.");
    }

    private static bool IsMovement(DotTelemetryRecord item) => item.IsMoving == true || (item.SpeedKph ?? 0m) > 0m;

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

}

public sealed record MovementWindow(DateTimeOffset? FirstUtc, DateTimeOffset? LastUtc, IReadOnlyList<string> VehicleIdentifiers);
public sealed record NightOutAssessment(string Status, int? RestMinutes, string Reason);
