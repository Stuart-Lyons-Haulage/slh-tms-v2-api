using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Driver suitability inputs for Dispatch: how a driver works (tramper, day or night) and
/// how much far-away work they have had recently, so long runs are rotated fairly.
/// </summary>
public static class DriverWorkloadFairness
{
    // SLH yard, Barnham. Straight-line miles from here is a fair, cheap proxy for "how far".
    public const double BaseLatitude = 50.8312;
    public const double BaseLongitude = -0.6375;
    public const int LongRunMiles = 120;

    public sealed record Summary(int Runs, int LongRuns, int FurthestMilesTotal);

    public static string? WorkingStyle(string? driverType, string? driverGroup)
    {
        var text = $"{driverType} {driverGroup}".ToLowerInvariant();
        if (text.Contains("half tramp") || text.Contains("halftramp")) return "HalfTramper";
        if (text.Contains("tramp")) return "Tramper";
        if (text.Contains("night")) return "Night";
        if (text.Contains("day")) return "Day";
        return null;
    }

    public static int FurthestMilesFromBase(Load load)
    {
        var furthest = 0.0;
        foreach (var stop in load.Stops)
        {
            if (stop.Latitude is not decimal lat || stop.Longitude is not decimal lon) continue;
            furthest = Math.Max(furthest, Miles(BaseLatitude, BaseLongitude, (double)lat, (double)lon));
        }
        return (int)Math.Round(furthest);
    }

    public static Dictionary<Guid, Summary> Summarise(IEnumerable<Load> history, DateOnly planningDate)
    {
        var since = planningDate.AddDays(-14);
        return history
            .Where(load => load.DriverId is not null && load.PlanningDate >= since && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
            .GroupBy(load => load.DriverId!.Value)
            .ToDictionary(group => group.Key, group =>
            {
                var miles = group.Select(FurthestMilesFromBase).ToList();
                return new Summary(group.Count(), miles.Count(value => value >= LongRunMiles), miles.Sum());
            });
    }

    public static double Miles(double lat1, double lon1, double lat2, double lon2)
    {
        static double Radians(double value) => value * Math.PI / 180;
        var dLat = Radians(lat2 - lat1);
        var dLon = Radians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Radians(lat1)) * Math.Cos(Radians(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 3958.8 * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
