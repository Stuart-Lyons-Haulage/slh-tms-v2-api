using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Operational wallboard timing derived from geofence execution and live RoadTech position.
/// Geofence departure proves progression and is the resilient fallback anchor. While the
/// vehicle is between stops, fresh RoadTech GPS + current time determine remaining travel.
/// Dwell starts at first observation inside a geofence and is projected between remaining jobs.
/// </summary>
[ApiController, Route("api/v1/run-timing")]
[Authorize]
public sealed class RunTimingController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    IConfiguration configuration,
    ILogger<RunTimingController> logger) : ControllerBase
{
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
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .Take(500)
            .ToList();

        EmbeddedGeofenceSnapshot snapshot;
        try
        {
            snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, GeofencePlanningMatch.PrepareLoads(loads), ct);
            snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Geofence timing was unavailable for wallboard timing.");
            return Ok(new RunTimingResponse(planningDate, DateTimeOffset.UtcNow, false, []));
        }

        PlannerSourceMasterDataResolver? masterData = null;
        try
        {
            masterData = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Site Master resolution was unavailable while calculating final ETAs; embedded geofence coordinate fallback will remain active.");
            db.ChangeTracker.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        var liveByVehicle = await LoadFreshLivePositionsAsync(loads, now, ct);
        var defaultIntermediateDwellMinutes = Math.Clamp(
            configuration.GetValue<int?>("Operations:DefaultIntermediateDwellMinutes") ?? 20,
            0,
            120);
        var records = new List<RunTimingRecord>();

        foreach (var load in loads)
        {
            var orderedStops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
            var finalDestination = RunFinalDestination.Select(orderedStops);
            var visits = snapshot.Visits
                .Where(visit => visit.LoadId == load.Id)
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToList();
            var completedStopIds = GeofencePlanningMatch.CompletedStopIds(load, visits);
            var currentVisit = visits.LastOrDefault(visit => visit.ExitedAtUtc is null);
            var activeStopId = currentVisit?.LoadStopId;
            var completed = RunProgressionFrontier.FinalStopCompleted(orderedStops, completedStopIds);
            var remainingStops = completed
                ? []
                : RunProgressionFrontier.RemainingOperationalStops(orderedStops, completedStopIds, activeStopId).ToList();
            var nextStop = remainingStops.FirstOrDefault();
            var evidenceGaps = RunProgressionFrontier.EvidenceGapsBeforeFrontier(orderedStops, completedStopIds, activeStopId)
                .Select(stop => new RunTimingEvidenceGap(stop.Id, stop.Sequence, stop.Name))
                .ToList();
            var lastDeparture = visits
                .Where(visit => visit.ExitedAtUtc is not null && visit.LoadStopId is not null && completedStopIds.Contains(visit.LoadStopId.Value))
                .OrderByDescending(visit => visit.ExitedAtUtc)
                .FirstOrDefault();
            var finalDestinationVisit = finalDestination is null
                ? null
                : visits.Where(visit => visit.LoadStopId == finalDestination.Id)
                    .OrderByDescending(visit => visit.EnteredAtUtc)
                    .FirstOrDefault();

            // Lake Lane is the authoritative route origin before any customer departure.
            // Once a customer stop has completed, that departure proves progression.
            var lakeLaneDeparture = lastDeparture is null
                ? OperationalRunOrigin.LakeLaneDepartureFor(snapshot, load)
                : null;
            var timingDeparture = lastDeparture ?? lakeLaneDeparture;
            var live = load.VehicleId is Guid vehicleId
                ? liveByVehicle.GetValueOrDefault(vehicleId)
                : null;

            DateTimeOffset? nextEtaUtc = null;
            string etaSource = "Unavailable";
            // If the customer destination has already been entered, preserve that actual
            // arrival even when later operational/depot stops remain on the run.
            DateTimeOffset? finalEtaUtc = finalDestinationVisit?.EnteredAtUtc;
            string finalEtaSource = finalEtaUtc is null ? "Unavailable" : "Geofence";
            string? etaUnavailableStopName = null;
            string? etaUnavailableReason = null;
            var etaLegs = new List<RunTimingLeg>();
            var preRouteDwellMinutes = 0;
            var routeAnchorSource = "Unavailable";

            if (!completed)
            {
                (decimal Longitude, decimal Latitude)? routeOrigin = null;
                DateTimeOffset? routeAnchorUtc = null;
                var routeStops = remainingStops;
                var finalDestinationReached = finalEtaUtc is not null;
                var destinationContainsEstimate = false;

                if (currentVisit is not null)
                {
                    var activeStop = currentVisit.LoadStopId is Guid currentId
                        ? orderedStops.FirstOrDefault(stop => stop.Id == currentId)
                        : orderedStops.FirstOrDefault(stop => GeofencePlanningMatch.SamePhysicalSite(stop, currentVisit.Fence));

                    if (activeStop is not null)
                    {
                        routeOrigin = OperationalRunOrigin.FenceCentre(currentVisit.Fence);
                        routeStops = orderedStops
                            .Where(stop => stop.Sequence > activeStop.Sequence && !completedStopIds.Contains(stop.Id))
                            .ToList();
                        var predictedDwell = PredictedDwellMinutes(snapshot, activeStop, defaultIntermediateDwellMinutes);
                        var elapsedMinutes = Math.Max(0, (now - currentVisit.EnteredAtUtc).TotalMinutes);
                        var remainingDwell = Math.Max(0, predictedDwell - elapsedMinutes);
                        preRouteDwellMinutes = (int)Math.Ceiling(remainingDwell);
                        routeAnchorUtc = now + TimeSpan.FromMinutes(remainingDwell);
                        routeAnchorSource = "Current geofence + remaining dwell";

                        if (!finalDestinationReached && finalDestination is not null && activeStop.Id == finalDestination.Id)
                        {
                            finalEtaUtc = currentVisit.EnteredAtUtc;
                            finalEtaSource = "Geofence";
                            finalDestinationReached = true;
                        }
                        else if (!finalDestinationReached && finalDestination is not null && activeStop.Sequence < finalDestination.Sequence && remainingDwell > 0)
                        {
                            destinationContainsEstimate = true;
                        }
                    }
                    else
                    {
                        etaUnavailableStopName = nextStop?.Name;
                        etaUnavailableReason = $"Current geofence '{currentVisit.Fence.Name}' is not linked to a planned stop for this run.";
                    }
                }
                else if (timingDeparture?.ExitedAtUtc is DateTimeOffset departedAt)
                {
                    (decimal Longitude, decimal Latitude)? departureOrigin;
                    if (lastDeparture is not null)
                    {
                        var previousStop = lastDeparture.LoadStopId is Guid previousStopId
                            ? orderedStops.FirstOrDefault(stop => stop.Id == previousStopId)
                            : null;
                        departureOrigin = Origin(previousStop, lastDeparture.Fence, masterData);
                    }
                    else
                    {
                        departureOrigin = OperationalRunOrigin.FenceCentre(timingDeparture.Fence);
                    }

                    if (departureOrigin is not null)
                    {
                        var anchor = RunTimingLiveAnchor.BetweenStops(now, departedAt, departureOrigin.Value, live);
                        routeOrigin = anchor.Origin;
                        routeAnchorUtc = anchor.AnchorUtc;
                        routeAnchorSource = anchor.Source;
                    }
                }
                else if (remainingStops.Count > 0)
                {
                    etaUnavailableStopName = nextStop?.Name;
                    etaUnavailableReason = "No authoritative Lake Lane/customer geofence departure is available to prove that this run has started.";
                }

                if (routeOrigin is not null && routeAnchorUtc is not null && routeStops.Count > 0)
                {
                    var cursor = routeOrigin.Value;
                    var cursorTime = routeAnchorUtc.Value;
                    var routeFailed = false;

                    for (var index = 0; index < routeStops.Count; index++)
                    {
                        var stop = routeStops[index];
                        var destination = OperationalStopCoordinates.Resolve(stop, masterData);
                        if (destination is null)
                        {
                            etaUnavailableStopName = stop.Name;
                            etaUnavailableReason = "No routable coordinate could be resolved from the plan, Site Master aliases/linked geofence, or approved DOT geofence.";
                            routeFailed = true;
                            break;
                        }

                        try
                        {
                            var route = await maps.TravelTimeEstimate(cursor, destination.Value, ct);
                            cursorTime += route.TravelTime;
                            if (!finalDestinationReached) destinationContainsEstimate |= route.IsApproximate;

                            if (index == 0 && currentVisit is null)
                            {
                                nextEtaUtc = cursorTime;
                                etaSource = route.IsApproximate ? "GeofenceEstimated" : "Geofence";
                            }

                            if (!finalDestinationReached && finalDestination is not null && stop.Id == finalDestination.Id)
                            {
                                finalEtaUtc = cursorTime;
                                finalEtaSource = destinationContainsEstimate ? "GeofenceEstimated" : "Geofence";
                                finalDestinationReached = true;
                            }
                            cursor = destination.Value;

                            var dwellMinutes = 0;
                            if (index < routeStops.Count - 1)
                                dwellMinutes = PredictedDwellMinutes(snapshot, stop, defaultIntermediateDwellMinutes);

                            etaLegs.Add(new RunTimingLeg(
                                stop.Id,
                                stop.Sequence,
                                stop.Name,
                                (int)Math.Ceiling(route.TravelTime.TotalMinutes),
                                dwellMinutes,
                                cursorTime,
                                route.IsApproximate,
                                route.Provider));

                            if (dwellMinutes > 0)
                            {
                                cursorTime += TimeSpan.FromMinutes(dwellMinutes);
                                if (!finalDestinationReached) destinationContainsEstimate = true;
                            }
                        }
                        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or Azure.Identity.AuthenticationFailedException)
                        {
                            logger.LogDebug(exception, "Could not calculate live/geofence ETA for run {Run} stop {Stop}.", load.Reference, stop.Name);
                            etaUnavailableStopName = stop.Name;
                            etaUnavailableReason = "The route provider could not calculate this remaining leg.";
                            routeFailed = true;
                            break;
                        }
                    }

                    // A failure after the customer destination must not erase the ETA/actual
                    // arrival already calculated for that destination. Only fail closed when
                    // the route could not reach the customer destination itself.
                    if (routeFailed && !finalDestinationReached)
                    {
                        finalEtaUtc = null;
                        finalEtaSource = "Unavailable";
                    }
                }
                else if (routeStops.Count > 0 && etaUnavailableReason is null)
                {
                    etaUnavailableStopName = nextStop?.Name;
                    etaUnavailableReason = routeOrigin is null
                        ? "The authoritative departure exists, but its route origin could not be resolved."
                        : "The remaining route does not yet have an authoritative timing anchor.";
                }
            }

            records.Add(new RunTimingRecord(
                load.Id,
                RunDisplayLabel.For(load),
                completed,
                nextStop?.Id,
                nextStop?.Sequence,
                nextStop?.Name,
                nextEtaUtc,
                etaSource,
                finalEtaUtc,
                finalEtaSource,
                finalDestination?.Id,
                finalDestination?.Name,
                timingDeparture?.ExitedAtUtc,
                currentVisit?.EnteredAtUtc,
                currentVisit?.Fence.Name,
                etaUnavailableStopName,
                etaUnavailableReason,
                preRouteDwellMinutes,
                etaLegs,
                routeAnchorSource,
                evidenceGaps));
        }

        return Ok(new RunTimingResponse(planningDate, now, true, records));
    }

    private async Task<Dictionary<Guid, VehicleLiveStatus>> LoadFreshLivePositionsAsync(
        IReadOnlyCollection<Load> loads,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var vehicleIds = loads
            .Where(load => load.VehicleId is not null)
            .Select(load => load.VehicleId!.Value)
            .Distinct()
            .ToList();
        if (vehicleIds.Count == 0) return new Dictionary<Guid, VehicleLiveStatus>();

        try
        {
            var vehicles = await db.Vehicles.AsNoTracking()
                .Where(vehicle => vehicleIds.Contains(vehicle.Id))
                .ToListAsync(ct);
            var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles, ct);
            var freshSince = now.AddMinutes(-5);
            var statuses = await db.VehicleLiveStatuses.AsNoTracking()
                .Where(status => status.LastReceivedAtUtc >= freshSince)
                .ToListAsync(ct);

            var result = new Dictionary<Guid, VehicleLiveStatus>();
            foreach (var vehicle in vehicles)
            {
                if (!aliasesByVehicle.TryGetValue(vehicle.Id, out var aliases)) continue;
                var live = ExecutionIdentityResolver.MatchLive(aliases, statuses);
                if (live is not null) result[vehicle.Id] = live;
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Fresh RoadTech SQL position could not be resolved for live ETA rebasing; geofence departure fallback will remain active.");
            return new Dictionary<Guid, VehicleLiveStatus>();
        }
    }

    private static (decimal Longitude, decimal Latitude)? Origin(
        LoadStop? stop,
        EmbeddedFence fence,
        PlannerSourceMasterDataResolver? masterData) =>
        stop is null
            ? OperationalRunOrigin.FenceCentre(fence)
            : OperationalStopCoordinates.Resolve(stop, masterData) ?? OperationalRunOrigin.FenceCentre(fence);

    private static int PredictedDwellMinutes(EmbeddedGeofenceSnapshot snapshot, LoadStop stop, int fallbackMinutes)
    {
        var samples = snapshot.Visits
            .Where(visit => visit.ExitedAtUtc is not null && GeofencePlanningMatch.SamePhysicalSite(stop, visit.Fence))
            .Select(visit => (int)Math.Round((visit.ExitedAtUtc!.Value - visit.EnteredAtUtc).TotalMinutes))
            .Where(minutes => minutes >= 2 && minutes <= 180)
            .OrderBy(minutes => minutes)
            .ToList();
        if (samples.Count == 0) return fallbackMinutes;
        var middle = samples.Count / 2;
        return samples.Count % 2 == 1
            ? samples[middle]
            : (int)Math.Round((samples[middle - 1] + samples[middle]) / 2d);
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}

public sealed record RunTimingResponse(DateOnly PlanningDate, DateTimeOffset CalculatedAtUtc, bool GeofenceAvailable, IReadOnlyList<RunTimingRecord> Records);
public sealed record RunTimingRecord(
    Guid LoadId,
    string LoadReference,
    bool Completed,
    Guid? NextStopId,
    int? NextStopSequence,
    string? NextStopName,
    DateTimeOffset? NextEtaUtc,
    string EtaSource,
    DateTimeOffset? FinalEtaUtc,
    string FinalEtaSource,
    Guid? FinalDestinationStopId,
    string? FinalDestinationName,
    DateTimeOffset? PreviousGeofenceDepartureUtc,
    DateTimeOffset? DwellStartedAtUtc,
    string? CurrentGeofenceName,
    string? EtaUnavailableStopName,
    string? EtaUnavailableReason,
    int PreRouteDwellMinutes,
    IReadOnlyList<RunTimingLeg> EtaLegs,
    string RouteAnchorSource,
    IReadOnlyList<RunTimingEvidenceGap> EvidenceGaps);

public sealed record RunTimingLeg(
    Guid StopId,
    int Sequence,
    string StopName,
    int TravelMinutes,
    int DwellMinutesAfterArrival,
    DateTimeOffset ArrivalEtaUtc,
    bool Approximate,
    string Provider);

public sealed record RunTimingEvidenceGap(Guid StopId, int Sequence, string StopName);
