using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record BetaOptimiserStopDto(int Sequence, string Name, Guid? OrderId);

public sealed record BetaOptimiserRouteDto(
    Guid LoadId,
    string Reference,
    string Status,
    bool IsProtected,
    string? Driver,
    int? TachoDriveAvailableMinutes,
    DateTimeOffset? LastTachoSyncUtc,
    string? Vehicle,
    string? FleetioStatus,
    string? Trailer,
    int StopCount,
    bool RoutingAvailable,
    string RoutingSource,
    decimal? CurrentMiles,
    int? CurrentDriveMinutes,
    decimal? ProposedMiles,
    int? ProposedDriveMinutes,
    decimal? SavingMiles,
    int? SavingDriveMinutes,
    IReadOnlyList<BetaOptimiserStopDto> Before,
    IReadOnlyList<BetaOptimiserStopDto> After,
    string Rationale,
    IReadOnlyList<string> Warnings);

public sealed record BetaOptimiserDayDto(
    DateOnly PlanningDate,
    DateTimeOffset AnalysedAtUtc,
    string RoutingPolicy,
    int RunCount,
    int RoutedRunCount,
    int UnroutedRunCount,
    decimal CurrentMiles,
    int CurrentDriveMinutes,
    decimal ProjectedMiles,
    int ProjectedDriveMinutes,
    decimal SavingMiles,
    int SavingDriveMinutes,
    IReadOnlyList<BetaOptimiserRouteDto> Routes,
    IReadOnlyList<string> Warnings);

public sealed record BetaPlannerRouteComparisonDto(
    string Reference,
    int StopCount,
    bool RoutingAvailable,
    decimal? CurrentMiles,
    int? CurrentDriveMinutes,
    decimal? ProposedMiles,
    int? ProposedDriveMinutes,
    decimal? SavingMiles,
    int? SavingDriveMinutes,
    IReadOnlyList<string> Before,
    IReadOnlyList<string> After,
    string Rationale,
    IReadOnlyList<string> Warnings);

public sealed record BetaPlannerComparisonDto(
    DateOnly PlanningDate,
    DateTimeOffset AnalysedAtUtc,
    int RouteCount,
    int RoutedRouteCount,
    decimal CurrentMiles,
    int CurrentDriveMinutes,
    decimal ProjectedMiles,
    int ProjectedDriveMinutes,
    decimal SavingMiles,
    int SavingDriveMinutes,
    IReadOnlyList<BetaPlannerRouteComparisonDto> Routes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Builds a read-only optimisation view over the operational Load model used by
/// Planner/Dispatch today. No run, order or resource allocation is changed.
/// </summary>
public sealed class BetaOptimiserService(
    TmsDbContext db,
    BetaRouteOptimisationEngine engine,
    ILogger<BetaOptimiserService> logger)
{
    public async Task<BetaOptimiserDayDto> AnalyseDayAsync(DateOnly planningDate, CancellationToken ct)
    {
        var loads = await db.Loads.AsNoTracking()
            .Include(load => load.Stops)
            .Where(load => load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToListAsync(ct);

        var driverIds = loads.Where(load => load.DriverId.HasValue).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(load => load.VehicleId.HasValue).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(load => load.TrailerId.HasValue).Select(load => load.TrailerId!.Value).Distinct().ToList();

        var drivers = driverIds.Count == 0
            ? []
            : await db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var vehicles = vehicleIds.Count == 0
            ? []
            : await db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)).ToListAsync(ct);
        var trailers = trailerIds.Count == 0
            ? []
            : await db.Trailers.AsNoTracking().Where(trailer => trailerIds.Contains(trailer.Id)).ToListAsync(ct);
        var sites = await LoadSitesAsync(ct);

        var routeResults = new List<BetaOptimiserRouteDto>();
        foreach (var load in loads)
        {
            ct.ThrowIfCancellationRequested();
            routeResults.Add(await AnalyseLoadAsync(load, drivers, vehicles, trailers, sites, ct));
        }

        var totals = Totals(routeResults.Select(route => (
            route.RoutingAvailable,
            route.CurrentMiles,
            route.CurrentDriveMinutes,
            route.ProposedMiles,
            route.ProposedDriveMinutes)));
        var dayWarnings = new List<string>();
        if (loads.Count == 0)
            dayWarnings.Add("No non-cancelled runs were found for this planning date.");
        if (routeResults.Any(route => !route.RoutingAvailable))
            dayWarnings.Add("One or more runs could not be scored because live Azure Maps HGV evidence or mapped stop coordinates were unavailable.");
        if (routeResults.Any(route => route.IsProtected))
            dayWarnings.Add("Dispatched, in-progress and completed runs are measured for comparison but are never resequenced by Beta Optimiser.");

        return new BetaOptimiserDayDto(
            planningDate,
            DateTimeOffset.UtcNow,
            "Azure Maps HGV/truck routing only; resilient Haversine estimates are rejected for optimisation decisions.",
            routeResults.Count,
            totals.Routed,
            routeResults.Count - totals.Routed,
            totals.CurrentMiles,
            totals.CurrentMinutes,
            totals.ProjectedMiles,
            totals.ProjectedMinutes,
            totals.CurrentMiles - totals.ProjectedMiles,
            totals.CurrentMinutes - totals.ProjectedMinutes,
            routeResults,
            dayWarnings);
    }

    public async Task<BetaPlannerComparisonDto> AnalysePlannerRoutesAsync(
        BetaPlannerComparisonRequest request,
        CancellationToken ct)
    {
        var sites = await LoadSitesAsync(ct);
        var results = new List<BetaPlannerRouteComparisonDto>();

        foreach (var route in request.Routes.Where(route => !string.IsNullOrWhiteSpace(route.Reference)))
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await AnalysePlannerRouteAsync(route, sites, ct));
        }

        var totals = Totals(results.Select(route => (
            route.RoutingAvailable,
            route.CurrentMiles,
            route.CurrentDriveMinutes,
            route.ProposedMiles,
            route.ProposedDriveMinutes)));
        var warnings = new List<string>();
        if (results.Count == 0) warnings.Add("No planner routes were found in the uploaded file.");
        if (results.Any(route => !route.RoutingAvailable))
            warnings.Add("Some uploaded planner routes contain sites that could not be mapped or could not obtain live Azure Maps HGV routing evidence.");

        return new BetaPlannerComparisonDto(
            request.PlanningDate,
            DateTimeOffset.UtcNow,
            results.Count,
            totals.Routed,
            totals.CurrentMiles,
            totals.CurrentMinutes,
            totals.ProjectedMiles,
            totals.ProjectedMinutes,
            totals.CurrentMiles - totals.ProjectedMiles,
            totals.CurrentMinutes - totals.ProjectedMinutes,
            results,
            warnings);
    }

    private async Task<BetaOptimiserRouteDto> AnalyseLoadAsync(
        Load load,
        IReadOnlyList<Driver> drivers,
        IReadOnlyList<Vehicle> vehicles,
        IReadOnlyList<Trailer> trailers,
        IReadOnlyList<Site> sites,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var orderedStops = load.Stops.OrderBy(stop => stop.Sequence).ToList();
        var mappedStops = new List<BetaRouteInputStop>();

        foreach (var stop in orderedStops)
        {
            var coordinate = ResolveCoordinate(stop.Name, stop.Latitude, stop.Longitude, sites);
            if (coordinate is null)
            {
                warnings.Add($"{stop.Name}: no mapped coordinate is available in the live stop or Site Master.");
                continue;
            }
            mappedStops.Add(new BetaRouteInputStop(
                stop.Id,
                stop.OrderId,
                stop.Name,
                coordinate.Value.Latitude,
                coordinate.Value.Longitude));
        }

        var isProtected = load.Status is LoadStatus.Dispatched or LoadStatus.InProgress or LoadStatus.Completed;
        BetaRouteAnalysis analysis;
        if (mappedStops.Count != orderedStops.Count)
        {
            analysis = new BetaRouteAnalysis(
                load.Id,
                load.Reference,
                isProtected,
                false,
                "Unavailable",
                null,
                null,
                null,
                null,
                null,
                null,
                mappedStops,
                mappedStops,
                "The complete route cannot be scored until every stop has a mapped coordinate. No partial-route saving is presented because that would be misleading.");
        }
        else
        {
            analysis = await engine.AnalyseAsync(
                new BetaRouteInput(load.Id, load.Reference, isProtected, mappedStops),
                ct);
        }

        var driver = load.DriverId.HasValue ? drivers.FirstOrDefault(item => item.Id == load.DriverId.Value) : null;
        var vehicle = load.VehicleId.HasValue ? vehicles.FirstOrDefault(item => item.Id == load.VehicleId.Value) : null;
        var trailer = load.TrailerId.HasValue ? trailers.FirstOrDefault(item => item.Id == load.TrailerId.Value) : null;

        if (driver is not null && driver.TachoDriveAvailableTodayMinutes is null)
            warnings.Add("TachoMaster drive-time availability is not currently present for the allocated driver.");
        if (vehicle is not null && !string.IsNullOrWhiteSpace(vehicle.FleetioStatus) &&
            !vehicle.FleetioStatus.Contains("active", StringComparison.OrdinalIgnoreCase) &&
            !vehicle.FleetioStatus.Contains("in service", StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Fleetio vehicle status is '{vehicle.FleetioStatus}'.");

        return new BetaOptimiserRouteDto(
            load.Id,
            load.Reference,
            load.Status.ToString(),
            isProtected,
            driver?.DisplayName,
            driver?.TachoDriveAvailableTodayMinutes,
            driver?.LastTachoSyncUtc,
            vehicle?.Registration,
            vehicle?.FleetioStatus,
            trailer?.TrailerNumber,
            orderedStops.Count,
            analysis.RoutingAvailable,
            analysis.RoutingSource,
            analysis.CurrentMiles,
            analysis.CurrentDriveMinutes,
            analysis.ProposedMiles,
            analysis.ProposedDriveMinutes,
            analysis.SavingMiles,
            analysis.SavingDriveMinutes,
            orderedStops.Select(stop => new BetaOptimiserStopDto(stop.Sequence, stop.Name, stop.OrderId)).ToList(),
            BuildDisplayStops(analysis.After),
            analysis.Rationale,
            warnings);
    }

    private async Task<BetaPlannerRouteComparisonDto> AnalysePlannerRouteAsync(
        BetaPlannerRouteRequest route,
        IReadOnlyList<Site> sites,
        CancellationToken ct)
    {
        var warnings = new List<string>();
        var orderIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<BetaRouteInputStop>();

        foreach (var stop in route.Stops.Where(stop => !string.IsNullOrWhiteSpace(stop.Name)))
        {
            var site = FindSite(stop.Name, sites);
            if (site?.Latitude is null || site.Longitude is null)
            {
                warnings.Add($"{stop.Name}: site could not be mapped to Site Master coordinates.");
                continue;
            }

            Guid? orderId = null;
            if (!string.IsNullOrWhiteSpace(stop.OrderKey))
            {
                if (!orderIds.TryGetValue(stop.OrderKey, out var mappedOrderId))
                {
                    mappedOrderId = Guid.NewGuid();
                    orderIds[stop.OrderKey] = mappedOrderId;
                }
                orderId = mappedOrderId;
            }

            mapped.Add(new BetaRouteInputStop(Guid.NewGuid(), orderId, stop.Name.Trim(), site.Latitude.Value, site.Longitude.Value));
        }

        if (mapped.Count != route.Stops.Count)
        {
            return new BetaPlannerRouteComparisonDto(
                route.Reference,
                route.Stops.Count,
                false,
                null,
                null,
                null,
                null,
                null,
                null,
                route.Stops.Select(stop => stop.Name).ToList(),
                route.Stops.Select(stop => stop.Name).ToList(),
                "The uploaded route is not scored until every stop maps to Site Master. Partial mileage would give a false comparison.",
                warnings);
        }

        var analysis = await engine.AnalyseAsync(new BetaRouteInput(Guid.NewGuid(), route.Reference, false, mapped), ct);
        return new BetaPlannerRouteComparisonDto(
            route.Reference,
            route.Stops.Count,
            analysis.RoutingAvailable,
            analysis.CurrentMiles,
            analysis.CurrentDriveMinutes,
            analysis.ProposedMiles,
            analysis.ProposedDriveMinutes,
            analysis.SavingMiles,
            analysis.SavingDriveMinutes,
            analysis.Before.Select(stop => stop.Name).ToList(),
            analysis.After.Select(stop => stop.Name).ToList(),
            analysis.Rationale,
            warnings);
    }

    private async Task<List<Site>> LoadSitesAsync(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        return sites;
    }

    private static IReadOnlyList<BetaOptimiserStopDto> BuildDisplayStops(IReadOnlyList<BetaRouteInputStop> stops) =>
        stops.Select((stop, index) => new BetaOptimiserStopDto(index + 1, stop.Name, stop.OrderId)).ToList();

    private static (decimal Latitude, decimal Longitude)? ResolveCoordinate(
        string name,
        decimal? latitude,
        decimal? longitude,
        IReadOnlyList<Site> sites)
    {
        if (latitude.HasValue && longitude.HasValue)
            return (latitude.Value, longitude.Value);
        var site = FindSite(name, sites);
        return site?.Latitude is not null && site.Longitude is not null
            ? (site.Latitude.Value, site.Longitude.Value)
            : null;
    }

    private static Site? FindSite(string value, IReadOnlyList<Site> sites)
    {
        var key = Normalise(value);
        if (string.IsNullOrWhiteSpace(key)) return null;

        return sites.FirstOrDefault(site =>
            Normalise(site.Name) == key ||
            Normalise(site.DriverTextName) == key ||
            Normalise(site.ExternalCode) == key ||
            AliasKeys(site.Aliases).Contains(key, StringComparer.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> AliasKeys(string? aliases) =>
        string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split(new[] { ',', ';', '|', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalise)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();

    private static string Normalise(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static (int Routed, decimal CurrentMiles, int CurrentMinutes, decimal ProjectedMiles, int ProjectedMinutes) Totals(
        IEnumerable<(bool RoutingAvailable, decimal? CurrentMiles, int? CurrentMinutes, decimal? ProposedMiles, int? ProposedMinutes)> rows)
    {
        var routed = 0;
        var currentMiles = 0m;
        var currentMinutes = 0;
        var projectedMiles = 0m;
        var projectedMinutes = 0;

        foreach (var row in rows)
        {
            if (!row.RoutingAvailable || row.CurrentMiles is null || row.CurrentMinutes is null) continue;
            routed++;
            currentMiles += row.CurrentMiles.Value;
            currentMinutes += row.CurrentMinutes.Value;
            projectedMiles += row.ProposedMiles ?? row.CurrentMiles.Value;
            projectedMinutes += row.ProposedMinutes ?? row.CurrentMinutes.Value;
        }

        return (
            routed,
            Math.Round(currentMiles, 2),
            currentMinutes,
            Math.Round(projectedMiles, 2),
            projectedMinutes);
    }
}
