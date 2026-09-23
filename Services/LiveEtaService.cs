using Microsoft.ApplicationInsights;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Hubs;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class LiveEtaOptions
{
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumTrackingAge { get; set; } = TimeSpan.FromMinutes(15);
}

public sealed record LiveEtaSnapshot(
    Guid RunId,
    string RunReference,
    Guid StopId,
    Guid? OrderId,
    string StopName,
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset EtaUtc,
    int TravelTimeMinutes,
    long DistanceMetres,
    string Source,
    string Risk,
    DateTimeOffset TrackingUpdatedAtUtc);

public sealed class LiveEtaCalculator(
    TmsDbContext db,
    IAzureMapsMatrixService matrixService,
    IOptions<HgvVehicleProfile> defaultProfile,
    IOptions<LiveEtaOptions> options,
    IHubContext<EtaHub> etaHub,
    TelemetryClient telemetry,
    TimeProvider timeProvider,
    ILogger<LiveEtaCalculator> logger)
{
    private readonly LiveEtaOptions _options = options.Value;

    public async Task<IReadOnlyList<LiveEtaSnapshot>> CalculateAsync(DateOnly planningDate, CancellationToken ct)
    {
        var loads = await PlanningResilience.ReadLoadsAsync(db, planningDate, ct);
        var activeLoads = loads
            .Where(load => (load.Status is LoadStatus.Planned or LoadStatus.Dispatched or LoadStatus.InProgress) && load.VehicleId != null)
            .ToList();
        if (activeLoads.Count == 0) return [];

        var vehicleIds = activeLoads.Select(load => load.VehicleId!.Value).Distinct().ToList();
        var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)).ToDictionaryAsync(vehicle => vehicle.Id, ct);
        var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
        var loadIds = activeLoads.Select(load => load.Id).ToList();
        var completed = await db.GeofenceVisits.AsNoTracking()
            .Where(visit => visit.LoadId != null && loadIds.Contains(visit.LoadId.Value) && visit.LoadStopId != null && visit.ConfirmedAtUtc != null && visit.ExitedAtUtc != null && visit.Status == "Departed")
            .Select(visit => new { LoadId = visit.LoadId!.Value, StopId = visit.LoadStopId!.Value })
            .ToListAsync(ct);
        var completedByLoad = completed.GroupBy(item => item.LoadId).ToDictionary(group => group.Key, group => group.Select(item => item.StopId).ToHashSet());

        var captured = new List<LiveEtaSnapshot>();
        var snapshotItems = new List<EtaSnapshotCaptureItem>();
        var now = timeProvider.GetUtcNow();

        foreach (var load in activeLoads)
        {
            if (!vehicles.TryGetValue(load.VehicleId!.Value, out var vehicle)) continue;
            var live = MatchLiveStatus(liveStatuses, vehicle.Registration);
            if (live is null || now - live.LastEventTimeUtc > _options.MaximumTrackingAge) continue;

            var completedStops = completedByLoad.GetValueOrDefault(load.Id) ?? [];
            var nextStop = load.Stops.OrderBy(stop => stop.Sequence)
                .FirstOrDefault(stop => !completedStops.Contains(stop.Id) && stop.Latitude != null && stop.Longitude != null);
            if (nextStop?.Latitude is null || nextStop.Longitude is null) continue;

            RouteMatrixCell? route;
            try
            {
                route = await matrixService.GetSingleRouteAsync(
                    new MatrixPoint(live.Latitude, live.Longitude),
                    new MatrixPoint(nextStop.Latitude.Value, nextStop.Longitude.Value),
                    defaultProfile.Value,
                    ct);
            }
            catch (MatrixTimeoutException exception)
            {
                logger.LogWarning("ETA matrix timed out for run {RunReference}, request {RequestId}.", load.Reference, exception.RequestId);
                continue;
            }
            if (route is null) continue;

            var eta = now.AddMinutes(route.TravelTimeMinutes);
            var risk = nextStop.PlannedArrivalUtc is DateTimeOffset planned
                ? eta <= planned.AddMinutes(15) ? "OnTime" : eta <= planned.AddMinutes(30) ? "AtRisk" : "Late"
                : "Live";
            var snapshot = new LiveEtaSnapshot(
                load.Id,
                load.Reference,
                nextStop.Id,
                nextStop.OrderId,
                nextStop.Name,
                now,
                eta,
                route.TravelTimeMinutes,
                route.DistanceMetres,
                "AzureMapsHgvMatrix",
                risk,
                live.LastEventTimeUtc);
            captured.Add(snapshot);
            snapshotItems.Add(new EtaSnapshotCaptureItem(
                load.Id,
                nextStop.Id,
                nextStop.OrderId,
                eta,
                "AzureMapsHgvMatrix",
                risk,
                "Unavailable",
                0,
                live.LastEventTimeUtc));

            telemetry.TrackEvent("EtaSnapshotCalculated", new Dictionary<string, string>
            {
                ["runId"] = load.Id.ToString(),
                ["runReference"] = load.Reference,
                ["stopId"] = nextStop.Id.ToString(),
                ["source"] = "AzureMapsHgvMatrix"
            });
        }

        if (snapshotItems.Count > 0) await ManagementReportingStore.CaptureAsync(db, snapshotItems, ct);
        foreach (var snapshot in captured)
            await etaHub.Clients.All.SendAsync("EtaSnapshotUpdated", snapshot, ct);
        return captured;
    }

    private static VehicleLiveStatus? MatchLiveStatus(IEnumerable<VehicleLiveStatus> statuses, string registration)
    {
        var registrationKey = Normalise(registration);
        return statuses
            .Where(status =>
            {
                var statusKey = Normalise(status.VehicleIdentifier);
                return statusKey == registrationKey || statusKey.Contains(registrationKey, StringComparison.OrdinalIgnoreCase) || registrationKey.Contains(statusKey, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(status => status.LastEventTimeUtc)
            .FirstOrDefault();
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed class LiveEtaService(
    IServiceScopeFactory scopeFactory,
    IOptions<LiveEtaOptions> options,
    TimeProvider timeProvider,
    ILogger<LiveEtaService> logger) : BackgroundService
{
    private readonly LiveEtaOptions _options = options.Value;
    private readonly TimeZoneInfo _ukZone = ResolveUkZone();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var calculator = scope.ServiceProvider.GetRequiredService<LiveEtaCalculator>();
                    var localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), _ukZone);
                    await calculator.CalculateAsync(DateOnly.FromDateTime(localNow.DateTime), stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Live ETA calculation cycle failed.");
                }

                await Task.Delay(_options.RefreshInterval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected host shutdown; deliberately not logged as an error.
        }
    }

    private static TimeZoneInfo ResolveUkZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
