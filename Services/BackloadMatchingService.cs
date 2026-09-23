using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Options;

namespace Slh.Tms.Api.Services;

public sealed class BackloadMatchingOptions
{
    public decimal SouthboundLatitudeThresholdDegrees { get; set; } = 0.3m;
    public decimal MaximumCollectionDetourMiles { get; set; } = 40m;
    public decimal BarnhamLatitude { get; set; } = 50.8414m;
    public decimal BarnhamLongitude { get; set; } = -0.5667m;
    public decimal ScheduledFinishDistanceMiles { get; set; } = 50m;
}

public sealed record BackloadCandidate(
    Guid OrderId,
    string OrderReference,
    string CollectionPointName,
    MatrixPoint CollectionPoint,
    string DeliveryPointName,
    MatrixPoint DeliveryPoint,
    int PalletCount);

public sealed record BackloadMatch(
    Guid OrderId,
    string OrderReference,
    string CollectionPointName,
    MatrixPoint CollectionPoint,
    string DeliveryPointName,
    MatrixPoint DeliveryPoint,
    int PalletCount,
    decimal EstimatedCollectionDetourMiles,
    int EstimatedTimeToCollectionMinutes,
    decimal BackloadScore,
    string Reason);

public interface IBackloadMatchingService
{
    Task<IReadOnlyList<BackloadMatch>> FindMatchesAsync(
        MatrixPoint currentPosition,
        int remainingPalletCapacity,
        IReadOnlyList<BackloadCandidate> candidates,
        HgvVehicleProfile profile,
        CancellationToken ct);
}

public sealed class BackloadMatchingService(
    IAzureMapsMatrixService matrixService,
    IOptions<BackloadMatchingOptions> options,
    TelemetryClient telemetry) : IBackloadMatchingService
{
    private const decimal MetresPerMile = 1609.344m;
    private readonly BackloadMatchingOptions _options = options.Value;

    public async Task<IReadOnlyList<BackloadMatch>> FindMatchesAsync(
        MatrixPoint currentPosition,
        int remainingPalletCapacity,
        IReadOnlyList<BackloadCandidate> candidates,
        HgvVehicleProfile profile,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(profile);
        currentPosition.Validate();
        if (remainingPalletCapacity < 0) throw new ArgumentOutOfRangeException(nameof(remainingPalletCapacity));
        if (_options.MaximumCollectionDetourMiles <= 0) throw new InvalidOperationException("Backload maximum collection detour must be greater than zero.");

        // Step 1a: only genuinely southbound work is worth asking Azure Maps to evaluate.
        var southbound = candidates
            .Where(candidate => candidate.DeliveryPoint.Latitude < candidate.CollectionPoint.Latitude - _options.SouthboundLatitudeThresholdDegrees)
            .ToList();
        if (southbound.Count == 0) return [];

        var matrix = await matrixService.ComputeMatrixAsync(
            [currentPosition],
            southbound.Select(candidate => candidate.CollectionPoint).ToArray(),
            profile,
            ct);

        var maximumDetourMetres = _options.MaximumCollectionDetourMiles * MetresPerMile;
        var matches = new List<BackloadMatch>();

        for (var index = 0; index < southbound.Count; index++)
        {
            var route = matrix[0, index];
            if (route is null || route.DistanceMetres > maximumDetourMetres) continue;

            var candidate = southbound[index];
            var detourMiles = route.DistanceMetres / MetresPerMile;
            var distanceFactor = Clamp100(100m - (detourMiles / _options.MaximumCollectionDetourMiles * 100m));

            var totalLatitudeToBarnham = currentPosition.Latitude - _options.BarnhamLatitude;
            var southboundFactor = totalLatitudeToBarnham <= 0
                ? 0m
                : Clamp100(((currentPosition.Latitude - candidate.DeliveryPoint.Latitude) / totalLatitudeToBarnham) * 100m);

            var capacityFactor = remainingPalletCapacity <= 0
                ? 0m
                : Clamp100(((decimal)candidate.PalletCount / remainingPalletCapacity) * 100m);

            var timeFactor = Clamp100(100m - ((decimal)route.TravelTimeMinutes / 120m * 100m));
            var score = Math.Round(
                distanceFactor * 0.35m +
                southboundFactor * 0.35m +
                capacityFactor * 0.20m +
                timeFactor * 0.10m,
                1,
                MidpointRounding.AwayFromZero);

            var deadheadReduction = Math.Max(0m,
                HaversineMiles(currentPosition, new MatrixPoint(_options.BarnhamLatitude, _options.BarnhamLongitude)) -
                HaversineMiles(candidate.DeliveryPoint, new MatrixPoint(_options.BarnhamLatitude, _options.BarnhamLongitude)));
            var roundedDetour = Math.Round(detourMiles, 0, MidpointRounding.AwayFromZero);
            var roundedSaving = Math.Round(deadheadReduction, 0, MidpointRounding.AwayFromZero);
            var reason = string.Create(CultureInfo.InvariantCulture,
                $"Collection {roundedDetour:0}mi from current position — delivers {candidate.DeliveryPointName}, reduces deadhead by approximately {roundedSaving:0}mi, {candidate.PalletCount} pallets");

            matches.Add(new BackloadMatch(
                candidate.OrderId,
                candidate.OrderReference,
                candidate.CollectionPointName,
                candidate.CollectionPoint,
                candidate.DeliveryPointName,
                candidate.DeliveryPoint,
                candidate.PalletCount,
                Math.Round(detourMiles, 1, MidpointRounding.AwayFromZero),
                route.TravelTimeMinutes,
                score,
                reason));
        }

        var ranked = matches
            .OrderByDescending(match => match.BackloadScore)
            .ThenBy(match => match.EstimatedCollectionDetourMiles)
            .ThenBy(match => match.OrderReference, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        foreach (var match in ranked)
        {
            telemetry.TrackEvent("BackloadMatchFound", new Dictionary<string, string>
            {
                ["orderReference"] = match.OrderReference,
                ["collection"] = match.CollectionPointName,
                ["delivery"] = match.DeliveryPointName
            });
            telemetry.TrackMetric("BackloadScoreDistribution", (double)match.BackloadScore);
        }

        return ranked;
    }

    internal static decimal HaversineMiles(MatrixPoint from, MatrixPoint to)
    {
        const double earthRadiusMiles = 3958.7613;
        var lat1 = DegreesToRadians((double)from.Latitude);
        var lat2 = DegreesToRadians((double)to.Latitude);
        var dLat = DegreesToRadians((double)(to.Latitude - from.Latitude));
        var dLon = DegreesToRadians((double)(to.Longitude - from.Longitude));
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return (decimal)(earthRadiusMiles * c);
    }

    private static decimal Clamp100(decimal value) => Math.Clamp(value, 0m, 100m);
    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
}
