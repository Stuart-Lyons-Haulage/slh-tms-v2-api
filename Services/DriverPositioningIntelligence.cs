namespace Slh.Tms.Api.Services;

public readonly record struct PositionPoint(double Latitude, double Longitude);

public sealed record DriverPositioningScore(
    decimal TotalScore,
    bool IsHomewardBackload,
    PositionPoint? EffectiveStart,
    string PositionSource,
    IReadOnlyList<string> Reasons);

/// <summary>
/// Shared, advisory positioning intelligence used by optimiser/dispatch ranking.
/// It deliberately does not make Tacho, availability or legality decisions; those remain hard constraints upstream.
/// </summary>
public static class DriverPositioningIntelligence
{
    private const double HomewardMinimumMiles = 40d;
    private const int HomewardDutyDayThreshold = 4;

    public static DriverPositioningScore Score(
        int consecutiveDutyDays,
        PositionPoint? livePosition,
        PositionPoint? previousFinish,
        PositionPoint? candidateStart,
        PositionPoint? candidateEnd,
        PositionPoint homeBase)
    {
        var reasons = new List<string>();
        decimal score = 0m;

        var effectiveStart = livePosition ?? previousFinish;
        var source = livePosition is not null ? "LiveTracking" : previousFinish is not null ? "PreviousRunEnd" : "Unavailable";

        if (effectiveStart is PositionPoint start && candidateStart is PositionPoint jobStart)
        {
            var repositionMiles = Miles(start, jobStart);
            var proximityScore = Math.Max(-25m, 18m - (decimal)(repositionMiles / 12d));
            score += proximityScore;
            reasons.Add(livePosition is not null
                ? $"Fresh live tracking is {repositionMiles:0} mi from the candidate start."
                : $"Previous run finish is {repositionMiles:0} mi from the candidate start.");
        }
        else
        {
            score -= 8m;
            reasons.Add("No reliable current/previous finish position is available for reposition scoring.");
        }

        var homeward = false;
        if (consecutiveDutyDays >= HomewardDutyDayThreshold &&
            effectiveStart is PositionPoint awayStart &&
            candidateEnd is PositionPoint jobEnd)
        {
            var before = Miles(awayStart, homeBase);
            var after = Miles(jobEnd, homeBase);
            var improvement = before - after;
            if (improvement >= HomewardMinimumMiles)
            {
                var dutyWeight = consecutiveDutyDays >= 5 ? 24m : 14m;
                var progressWeight = Math.Min(18m, (decimal)(improvement / 20d));
                score += dutyWeight + progressWeight;
                homeward = true;
                reasons.Add($"Day {consecutiveDutyDays}: this work moves the driver about {improvement:0} mi closer to the Chichester home base, so it is preferred as a homeward backload.");
            }
            else
            {
                reasons.Add($"Day {consecutiveDutyDays}: the route does not make meaningful progress toward the Chichester home base.");
            }
        }

        return new DriverPositioningScore(Math.Round(score, 2), homeward, effectiveStart, source, reasons);
    }

    private static double Miles(PositionPoint left, PositionPoint right)
    {
        const double earthRadiusMiles = 3958.7613d;
        static double Radians(double degrees) => degrees * Math.PI / 180d;

        var lat1 = Radians(left.Latitude);
        var lat2 = Radians(right.Latitude);
        var deltaLat = Radians(right.Latitude - left.Latitude);
        var deltaLon = Radians(right.Longitude - left.Longitude);
        var a = Math.Sin(deltaLat / 2d) * Math.Sin(deltaLat / 2d) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(deltaLon / 2d) * Math.Sin(deltaLon / 2d);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return earthRadiusMiles * c;
    }
}
