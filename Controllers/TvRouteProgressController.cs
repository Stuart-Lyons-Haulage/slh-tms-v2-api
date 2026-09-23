using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Read-only route progression for the office TV. The progression is derived
/// from the embedded approved SLH geofences, RoadTech tracking and the planning register.
/// No geofence-specific SQL tables are required.
/// </summary>
[ApiController, Route("api/v1/tv-display/route-progress")]
public sealed class TvRouteProgressController(
    TmsDbContext db,
    IConfiguration configuration,
    DotTrackingClient trackingClient,
    TachoMasterClient tachoMaster,
    DotTrackingTelemetryStore telemetryStore,
    ILogger<TvRouteProgressController> logger) : ControllerBase
{
    private static readonly TimeSpan LiveTrackingThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan LiveRefreshBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan TachoEvidenceBudget = TimeSpan.FromSeconds(5);

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This TV display is not authorised." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        WallboardPhysicalStops.Apply(loads);

        // TV reads must remain bounded. Current RoadTech ingestion and the shared geofence
        // engine run continuously in the background; a display refresh must consume that durable
        // projection rather than replaying the whole operating day synchronously.
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        var snapshot = new EmbeddedGeofenceSnapshot(
            EmbeddedGeofenceEngine.ApprovedFences,
            [],
            [],
            [],
            0,
            liveStatuses.Count > 0 ? liveStatuses.Max(status => status.LastReceivedAtUtc) : null);
        snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);

        var vehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var vehicles = vehicleIds.Count == 0
            ? new List<Vehicle>()
            : await SafeList(db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)), ct);
        var vehicleById = vehicles.ToDictionary(x => x.Id);
        if (vehicles.Count > 0 && liveStatuses.Count > 0)
            await ExecutionIdentityResolver.RepairDotVehicleMappingsAsync(
                db,
                vehicles,
                liveStatuses.Select(status => status.VehicleIdentifier),
                ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles, ct);
        var tachoEvidence = await ResolveTachoEvidenceForTvAsync(loads, day, ct);

        var rows = new List<object>();
        foreach (var load in loads)
        {
            var stops = load.Stops.OrderBy(x => x.Sequence).ToList();
            var visits = snapshot.Visits.Where(x => x.LoadId == load.Id).OrderBy(x => x.EnteredAtUtc).ToList();
            var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
            var stopDwell = RunStopDwellProjection.Build(load, visits, snapshot.ActiveVisits, now);
            var linkageException = RunStopDwellProjection.LinkExceptionFor(load, snapshot);
            var activeVisit = snapshot.ActiveVisits
                .Where(x => x.LoadId == load.Id)
                .OrderByDescending(x => x.EnteredAtUtc)
                .FirstOrDefault();

            var currentStop = activeVisit?.LoadStopId is Guid currentId
                ? stops.FirstOrDefault(x => x.Id == currentId)
                : null;
            var nextStop = RunProgressionFrontier.NextOperationalStop(stops, completedStopIds, currentStop?.Id);
            var progressionFrontierSequence = RunProgressionFrontier.Sequence(stops, completedStopIds, currentStop?.Id);
            var evidenceGaps = RunProgressionFrontier.EvidenceGapsBeforeFrontier(stops, completedStopIds, currentStop?.Id);

            // Lake Lane is the depot origin. Its departure proves the run has started
            // before the first customer geofence has been reached.
            var lakeLaneDeparture = progressionFrontierSequence == 0
                ? OperationalRunOrigin.LakeLaneDepartureFor(snapshot, load)
                : null;
            var departedLakeLane = lakeLaneDeparture?.ExitedAtUtc is not null;

            VehicleLiveStatus? live = null;
            if (load.VehicleId is Guid vehicleId && vehicleById.TryGetValue(vehicleId, out var vehicle))
                live = aliasesByVehicle.TryGetValue(vehicle.Id, out var aliases)
                    ? ExecutionIdentityResolver.MatchLive(aliases, liveStatuses)
                    : null;
            var tacho = tachoEvidence.ByLoadId.GetValueOrDefault(load.Id);

            var freshnessAtUtc = live?.LastReceivedAtUtc;
            var trackingAge = freshnessAtUtc is null ? (TimeSpan?)null : now - freshnessAtUtc.Value;
            var trackingFresh = trackingAge is not null && trackingAge.Value >= TimeSpan.Zero && trackingAge.Value <= LiveTrackingThreshold;
            var trackingMoving = trackingFresh && live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2);
            var geofenceStarted = departedLakeLane || currentStop is not null || progressionFrontierSequence > 0;

            // Only fresh RoadTech coordinates may influence the visible vehicle marker.
            // Lake Lane departure supplies the trustworthy origin for the first leg.
            var truckPosition = TruckPositionPercent(
                stops,
                completedStopIds,
                activeVisit,
                trackingFresh ? live : null,
                lakeLaneDeparture);
            var complete = RunProgressionFrontier.FinalStopCompleted(stops, completedStopIds);
            var phase = complete
                ? "Complete"
                : currentStop is not null
                    ? "On site"
                    : geofenceStarted
                        ? "Heading to"
                        : "Next job";
            var focusStop = currentStop ?? nextStop;

            var stopRows = stops.Select(stop =>
            {
                var state = completedStopIds.Contains(stop.Id)
                    ? "completed"
                    : currentStop?.Id == stop.Id
                        ? "onsite"
                        : nextStop?.Id == stop.Id && geofenceStarted
                            ? "heading"
                            : "upcoming";
                return new
                {
                    stop.Id,
                    stop.Sequence,
                    stop.Name,
                    stop.PlannedArrivalUtc,
                    state
                };
            }).ToList();

            rows.Add(new
            {
                loadId = load.Id,
                reference = load.Reference,
                totalStops = stops.Count,
                completedStops = completedStopIds.Count,
                progressionFrontierSequence,
                evidenceGaps = evidenceGaps.Select(stop => new { stop.Id, stop.Sequence, stop.Name }).ToList(),
                currentStopId = currentStop?.Id,
                nextStopId = nextStop?.Id,
                focusStop = focusStop?.Name,
                phase,
                truckPositionPercent = truckPosition,
                originDepartureUtc = lakeLaneDeparture?.ExitedAtUtc,
                originGeofence = lakeLaneDeparture?.Fence.Name,
                geofenceOnSite = currentStop is not null,
                currentVisit = activeVisit is null ? null : new
                {
                    geofenceName = activeVisit.Fence.Name,
                    loadStopId = activeVisit.LoadStopId,
                    enteredAtUtc = activeVisit.EnteredAtUtc,
                    siteArrivalUtc = activeVisit.EnteredAtUtc,
                    siteDepartureUtc = (DateTimeOffset?)null,
                    state = "OnSite",
                    dwellMinutes = Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes))),
                    liveDwellMinutes = Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes))),
                    liveDwellSeconds = Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalSeconds)),
                    status = activeVisit.ConfirmedAtUtc is null ? "Arrived" : "OnSite"
                },
                stopDwell,
                linkageException,
                trackingFresh,
                trackingMoving,
                ignitionOn = trackingFresh ? live?.IgnitionOn : null,
                driverCardPresent = trackingFresh ? !string.IsNullOrWhiteSpace(live?.CurrentDriverCardNumber) : (bool?)null,
                trackingObservedAtUtc = freshnessAtUtc,
                trackingAgeSeconds = trackingAge is null ? (int?)null : (int)Math.Max(0, Math.Floor(trackingAge.Value.TotalSeconds)),
                speedKph = trackingFresh ? live?.SpeedKph : null,
                tacho,
                stops = stopRows
            });
        }

        var geofenceCoverage = await RunGeofenceConfigurationCoverage.CalculateAsync(db, loads, ct);
        var geofenceHitRuns = snapshot.Visits.Where(x => x.LoadId is not null).Select(x => x.LoadId!.Value).Distinct().Count();
        var geofenceHitStops = snapshot.Visits.Where(x => x.LoadStopId is not null).Select(x => x.LoadStopId!.Value).Distinct().Count();

        return Ok(new
        {
            planningDate = day,
            calculatedAtUtc = now,
            trackingSource = "SQL live status + durable geofence projection",
            latestTrackingUtc = liveStatuses.Count > 0
                ? liveStatuses.Max(status => status.LastReceivedAtUtc)
                : snapshot.LatestTrackingUtc,
            geofenceAvailable = snapshot.Fences.Count > 0,
            geofenceCount = snapshot.Fences.Count,
            geofenceConfiguredRuns = geofenceCoverage.LinkedRuns,
            geofenceLinkedStops = geofenceCoverage.LinkedStops,
            geofenceTotalStops = geofenceCoverage.TotalStops,
            geofenceHitRuns,
            geofenceHitStops,
            geofenceVisitCount = snapshot.Visits.Count,
            geofenceConfirmedVisitCount = snapshot.ConfirmedVisits.Count,
            geofenceLinkedRuns = geofenceCoverage.LinkedRuns,
            tachoAvailable = tachoEvidence.Available,
            tachoWarning = tachoEvidence.Warning,
            runs = rows
        });
    }

    private async Task<(List<VehicleLiveStatus> Statuses, string Source)> LoadLiveStatusesAsync(
        DateTimeOffset receivedAtUtc,
        CancellationToken ct)
    {
        try
        {
            using var liveRefresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
            liveRefresh.CancelAfter(LiveRefreshBudget);
            var providerRows = await trackingClient.GetLatestVehicleEventsAsync(liveRefresh.Token);
            var records = providerRows
                .Select(DotTelemetryRecord.FromProvider)
                .Where(record => record.Latitude is not null && record.Longitude is not null)
                .ToList();

            if (records.Count > 0)
            {
                try
                {
                    await telemetryStore.PersistAsync(records, ct, markAsLiveReceipt: true);
                    await GeofenceRunProgression.ProcessTelemetryAsync(db, records, ct);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(
                        exception,
                        "Fresh RoadTech telemetry could not be persisted or applied to SQL geofences for TV/geofence history; the TV will continue from the direct provider snapshot.");
                }

                var statuses = records
                    .Where(record => !string.IsNullOrWhiteSpace(record.VehicleIdentifier))
                    .GroupBy(
                        record => Normalise(record.VehicleIdentifier),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
                    .Select(record => new VehicleLiveStatus
                    {
                        VehicleIdentifier = ExecutionIdentityResolver.NormaliseVehicle(record.VehicleIdentifier),
                        LastEventTimeUtc = record.EventTimeUtc,
                        LastReceivedAtUtc = receivedAtUtc,
                        Latitude = record.Latitude!.Value,
                        Longitude = record.Longitude!.Value,
                        SpeedKph = record.SpeedKph,
                        IgnitionOn = record.IgnitionOn,
                        IsMoving = record.IsMoving,
                        LastKnownStatus = record.Status,
                        CurrentDriverName = record.DriverName,
                        CurrentDriverCardNumber = record.DriverCardNumber,
                        UpdatedAtUtc = receivedAtUtc
                    })
                    .ToList();

                if (statuses.Count > 0)
                    return (statuses, "RoadTech current");
            }
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                exception,
                "RoadTech direct TV snapshot timed out; falling back to persisted live status.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "RoadTech direct TV snapshot failed; falling back to persisted live status.");
        }

        return (await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct), "SQL fallback");
    }

    private async Task<RunTachoEvidenceResult> ResolveTachoEvidenceForTvAsync(
        IReadOnlyCollection<Load> loads,
        DateOnly day,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TachoEvidenceBudget);
            return await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, day, logger, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(
                exception,
                "TachoMaster TV evidence timed out; route-progress will keep tracking visible without tacho enrichment.");
            return new RunTachoEvidenceResult(
                new Dictionary<Guid, RunTachoEvidence>(),
                false,
                "TachoMaster evidence timed out on this TV refresh.");
        }
    }

    private static decimal TruckPositionPercent(
        IReadOnlyList<LoadStop> stops,
        IReadOnlySet<Guid> completedStopIds,
        DerivedVisit? activeVisit,
        VehicleLiveStatus? live,
        DerivedVisit? lakeLaneDeparture)
    {
        if (stops.Count == 0) return 0m;

        if (activeVisit?.LoadStopId is Guid activeStopId)
        {
            var activeIndex = IndexOf(stops, activeStopId);
            if (activeIndex >= 0) return StopPercent(activeIndex, stops.Count);
        }

        var lastCompletedIndex = -1;
        for (var i = 0; i < stops.Count; i++)
            if (completedStopIds.Contains(stops[i].Id)) lastCompletedIndex = i;

        if (lastCompletedIndex >= stops.Count - 1) return 100m;

        // First leg: START is the Lake Lane geofence. Once the vehicle leaves it, use
        // the same live-coordinate interpolation as later legs so the truck visibly moves
        // toward stop 1 before any customer geofence has been completed.
        if (lastCompletedIndex < 0)
        {
            if (lakeLaneDeparture?.ExitedAtUtc is null) return 0m;
            var first = stops[0];
            var firstPercent = StopPercent(0, stops.Count);
            var origin = OperationalRunOrigin.FenceCentre(lakeLaneDeparture.Fence);
            if (live is not null && origin is not null && first.Latitude is not null && first.Longitude is not null)
            {
                var fromOrigin = DistanceKm(origin.Value.Latitude, origin.Value.Longitude, live.Latitude, live.Longitude);
                var toFirst = DistanceKm(live.Latitude, live.Longitude, first.Latitude.Value, first.Longitude.Value);
                var total = fromOrigin + toFirst;
                if (total > 0.05)
                {
                    var fraction = Math.Clamp((decimal)(fromOrigin / total), 0.02m, 0.98m);
                    return Math.Round(firstPercent * fraction, 1);
                }
            }

            // Departure itself is enough to show that the run is no longer parked.
            return Math.Min(1m, firstPercent);
        }

        var nextIndex = lastCompletedIndex + 1;
        while (nextIndex < stops.Count && completedStopIds.Contains(stops[nextIndex].Id)) nextIndex++;
        if (nextIndex >= stops.Count) return 100m;

        var legFraction = 0.5m;
        var previous = stops[lastCompletedIndex];
        var next = stops[nextIndex];
        if (live is not null && previous.Latitude is not null && previous.Longitude is not null && next.Latitude is not null && next.Longitude is not null)
        {
            var fromPrevious = DistanceKm(previous.Latitude.Value, previous.Longitude.Value, live.Latitude, live.Longitude);
            var toNext = DistanceKm(live.Latitude, live.Longitude, next.Latitude.Value, next.Longitude.Value);
            var total = fromPrevious + toNext;
            if (total > 0.05)
                legFraction = Math.Clamp((decimal)(fromPrevious / total), 0.02m, 0.98m);
        }

        var previousPercent = StopPercent(lastCompletedIndex, stops.Count);
        var nextPercent = StopPercent(nextIndex, stops.Count);
        return Math.Round(previousPercent + (nextPercent - previousPercent) * legFraction, 1);
    }

    private static int IndexOf(IReadOnlyList<LoadStop> stops, Guid id)
    {
        for (var i = 0; i < stops.Count; i++) if (stops[i].Id == id) return i;
        return -1;
    }

    private static decimal StopPercent(int index, int count) =>
        count <= 0 ? 0m : Math.Round((decimal)(index + 1) / count * 100m, 1);

    private static double DistanceKm(decimal latitude1, decimal longitude1, decimal latitude2, decimal longitude2)
    {
        const double earthRadiusKm = 6371.0;
        var lat1 = DegreesToRadians((double)latitude1);
        var lat2 = DegreesToRadians((double)latitude2);
        var dLat = lat2 - lat1;
        var dLon = DegreesToRadians((double)longitude2 - (double)longitude1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return earthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch { dbSafeNoop(); return new List<T>(); }

        static void dbSafeNoop() { }
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
