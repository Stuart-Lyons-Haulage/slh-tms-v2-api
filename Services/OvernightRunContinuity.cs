using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Applies SLH's operating-day convention to planner stop times for read/display purposes.
/// A run may start with an evening collection on the previous calendar day, take daily rest,
/// then continue after midnight on the planning/delivery day. Source spreadsheets often carry
/// only the operating date, so the leading PM block otherwise appears one day too late.
/// </summary>
public static class OvernightRunContinuity
{
    private static readonly TimeSpan EveningThreshold = TimeSpan.FromHours(15);

    public static bool Apply(Load load)
    {
        var timedStops = (load.Stops ?? [])
            .Where(stop => stop.PlannedArrivalUtc is not null)
            .OrderBy(stop => stop.Sequence)
            .ToList();
        if (timedStops.Count < 2) return false;

        var first = ToUk(timedStops[0].PlannedArrivalUtc!.Value);
        if (first.TimeOfDay < EveningThreshold) return false;

        // A later morning/daytime stop proves that this is a cross-midnight operating run,
        // rather than ordinary same-day PM work.
        if (!timedStops.Skip(1).Any(stop => ToUk(stop.PlannedArrivalUtc!.Value).TimeOfDay < EveningThreshold))
            return false;

        var changed = false;
        var crossedMidnight = false;
        foreach (var stop in timedStops)
        {
            var value = stop.PlannedArrivalUtc!.Value;
            var local = ToUk(value);
            if (local.TimeOfDay < EveningThreshold)
            {
                crossedMidnight = true;
                continue;
            }

            // Once the post-midnight block has begun, later same-day afternoon deliveries
            // remain on the planning date.
            if (crossedMidnight) continue;
            if (DateOnly.FromDateTime(local.DateTime) != load.PlanningDate) continue;

            stop.PlannedArrivalUtc = value.AddDays(-1);
            changed = true;
        }

        return changed;
    }

    public static bool IsCarryIn(Load load) => (load.Stops ?? [])
        .Where(stop => stop.PlannedArrivalUtc is not null)
        .Any(stop => DateOnly.FromDateTime(ToUk(stop.PlannedArrivalUtc!.Value).DateTime) < load.PlanningDate);

    private static DateTimeOffset ToUk(DateTimeOffset value)
    {
        try
        {
            var id = OperatingSystem.IsWindows() ? "GMT Standard Time" : "Europe/London";
            return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(id));
        }
        catch (TimeZoneNotFoundException)
        {
            return value;
        }
    }
}
