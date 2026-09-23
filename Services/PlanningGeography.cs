namespace Slh.Tms.Api.Services;

public sealed record PlanningGeographyPoint(double Latitude, double Longitude);

public sealed record PlanningGeographyRoute(
    string CollectionKey,
    string DeliveryKey,
    PlanningGeographyPoint? Collection,
    PlanningGeographyPoint? Delivery);

public static class PlanningGeography
{
    public const double CollectionRadiusMiles = 40d;
    public const double DeliveryRadiusMiles = 60d;
    public const double MinimumDirectionCosine = 0.55d;

    public static bool Compatible(PlanningGeographyRoute left, PlanningGeographyRoute right)
    {
        if (left.Collection is null || left.Delivery is null || right.Collection is null || right.Delivery is null)
            return string.Equals(left.CollectionKey, right.CollectionKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.DeliveryKey, right.DeliveryKey, StringComparison.OrdinalIgnoreCase);

        var collectionDistance = HaversineMiles(left.Collection, right.Collection);
        var deliveryDistance = HaversineMiles(left.Delivery, right.Delivery);
        if (collectionDistance > CollectionRadiusMiles || deliveryDistance > DeliveryRadiusMiles)
            return false;

        var leftDirection = DirectionVector(left.Collection, left.Delivery);
        var rightDirection = DirectionVector(right.Collection, right.Delivery);
        return Cosine(leftDirection, rightDirection) >= MinimumDirectionCosine;
    }

    public static double HaversineMiles(PlanningGeographyPoint left, PlanningGeographyPoint right)
    {
        const double earthRadiusMiles = 3958.7613;
        var lat1 = DegreesToRadians(left.Latitude);
        var lat2 = DegreesToRadians(right.Latitude);
        var dLat = lat2 - lat1;
        var dLon = DegreesToRadians(right.Longitude - left.Longitude);
        var a = Math.Pow(Math.Sin(dLat / 2d), 2d) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Pow(Math.Sin(dLon / 2d), 2d);
        return earthRadiusMiles * 2d * Math.Atan2(Math.Sqrt(a), Math.Sqrt(Math.Max(0d, 1d - a)));
    }

    public static int EstimateDriveMinutes(PlanningGeographyPoint? collection, PlanningGeographyPoint? delivery)
    {
        if (collection is null || delivery is null) return 240;
        var miles = HaversineMiles(collection, delivery);
        return Math.Max(60, (int)Math.Ceiling(miles / 45d * 60d) + 30);
    }

    private static (double X, double Y) DirectionVector(PlanningGeographyPoint from, PlanningGeographyPoint to)
    {
        var meanLatitude = DegreesToRadians((from.Latitude + to.Latitude) / 2d);
        return (
            (to.Longitude - from.Longitude) * Math.Cos(meanLatitude),
            to.Latitude - from.Latitude);
    }

    private static double Cosine((double X, double Y) left, (double X, double Y) right)
    {
        var denominator = Math.Sqrt(left.X * left.X + left.Y * left.Y) * Math.Sqrt(right.X * right.X + right.Y * right.Y);
        if (denominator <= 0.000001d) return 1d;
        return (left.X * right.X + left.Y * right.Y) / denominator;
    }

    private static double DegreesToRadians(double value) => value * Math.PI / 180d;
}
