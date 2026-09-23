using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/run-progress")]
[Authorize]
public sealed class RunProgressController(
    TmsDbContext db,
    DotTrackingClient trackingClient,
    DotTrackingTelemetryStore telemetryStore,
    TachoMasterClient tachoMaster,
    ILogger<RunProgressController> logger,
    IConfiguration configuration) : ControllerBase
{
    private static readonly TimeSpan LiveRefreshBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan LiveTrackingThreshold = TimeSpan.FromMinutes(5);

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayKey) && Request.Query.TryGetValue("key", out var queryKey))
            displayKey = queryKey.FirstOrDefault();
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        try
        {
            string? refreshWarning = null;
            try
            {
                // Keep Operations progression on the same current Falcon evidence as
                // the Hisense route board. SQL remains the resilience fallback when
                // RoadTech is temporarily unavailable. The background ingestion service
                // applies these positions to the authoritative Site Master geofences.
                using var liveRefresh = CancellationTokenSource.CreateLinkedTokenSource(ct);
                liveRefresh.CancelAfter(LiveRefreshBudget);
                var trackingRecords = (await trackingClient.GetLatestVehicleEventsAsync(liveRefresh.Token))
                    .Select(DotTelemetryRecord.FromProvider)
                    .Where(record => record.Latitude is not null && record.Longitude is not null)
                    .ToList();

                if (trackingRecords.Count > 0)
                {
                    await telemetryStore.PersistAsync(trackingRecords, ct, markAsLiveReceipt: true);
                    await GeofenceRunProgression.ProcessTelemetryAsync(db, trackingRecords, ct);
                }
            }
            catch (OperationCanceledException exception) when (!ct.IsCancellationRequested)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    exception,
                    "RoadTech live refresh timed out for Operations progression on {PlanningDate}; using stored tracking evidence.",
                    planningDate);
                refreshWarning = "Live Falcon refresh timed out; progression is using the latest stored tracking evidence.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(
                    exception,
                    "RoadTech live refresh failed for Operations progression on {PlanningDate}; using stored tracking evidence.",
                    planningDate);
                refreshWarning = "Live Falcon refresh was unavailable; progression is using the latest stored tracking evidence.";
            }

            // Current production runs can exist in either the planning register or the
            // live SQL Loads table. Use the shared resilient merger so wallboard progress
            // sees exactly the same current-day runs as the rest of the TMS.
            var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
                .Where(x => x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference)
                .ToList();
            await RunOperationalStore.EnrichAsync(db, loads, ct);
            WallboardPhysicalStops.Apply(loads);

            // Embedded reconstruction remains an in-memory resilience source for tracking
            // coverage. MergeDurableProjectionAsync replaces its visit evidence with active
            // SQL/Site Master GeofenceVisits whenever the authoritative SQL catalogue exists.
            var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);
            snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);

            // Do not persist the embedded reconstruction from a wallboard read. Durable
            // ENTER/EXIT evidence is written by GeofenceRunProgression from the active SQL
            // SiteGeofences during RoadTech ingestion.
            var geofenceCoverage = await RunGeofenceConfigurationCoverage.CalculateAsync(db, loads, ct);
            var geofenceHitRuns = snapshot.Visits
                .Where(visit => visit.LoadId is not null)
                .Select(visit => visit.LoadId!.Value)
                .Distinct()
                .Count();
            var geofenceHitStops = snapshot.Visits
                .Where(visit => visit.LoadStopId is not null)
                .Select(visit => visit.LoadStopId!.Value)
                .Distinct()
                .Count();

            // Movement/ignition/card evidence is deliberately included on run-progress
            // itself. The wallboard must not depend on the separate TV route-progress
            // request to know that a truck is moving or parked.
            var liveByLoad = await ResolveLiveByLoadAsync(db, loads, ct);
            var tachoEvidence = await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, planningDate, logger, ct);
            var records = loads.Select(load => BuildRecord(
                load,
                snapshot,
                now,
                tachoEvidence.ByLoadId.GetValueOrDefault(load.Id),
                liveByLoad.GetValueOrDefault(load.Id))).ToList();

            var activeGeofenceCount = geofenceCoverage.ActiveGeofenceCount > 0
                ? geofenceCoverage.ActiveGeofenceCount
                : snapshot.Fences.Count;

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = records.Count,
                source = "PlanningResilience+RoadTechCurrent+SiteMasterSqlGeofences+DurableGeofenceVisits",
                geofenceAvailable = activeGeofenceCount > 0,
                geofenceCount = activeGeofenceCount,
                geofenceConfiguredRuns = geofenceCoverage.LinkedRuns,
                geofenceLinkedRuns = geofenceCoverage.LinkedRuns,
                geofenceLinkedStops = geofenceCoverage.LinkedStops,
                geofenceTotalStops = geofenceCoverage.TotalStops,
                geofenceHitRuns,
                geofenceHitStops,
                geofenceVisitCount = snapshot.Visits.Count,
                trackingEventCount = snapshot.TrackingEventCount,
                latestTrackingUtc = LatestTracking(snapshot.LatestTrackingUtc, liveByLoad.Values),
                tachoAvailable = tachoEvidence.Available,
                tachoWarning = tachoEvidence.Warning,
                warning = string.Join(" ", new[] { refreshWarning, tachoEvidence.Warning }.Where(value => !string.IsNullOrWhiteSpace(value))),
                records
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Geofence run progression failed for {PlanningDate}.", planningDate);
            db.ChangeTracker.Clear();

            List<Load> loads;
            try
            {
                loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
                    .Where(load => load.Status != LoadStatus.Cancelled)
                    .ToList();
                await RunOperationalStore.EnrichAsync(db, loads, ct);
                WallboardPhysicalStops.Apply(loads);
            }
            catch
            {
                loads = [];
                db.ChangeTracker.Clear();
            }

            var liveByLoad = await ResolveLiveByLoadAsync(db, loads, ct);
            var tachoEvidence = await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, planningDate, logger, ct);
            var geofenceCoverage = await RunGeofenceConfigurationCoverage.CalculateAsync(db, loads, ct);
            var durableHits = await DurableHitCoverageAsync(db, loads, ct);

            var records = loads.OrderBy(x => x.Reference).Select(load =>
            {
                var stops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
                var next = stops.FirstOrDefault();
                var tacho = tachoEvidence.ByLoadId.GetValueOrDefault(load.Id);
                var live = liveByLoad.GetValueOrDefault(load.Id);
                var trackingObservedAtUtc = live?.LastReceivedAtUtc;
                var trackingAge = trackingObservedAtUtc is null ? (TimeSpan?)null : now - trackingObservedAtUtc.Value;
                var trackingFresh = trackingAge is not null && trackingAge.Value >= TimeSpan.Zero && trackingAge.Value <= LiveTrackingThreshold;
                var trackingMoving = trackingFresh && live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2);
                return new
                {
                    loadId = load.Id,
                    loadReference = load.Reference,
                    loadStatus = load.Status.ToString(),
                    runState = trackingMoving ? "InProgress" : InferredRunState(load, stops, now),
                    totalStops = stops.Count,
                    completedStops = 0,
                    progressPercent = 0m,
                    nextStop = next is null ? null : new { next.Id, next.Sequence, next.Name, next.Address, next.PlannedArrivalUtc },
                    phase = trackingMoving ? "Heading to" : "Next job",
                    focusStop = next?.Name,
                    geofenceOnSite = false,
                    trackingFresh,
                    trackingMoving,
                    ignitionOn = trackingFresh ? live?.IgnitionOn : null,
                    driverCardPresent = trackingFresh ? !string.IsNullOrWhiteSpace(live?.CurrentDriverCardNumber) : (bool?)null,
                    trackingObservedAtUtc,
                    trackingAgeSeconds = trackingAge is null ? (int?)null : (int)Math.Max(0, Math.Floor(trackingAge.Value.TotalSeconds)),
                    speedKph = trackingFresh ? live?.SpeedKph : null,
                    stopDwell = Array.Empty<object>(),
                    linkageException = (object?)null,
                    currentVisit = (object?)null,
                    lastDeparture = (object?)null,
                    tacho,
                    calculatedAtUtc = now
                };
            }).ToList();

            var geofenceCount = geofenceCoverage.ActiveGeofenceCount;
            if (geofenceCount == 0)
            {
                try { geofenceCount = EmbeddedGeofenceEngine.ApprovedFences.Count; }
                catch (Exception geofenceException) when (geofenceException is not OperationCanceledException)
                {
                    logger.LogError(geofenceException, "Approved geofence fallback payload could not be initialised while building safe run-progress fallback.");
                }
            }

            return Ok(new
            {
                planningDate,
                calculatedAtUtc = now,
                count = records.Count,
                source = "PlanningResilience+StoredLiveSafeFallback",
                geofenceAvailable = geofenceCount > 0,
                geofenceCount,
                geofenceConfiguredRuns = geofenceCoverage.LinkedRuns,
                geofenceLinkedRuns = geofenceCoverage.LinkedRuns,
                geofenceLinkedStops = geofenceCoverage.LinkedStops,
                geofenceTotalStops = geofenceCoverage.TotalStops,
                geofenceHitRuns = durableHits.HitRuns,
                geofenceHitStops = durableHits.HitStops,
                geofenceVisitCount = durableHits.Visits,
                latestTrackingUtc = LatestTracking(null, liveByLoad.Values),
                tachoAvailable = tachoEvidence.Available,
                tachoWarning = tachoEvidence.Warning,
                warning = string.Join(" ", new[]
                {
                    geofenceCount > 0
                    ? "Live progression reconstruction could not be calculated on this refresh. Configured Site Master linkage and durable geofence evidence remain visible with stored live movement."
                    : "Live run progression could not be calculated and no active geofence catalogue was available on this refresh. Stored live movement remains active.",
                    tachoEvidence.Warning
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                records
            });
        }
    }

    private static object BuildRecord(
        Load load,
        EmbeddedGeofenceSnapshot snapshot,
        DateTimeOffset now,
        RunTachoEvidence? tacho,
        VehicleLiveStatus? live)
    {
        var orderedStops = (load.Stops ?? []).OrderBy(x => x.Sequence).ToList();
        var visits = snapshot.Visits.Where(x => x.LoadId == load.Id).OrderBy(x => x.EnteredAtUtc).ToList();
        var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
        var stopDwell = RunStopDwellProjection.Build(load, visits, snapshot.ActiveVisits, now);
        var linkageException = RunStopDwellProjection.LinkExceptionFor(load, snapshot);

        var activeVisit = snapshot.ActiveVisits
            .Where(x => x.LoadId == load.Id)
            .OrderByDescending(x => x.EnteredAtUtc)
            .FirstOrDefault();
        var nextStop = RunProgressionFrontier.NextOperationalStop(orderedStops, completedStopIds, activeVisit?.LoadStopId);
        var progressionFrontierSequence = RunProgressionFrontier.Sequence(orderedStops, completedStopIds, activeVisit?.LoadStopId);
        var evidenceGaps = RunProgressionFrontier.EvidenceGapsBeforeFrontier(orderedStops, completedStopIds, activeVisit?.LoadStopId);
        var lastDeparture = visits
            .Where(x => x.ExitedAtUtc != null && x.ConfirmedAtUtc != null)
            .OrderByDescending(x => x.ExitedAtUtc)
            .FirstOrDefault();

        var trackingObservedAtUtc = live?.LastReceivedAtUtc;
        var trackingAge = trackingObservedAtUtc is null ? (TimeSpan?)null : now - trackingObservedAtUtc.Value;
        var trackingFresh = trackingAge is not null && trackingAge.Value >= TimeSpan.Zero && trackingAge.Value <= LiveTrackingThreshold;
        var trackingMoving = trackingFresh && live is not null && (live.IsMoving == true || (live.SpeedKph ?? 0) > 2);

        var totalStops = orderedStops.Count;
        var completedStops = completedStopIds.Count;
        var progressPercent = totalStops == 0 ? 0m : Math.Round((decimal)completedStops / totalStops * 100m, 1);
        var waitLimit = activeVisit?.Fence.MaxWaitMinutes ?? activeVisit?.Fence.CategoryMaxWaitMinutes;
        var dwell = activeVisit?.DwellMinutes;
        if (activeVisit is not null && now - activeVisit.LastInsideAtUtc <= TimeSpan.FromMinutes(5))
            dwell = Math.Max(activeVisit.DwellMinutes, Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalMinutes)));
        var delayed = activeVisit is not null && waitLimit is int limit && dwell.GetValueOrDefault() > limit;
        var activeStatus = activeVisit is null ? null : delayed ? "SiteDelay" : activeVisit.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived";
        var complete = RunProgressionFrontier.FinalStopCompleted(orderedStops, completedStopIds);
        var runState = complete
            ? "Completed"
            : activeStatus ?? (progressionFrontierSequence > 0 || trackingMoving ? "BetweenStops" : InferredRunState(load, orderedStops, now));
        var phase = complete
            ? "Complete"
            : activeVisit is not null
                ? "On site"
                : progressionFrontierSequence > 0 || trackingMoving
                    ? "Heading to"
                    : "Next job";

        return new
        {
            loadId = load.Id,
            loadReference = load.Reference,
            loadStatus = load.Status.ToString(),
            runState,
            totalStops,
            completedStops,
            progressionFrontierSequence,
            evidenceGaps = evidenceGaps.Select(stop => new { stop.Id, stop.Sequence, stop.Name }).ToList(),
            progressPercent,
            nextStop = nextStop is null ? null : new { nextStop.Id, nextStop.Sequence, nextStop.Name, nextStop.Address, nextStop.PlannedArrivalUtc },
            phase,
            focusStop = activeVisit?.Fence.Name ?? nextStop?.Name,
            geofenceOnSite = activeVisit is not null,
            trackingFresh,
            trackingMoving,
            ignitionOn = trackingFresh ? live?.IgnitionOn : null,
            driverCardPresent = trackingFresh ? !string.IsNullOrWhiteSpace(live?.CurrentDriverCardNumber) : (bool?)null,
            trackingObservedAtUtc,
            trackingAgeSeconds = trackingAge is null ? (int?)null : (int)Math.Max(0, Math.Floor(trackingAge.Value.TotalSeconds)),
            speedKph = trackingFresh ? live?.SpeedKph : null,
            currentVisit = activeVisit is null ? null : new
            {
                activeVisit.Id,
                geofenceId = activeVisit.Fence.Id,
                geofenceName = activeVisit.Fence.Name,
                category = activeVisit.Fence.Category,
                activeVisit.LoadStopId,
                activeVisit.EnteredAtUtc,
                siteArrivalUtc = activeVisit.EnteredAtUtc,
                activeVisit.ConfirmedAtUtc,
                siteDepartureUtc = (DateTimeOffset?)null,
                state = "OnSite",
                dwellMinutes = dwell,
                liveDwellMinutes = dwell,
                liveDwellSeconds = Math.Max(0, (int)Math.Floor((now - activeVisit.EnteredAtUtc).TotalSeconds)),
                finalDwellMinutes = (int?)null,
                finalDwellSeconds = (int?)null,
                waitLimitMinutes = waitLimit,
                isDelayed = delayed,
                status = activeStatus,
                statusReason = delayed
                    ? $"Dwell is {dwell.GetValueOrDefault()} minutes; site threshold is {waitLimit} minutes."
                    : activeVisit.ConfirmedAtUtc is not null
                        ? $"Confirmed after {dwell.GetValueOrDefault()} minutes in {activeVisit.Fence.Name}."
                        : $"Inside {activeVisit.Fence.Name}; awaiting 10-minute confirmation."
            },
            lastDeparture = lastDeparture is null ? null : new
            {
                lastDeparture.LoadStopId,
                siteArrivalUtc = lastDeparture.EnteredAtUtc,
                siteDepartureUtc = lastDeparture.ExitedAtUtc,
                lastDeparture.ExitedAtUtc,
                lastDeparture.DwellMinutes,
                finalDwellMinutes = lastDeparture.DwellMinutes,
                finalDwellSeconds = lastDeparture.ExitedAtUtc is null ? (int?)null : Math.Max(0, (int)Math.Floor((lastDeparture.ExitedAtUtc.Value - lastDeparture.EnteredAtUtc).TotalSeconds)),
                state = "Departed"
            },
            stopDwell = stopDwell.Select(stop => new
            {
                stop.StopId,
                stop.Sequence,
                stop.StopName,
                stop.State,
                stop.GeofenceId,
                stop.GeofenceName,
                stop.SiteArrivalUtc,
                stop.SiteDepartureUtc,
                stop.LiveDwellSeconds,
                stop.LiveDwellMinutes,
                stop.FinalDwellSeconds,
                stop.FinalDwellMinutes,
                stop.DwellSeconds
            }),
            linkageException,
            tacho,
            calculatedAtUtc = now
        };
    }

    private static async Task<Dictionary<Guid, VehicleLiveStatus?>> ResolveLiveByLoadAsync(
        TmsDbContext db,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct)
    {
        try
        {
            var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
            if (vehicleIds.Count == 0) return [];

            var vehicles = await db.Vehicles.AsNoTracking()
                .Where(vehicle => vehicleIds.Contains(vehicle.Id))
                .ToListAsync(ct);
            var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles, ct);
            var liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);

            var result = new Dictionary<Guid, VehicleLiveStatus?>();
            foreach (var load in loads)
            {
                VehicleLiveStatus? live = null;
                if (load.VehicleId is Guid vehicleId && aliasesByVehicle.TryGetValue(vehicleId, out var aliases))
                    live = ExecutionIdentityResolver.MatchLive(aliases, liveStatuses);
                result[load.Id] = live;
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static async Task<DurableHitCoverage> DurableHitCoverageAsync(
        TmsDbContext db,
        IReadOnlyCollection<Load> loads,
        CancellationToken ct)
    {
        try
        {
            var loadIds = loads.Select(load => load.Id).Distinct().ToList();
            if (loadIds.Count == 0) return new DurableHitCoverage(0, 0, 0);
            var rows = await db.GeofenceVisits.AsNoTracking()
                .Where(visit => visit.LoadId != null && loadIds.Contains(visit.LoadId.Value))
                .Select(visit => new { visit.LoadId, visit.LoadStopId })
                .ToListAsync(ct);
            return new DurableHitCoverage(
                rows.Where(row => row.LoadId is not null).Select(row => row.LoadId!.Value).Distinct().Count(),
                rows.Where(row => row.LoadStopId is not null).Select(row => row.LoadStopId!.Value).Distinct().Count(),
                rows.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return new DurableHitCoverage(0, 0, 0);
        }
    }

    private static DateTimeOffset? LatestTracking(
        DateTimeOffset? snapshotLatest,
        IEnumerable<VehicleLiveStatus?> liveStatuses)
    {
        var liveLatest = liveStatuses
            .Where(status => status is not null)
            .Select(status => (DateTimeOffset?)status!.LastReceivedAtUtc)
            .OrderByDescending(value => value)
            .FirstOrDefault();
        if (snapshotLatest is null) return liveLatest;
        if (liveLatest is null) return snapshotLatest;
        return snapshotLatest > liveLatest ? snapshotLatest : liveLatest;
    }

    internal static string InferredRunState(Load load, IReadOnlyList<LoadStop> orderedStops, DateTimeOffset now)
    {
        if (load.Status is LoadStatus.Completed or LoadStatus.Cancelled)
            return load.Status.ToString();
        if (load.Status is LoadStatus.Dispatched or LoadStatus.InProgress)
            return "InProgress";
        if (load.Status != LoadStatus.Planned)
            return load.Status.ToString();
        if (load.VehicleId is null && load.DriverId is null)
            return load.Status.ToString();

        return load.Status.ToString();
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

    private sealed record DurableHitCoverage(int HitRuns, int HitStops, int Visits);
}
