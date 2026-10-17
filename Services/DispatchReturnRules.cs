namespace Slh.Tms.Api.Services;

public static class DispatchReturnRules
{
    private const double EarthRadiusMiles = 3958.7613d;

    public static bool NeedsReturn(int currentDutyDay, decimal? lastKnownLatitude, decimal northernLatitudeThreshold) =>
        currentDutyDay >= 4 && lastKnownLatitude is decimal latitude && latitude > northernLatitudeThreshold;

    public static decimal? DistanceMiles(decimal? fromLatitude, decimal? fromLongitude, decimal? toLatitude, decimal? toLongitude)
    {
        if (fromLatitude is not decimal fromLat || fromLongitude is not decimal fromLon ||
            toLatitude is not decimal toLat || toLongitude is not decimal toLon) return null;

        var lat1 = DegreesToRadians((double)fromLat);
        var lat2 = DegreesToRadians((double)toLat);
        var dLat = lat2 - lat1;
        var dLon = DegreesToRadians((double)(toLon - fromLon));
        var a = Math.Pow(Math.Sin(dLat / 2d), 2d) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2d), 2d);
        var c = 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1d - a));
        return Math.Round((decimal)(EarthRadiusMiles * c), 1);
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
