using System.Globalization;
using Microsoft.ApplicationInsights;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record FuelCalculation(decimal FuelLitres, decimal FuelCostPounds);

public static class FuelCostCalculator
{
    private const decimal LitresPerImperialGallon = 4.54609m;

    public static FuelCalculation Calculate(decimal distanceMiles, decimal milesPerImperialGallon, decimal pricePencePerLitre)
    {
        if (distanceMiles < 0) throw new ArgumentOutOfRangeException(nameof(distanceMiles));
        if (milesPerImperialGallon <= 0) throw new ArgumentOutOfRangeException(nameof(milesPerImperialGallon));
        if (pricePencePerLitre <= 0) throw new ArgumentOutOfRangeException(nameof(pricePencePerLitre));

        var gallons = distanceMiles / milesPerImperialGallon;
        var litres = gallons * LitresPerImperialGallon;
        var cost = litres * pricePencePerLitre / 100m;
        return new FuelCalculation(
            Math.Round(litres, 2, MidpointRounding.AwayFromZero),
            Math.Round(cost, 2, MidpointRounding.AwayFromZero));
    }
}

public sealed class FuelCostOptions
{
    /// <summary>
    /// Conservative planning default used only when no vehicle-specific override exists.
    /// Set Fuel:Costing:DefaultMilesPerImperialGallon in production when SLH has a preferred benchmark.
    /// </summary>
    public decimal DefaultMilesPerImperialGallon { get; set; } = 9m;

    /// <summary>
    /// Optional per-registration MPG overrides, e.g. Fuel:Costing:VehicleMpg:AB12CDE = 9.4.
    /// Registration matching ignores spaces and punctuation.
    /// </summary>
    public Dictionary<string, decimal> VehicleMpg { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Limits each matrix request to 20 sequential legs (400 matrix cells).</summary>
    public int MaxLegsPerMatrixBatch { get; set; } = 20;
}

public sealed record RunFuelCostEstimate(
    Guid RunId,
    string RunReference,
    DateOnly PlanningDate,
    Guid? VehicleId,
    string? VehicleRegistration,
    string? FuelProvider,
    decimal DistanceMiles,
    decimal MilesPerImperialGallon,
    decimal FuelLitres,
    decimal FuelPricePencePerLitre,
    string FuelPriceProvider,
    DateOnly FuelPriceWeekCommencing,
    decimal FuelCostPounds,
    decimal? EmptyMiles,
    decimal? EmptyMilesFuelCostPounds,
    string DistanceSource,
    string MpgSource);

public sealed record FuelOptimisationSummary(
    DateOnly PlanningDate,
    DateTimeOffset GeneratedAtUtc,
    int RunCount,
    decimal TotalDistanceMiles,
    decimal TotalFuelLitres,
    decimal TotalFuelCostPounds,
    decimal TotalEmptyMiles,
    decimal EstimatedEmptyMilesFuelCostPounds,
    IReadOnlyList<RunFuelCostEstimate> Runs);

public sealed class FuelOptimisationService(
    TmsDbContext db,
    IAzureMapsMatrixService matrixService,
    IOptions<HgvVehicleProfile> hgvProfile,
    IOptions<FuelCostOptions> options,
    TelemetryClient telemetry,
    TimeProvider timeProvider,
    ILogger<FuelOptimisationService> logger)
{
    private const decimal MetresPerMile = 1609.344m;
    private readonly FuelCostOptions _options = options.Value;

    public async Task<RunFuelCostEstimate?> EstimateRunAsync(Guid runId, CancellationToken ct)
    {
        // Prefer the audited planning register because operational/commercial values such as
        // EstimatedDistanceMiles and EmptyMiles are intentionally non-mapped on the SQL Load model.
        var load = await PlanningRegisterStore.GetLoadAsync(db, runId, ct)
            ?? await db.Loads.AsNoTracking().Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == runId, ct);
        if (load is null || load.Status == LoadStatus.Cancelled) return null;
        return await EstimateLoadAsync(load, ct);
    }

    public async Task<FuelOptimisationSummary> EstimatePlanningDateAsync(DateOnly planningDate, CancellationToken ct)
    {
        var registered = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
        var live = await db.Loads.AsNoTracking().Include(item => item.Stops)
            .Where(item => item.PlanningDate == planningDate)
            .ToListAsync(ct);
        var byId = new Dictionary<Guid, Load>();
        foreach (var load in live) byId[load.Id] = load;
        // Audited register values deliberately win when both stores contain the same logical run.
        foreach (var load in registered) byId[load.Id] = load;

        var estimates = new List<RunFuelCostEstimate>();
        foreach (var load in byId.Values
                     .Where(item => item.Status != LoadStatus.Cancelled)
                     .OrderBy(item => item.Reference, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var estimate = await EstimateLoadAsync(load, ct);
                if (estimate is not null) estimates.Add(estimate);
            }
            catch (MatrixTimeoutException exception)
            {
                logger.LogWarning(
                    "Fuel costing skipped run {RunReference}: Azure Maps matrix request {RequestId} timed out.",
                    load.Reference,
                    exception.RequestId);
            }
            catch (HttpRequestException exception)
            {
                logger.LogWarning(exception, "Fuel costing skipped run {RunReference}: HGV routing is unavailable.", load.Reference);
            }
        }

        var ordered = estimates
            .OrderByDescending(item => item.FuelCostPounds)
            .ThenBy(item => item.RunReference, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new FuelOptimisationSummary(
            planningDate,
            timeProvider.GetUtcNow(),
            ordered.Count,
            Math.Round(ordered.Sum(item => item.DistanceMiles), 1),
            Math.Round(ordered.Sum(item => item.FuelLitres), 1),
            Math.Round(ordered.Sum(item => item.FuelCostPounds), 2),
            Math.Round(ordered.Sum(item => item.EmptyMiles ?? 0m), 1),
            Math.Round(ordered.Sum(item => item.EmptyMilesFuelCostPounds ?? 0m), 2),
            ordered);
    }

    private async Task<RunFuelCostEstimate?> EstimateLoadAsync(Load load, CancellationToken ct)
    {
        Vehicle? vehicle = null;
        if (load.VehicleId is Guid vehicleId)
            vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == vehicleId, ct);

        var price = await ResolveFuelPriceAsync(load.PlanningDate, vehicle?.FuelProvider, ct);
        if (price is null)
        {
            logger.LogWarning("Fuel costing skipped run {RunReference}: no fuel price exists on or before {PlanningDate}.", load.Reference, load.PlanningDate);
            return null;
        }

        var (distanceMiles, distanceSource) = await ResolveDistanceMilesAsync(load, ct);
        if (distanceMiles is null) return null;

        var (mpg, mpgSource) = ResolveMpg(vehicle);
        var calculation = FuelCostCalculator.Calculate(distanceMiles.Value, mpg, price.PricePencePerLitre);
        decimal? emptyCost = null;
        if (load.EmptyMiles is decimal emptyMiles && emptyMiles >= 0)
            emptyCost = FuelCostCalculator.Calculate(emptyMiles, mpg, price.PricePencePerLitre).FuelCostPounds;

        var estimate = new RunFuelCostEstimate(
            load.Id,
            load.Reference,
            load.PlanningDate,
            load.VehicleId,
            vehicle?.Registration,
            vehicle?.FuelProvider,
            Math.Round(distanceMiles.Value, 1, MidpointRounding.AwayFromZero),
            mpg,
            calculation.FuelLitres,
            price.PricePencePerLitre,
            price.Provider,
            price.WeekCommencing,
            calculation.FuelCostPounds,
            load.EmptyMiles,
            emptyCost,
            distanceSource,
            mpgSource);

        telemetry.TrackMetric(
            "FuelCostPoundsPerRun",
            (double)estimate.FuelCostPounds,
            new Dictionary<string, string>
            {
                ["runId"] = estimate.RunId.ToString(),
                ["runReference"] = estimate.RunReference,
                ["planningDate"] = estimate.PlanningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["vehicleRegistration"] = estimate.VehicleRegistration ?? "unallocated",
                ["fuelProvider"] = estimate.FuelPriceProvider,
                ["distanceSource"] = estimate.DistanceSource,
                ["mpgSource"] = estimate.MpgSource
            });
        return estimate;
    }

    private async Task<(decimal? Miles, string Source)> ResolveDistanceMilesAsync(Load load, CancellationToken ct)
    {
        var mappedStops = load.Stops
            .OrderBy(stop => stop.Sequence)
            .Where(stop => stop.Latitude is not null && stop.Longitude is not null)
            .Select(stop => new MatrixPoint(stop.Latitude!.Value, stop.Longitude!.Value))
            .ToList();

        if (mappedStops.Count >= 2)
        {
            var legs = mappedStops.Zip(mappedStops.Skip(1), (origin, destination) => (origin, destination)).ToList();
            var maxBatch = Math.Clamp(_options.MaxLegsPerMatrixBatch, 1, 20);
            long totalMetres = 0;
            for (var offset = 0; offset < legs.Count; offset += maxBatch)
            {
                var batch = legs.Skip(offset).Take(maxBatch).ToList();
                var matrix = await matrixService.ComputeMatrixAsync(
                    batch.Select(leg => leg.origin).ToList(),
                    batch.Select(leg => leg.destination).ToList(),
                    hgvProfile.Value,
                    ct);
                for (var index = 0; index < batch.Count; index++)
                {
                    var cell = matrix[index, index];
                    if (cell is null)
                    {
                        logger.LogWarning(
                            "Fuel costing could not route leg {LegNumber} of run {RunReference}; falling back to stored run mileage if available.",
                            offset + index + 1,
                            load.Reference);
                        return load.EstimatedDistanceMiles is > 0
                            ? (load.EstimatedDistanceMiles.Value, "StoredRunMileageFallback")
                            : (null, "Unavailable");
                    }
                    totalMetres += cell.DistanceMetres;
                }
            }
            return ((decimal)totalMetres / MetresPerMile, "AzureMapsHgvMatrix");
        }

        if (load.EstimatedDistanceMiles is > 0)
            return (load.EstimatedDistanceMiles.Value, "StoredRunMileage");
        return (null, "Unavailable");
    }

    private (decimal Mpg, string Source) ResolveMpg(Vehicle? vehicle)
    {
        if (vehicle is not null)
        {
            var registrationKey = NormaliseRegistration(vehicle.Registration);
            var match = _options.VehicleMpg.FirstOrDefault(pair => NormaliseRegistration(pair.Key) == registrationKey);
            if (!string.IsNullOrWhiteSpace(match.Key) && match.Value > 0)
                return (match.Value, $"VehicleOverride:{vehicle.Registration}");
        }

        if (_options.DefaultMilesPerImperialGallon <= 0)
            throw new InvalidOperationException("Fuel costing DefaultMilesPerImperialGallon must be greater than zero.");
        return (_options.DefaultMilesPerImperialGallon, "ConfiguredDefault");
    }

    private async Task<FuelPrice?> ResolveFuelPriceAsync(DateOnly planningDate, string? vehicleFuelProvider, CancellationToken ct)
    {
        var candidates = await db.FuelPrices.AsNoTracking()
            .Where(item => item.WeekCommencing <= planningDate && item.PricePencePerLitre > 0)
            .OrderByDescending(item => item.WeekCommencing)
            .ThenByDescending(item => item.IsPricingMaximum)
            .ThenByDescending(item => item.PricePencePerLitre)
            .Take(100)
            .ToListAsync(ct);
        if (candidates.Count == 0) return null;

        var latestWeek = candidates.Max(item => item.WeekCommencing);
        var latest = candidates.Where(item => item.WeekCommencing == latestWeek).ToList();
        if (!string.IsNullOrWhiteSpace(vehicleFuelProvider))
        {
            var provider = latest.FirstOrDefault(item => string.Equals(item.Provider.Trim(), vehicleFuelProvider.Trim(), StringComparison.OrdinalIgnoreCase));
            if (provider is not null) return provider;
        }

        return latest
            .OrderByDescending(item => item.IsPricingMaximum)
            .ThenByDescending(item => item.PricePencePerLitre)
            .First();
    }

    private static string NormaliseRegistration(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
