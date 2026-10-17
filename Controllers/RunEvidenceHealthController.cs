using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Cross-system operational evidence for today's planned runs. This endpoint is
/// deliberately evidence-focused: a green configuration check is not enough to
/// prove that RoadTech geofences or Tacho/Falcon driver identities are actually
/// matching the runs shown on the wallboards.
/// </summary>
[ApiController, Route("api/v1/health/run-evidence")]
[Authorize]
public sealed class RunEvidenceHealthController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<RunEvidenceHealthController> logger,
    IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();
        var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, geofenceLoads, ct);
        snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);
        var geofenceCoverage = await RunGeofenceConfigurationCoverage.CalculateAsync(db, loads, ct);
        var tacho = await RunTachoEvidenceResolver.ResolveAsync(db, tachoMaster, loads, planningDate, logger, ct);
        var trackingCoverageResult = await TrackingCoverageAsync(loads, planningDate, ct);
        var trackingCoverage = trackingCoverageResult.Vehicles;

        var runEvidence = loads.Select(load =>
        {
            var stops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
            var visits = snapshot.Visits
                .Where(visit => visit.LoadId == load.Id)
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToList();
            var departures = visits.Where(visit => visit.ExitedAtUtc is not null).ToList();
            var finalStop = stops.LastOrDefault();
            var finalVisit = finalStop is null
                ? null
                : visits.Where(visit => visit.LoadStopId == finalStop.Id)
                    .OrderByDescending(visit => visit.EnteredAtUtc)
                    .FirstOrDefault();
            var tachoEvidence = tacho.ByLoadId.GetValueOrDefault(load.Id);
            var completionEvidence = finalVisit is null ? "None" : "FinalGeofenceArrival";
            var progressEvidence = visits.Count == 0
                ? "None"
                : departures.Count > 0
                    ? "GeofenceDeparture"
                    : "GeofenceArrival";

            return new
            {
                loadId = load.Id,
                loadReference = load.Reference,
                totalStops = stops.Count,
                geofenceVisits = visits.Count,
                geofenceDepartures = departures.Count,
                lastGeofenceEvidenceUtc = visits
                    .Select(visit => visit.ExitedAtUtc ?? visit.LastInsideAtUtc)
                    .OrderByDescending(value => value)
                    .FirstOrDefault(),
                finalStopName = finalStop?.Name,
                finalStopArrivalUtc = finalVisit?.EnteredAtUtc,
                finalStopDepartureUtc = finalVisit?.ExitedAtUtc,
                completionEvidence,
                progressEvidence,
                tachoStatus = tachoEvidence?.Status ?? "Unknown",
                tachoSource = tachoEvidence?.EvidenceSource,
                tachoExplanation = tachoEvidence?.Explanation
            };
        }).ToList();

        var prioritySites = new[] { "Lake Lane", "NWF Selsey", "NWF Runcton", "NWF Merston", "NWF Drayton" }
            .Select(site =>
            {
                var visits = snapshot.Visits.Where(visit => CanonicalFenceName(visit.Fence.Name) == site).ToList();
                return new
                {
                    site,
                    plannedStops = PlannedStops(loads, site),
                    visits = visits.Count,
                    linkedVisits = visits.Count(visit => visit.LoadStopId is not null),
                    departures = visits.Count(visit => visit.ExitedAtUtc is not null),
                    latestEvidenceUtc = visits
                        .Select(visit => (DateTimeOffset?)(visit.ExitedAtUtc ?? visit.LastInsideAtUtc))
                        .OrderByDescending(value => value)
                        .FirstOrDefault()
                };
            })
            .ToList();

        var statusCounts = tacho.StatusCounts ?? new Dictionary<string, int>();
        var completedRuns = runEvidence.Count(run => run.completionEvidence == "FinalGeofenceArrival");
        var runsWithProgress = runEvidence.Count(run => run.geofenceVisits > 0);
        var runsWithDeparture = runEvidence.Count(run => run.geofenceDepartures > 0);
        var hitRunCount = snapshot.Visits
            .Where(visit => visit.LoadId is not null)
            .Select(visit => visit.LoadId!.Value)
            .Distinct()
            .Count();
        var hitStopCount = snapshot.Visits
            .Where(visit => visit.LoadStopId is not null)
            .Select(visit => visit.LoadStopId!.Value)
            .Distinct()
            .Count();
        var evidenceGapRuns = runEvidence
            .Where(run => run.geofenceVisits == 0 || run.tachoStatus is "NoTachoDuty" or "Mismatch" or "Unavailable")
            .Select(run => new
            {
                run.loadReference,
                run.geofenceVisits,
                run.geofenceDepartures,
                run.progressEvidence,
                run.tachoStatus,
                run.tachoSource,
                run.tachoExplanation
            })
            .ToList();

        return Ok(new
        {
            planningDate,
            checkedAtUtc = DateTimeOffset.UtcNow,
            source = "PlanningResilience+RoadTechFalcon+SiteMasterSqlGeofences+DurableGeofenceVisits+TachoMaster",
            tracking = new
            {
                observationCount = snapshot.TrackingEventCount,
                snapshot.LatestTrackingUtc,
                plannedVehicleCount = trackingCoverage.Count,
                vehiclesWithMultipleSamples = trackingCoverage.Count(item => item.SampleCount > 1),
                vehiclesWithSingleSample = trackingCoverage.Count(item => item.SampleCount == 1),
                vehiclesWithNoSamples = trackingCoverage.Count(item => item.SampleCount == 0),
                coverageWarning = trackingCoverageResult.Warning,
                vehicleCoverage = trackingCoverage
            },
            geofences = new
            {
                activeFenceCount = geofenceCoverage.ActiveGeofenceCount,
                approvedFenceCount = snapshot.Fences.Count,
                configuredLinkedRunCount = geofenceCoverage.LinkedRuns,
                configuredLinkedStopCount = geofenceCoverage.LinkedStops,
                totalPlannedStops = geofenceCoverage.TotalStops,
                linkedRunCount = geofenceCoverage.LinkedRuns,
                hitRunCount,
                hitStopCount,
                visitCount = snapshot.Visits.Count,
                departureCount = snapshot.Visits.Count(visit => visit.ExitedAtUtc is not null),
                runsWithProgress,
                runsWithDeparture,
                completedRuns,
                plannedRuns = loads.Count
            },
            tacho = new
            {
                configured = tachoMaster.IsConfigured,
                tacho.Available,
                tacho.Warning,
                providerVehicles = tacho.ProviderVehicles,
                providerEvidenceRecords = tacho.ProviderEvidenceRecords,
                tachoDutyRecords = tacho.TachoDutyRecords,
                falconCardRecords = tacho.FalconCardRecords,
                statusCounts
            },
            prioritySites,
            evidenceGapCount = evidenceGapRuns.Count,
            evidenceGaps = evidenceGapRuns
        });
    }

    private async Task<TrackingCoverageResult> TrackingCoverageAsync(
        IReadOnlyCollection<Load> loads,
        DateOnly planningDate,
        CancellationToken ct)
    {
        var vehicleIds = loads
            .Where(load => load.VehicleId is not null)
            .Select(load => load.VehicleId!.Value)
            .Distinct()
            .ToList();
        if (vehicleIds.Count == 0) return new TrackingCoverageResult([], null);

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(vehicle => vehicleIds.Contains(vehicle.Id))
            .ToListAsync(ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles, ct);
        var (fromUtc, toUtc) = OperatingWindowUtc(planningDate);

        List<TrackingStoredSample> storedEvents;
        string? warning = null;
        try
        {
            // Keep this query bounded to the identifiers that belong to the vehicles on
            // the resilient current-day run set. This avoids a fleet-wide day scan while
            // retaining the same canonical alias matching used by live execution.
            var plannedIdentifiers = aliasesByVehicle.Values
                .SelectMany(aliases => aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            storedEvents = plannedIdentifiers.Count == 0
                ? []
                : await db.VehicleTrackingEvents.AsNoTracking()
                    .Where(item => item.EventTimeUtc >= fromUtc && item.EventTimeUtc < toUtc &&
                                   plannedIdentifiers.Contains(item.VehicleIdentifier))
                    .OrderBy(item => item.EventTimeUtc)
                    .Select(item => new TrackingStoredSample(
                        item.VehicleIdentifier,
                        item.EventTimeUtc,
                        item.Latitude,
                        item.Longitude))
                    .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "RoadTech stored-sample coverage query failed; returning live receipt evidence without pretending stored history is healthy.");
            storedEvents = [];
            warning = "Stored RoadTech sample coverage could not be read. Live receipt freshness remains visible, but history/geofence confidence is incomplete.";
        }

        List<TrackingLiveSample> liveStatuses;
        try
        {
            liveStatuses = await db.VehicleLiveStatuses.AsNoTracking()
                .Select(item => new TrackingLiveSample(
                    item.VehicleIdentifier,
                    item.LastEventTimeUtc,
                    item.LastReceivedAtUtc))
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "RoadTech live-status coverage query failed while building cross-system evidence.");
            liveStatuses = [];
            warning = warning is null
                ? "RoadTech live receipt coverage could not be read; tracking confidence is incomplete."
                : warning + " Live receipt coverage was also unavailable.";
        }

        var coverage = vehicles
            .OrderBy(vehicle => vehicle.Registration)
            .Select(vehicle =>
            {
                var vehicleAliases = aliasesByVehicle.GetValueOrDefault(vehicle.Id)
                    ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var events = storedEvents
                    .Where(item => ExecutionIdentityResolver.MatchesVehicleIdentifier(vehicleAliases, item.VehicleIdentifier))
                    .ToList();
                var live = liveStatuses
                    .Where(item => ExecutionIdentityResolver.MatchesVehicleIdentifier(vehicleAliases, item.VehicleIdentifier))
                    .OrderByDescending(item => item.LastReceivedAtUtc)
                    .FirstOrDefault();
                var runRefs = loads
                    .Where(load => load.VehicleId == vehicle.Id)
                    .Select(load => load.Reference)
                    .Where(reference => !string.IsNullOrWhiteSpace(reference))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(reference => reference)
                    .ToList();
                var distinctPositionCount = events
                    .Select(item => (item.Latitude, item.Longitude))
                    .Distinct()
                    .Count();

                return new TrackingVehicleCoverage(
                    vehicle.Id,
                    vehicle.Registration,
                    runRefs,
                    events.Count,
                    distinctPositionCount,
                    events.FirstOrDefault()?.EventTimeUtc,
                    events.LastOrDefault()?.EventTimeUtc,
                    live?.LastEventTimeUtc,
                    live?.LastReceivedAtUtc,
                    warning is not null && events.Count == 0
                        ? "CoverageUnavailable"
                        : events.Count == 0
                            ? "NoStoredSamples"
                            : events.Count == 1
                                ? "SingleProviderSample"
                                : "SamplesAccumulating");
            })
            .ToList();

        return new TrackingCoverageResult(coverage, warning);
    }

    private static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) OperatingWindowUtc(DateOnly planningDate)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var localStart = planningDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            var localEnd = planningDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return (
                new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, zone), TimeSpan.Zero),
                new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localEnd, zone), TimeSpan.Zero));
        }
        catch (TimeZoneNotFoundException)
        {
            return (
                new DateTimeOffset(planningDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
                new DateTimeOffset(planningDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)));
        }
    }

    private static int PlannedStops(IEnumerable<Load> loads, string site)
    {
        if (site == "Lake Lane") return 0;
        var locality = site.Replace("NWF ", string.Empty, StringComparison.OrdinalIgnoreCase);
        return loads.SelectMany(load => load.Stops ?? [])
            .Count(stop => stop.Name.Contains(locality, StringComparison.OrdinalIgnoreCase));
    }

    private static string? CanonicalFenceName(string value)
    {
        var normalized = value.ToUpperInvariant();
        if (normalized.Contains("LAKE LANE")) return "Lake Lane";
        if (normalized.Contains("SELSEY")) return "NWF Selsey";
        if (normalized.Contains("RUNCTON")) return "NWF Runcton";
        if (normalized.Contains("MERSTON")) return "NWF Merston";
        if (normalized.Contains("DRAYTON")) return "NWF Drayton";
        return null;
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

public sealed record TrackingVehicleCoverage(
    Guid VehicleId,
    string Registration,
    IReadOnlyList<string> RunReferences,
    int SampleCount,
    int DistinctPositionCount,
    DateTimeOffset? FirstEventUtc,
    DateTimeOffset? LastEventUtc,
    DateTimeOffset? LatestProviderEventUtc,
    DateTimeOffset? LatestReceiptUtc,
    string CaptureStatus);

internal sealed record TrackingCoverageResult(
    IReadOnlyList<TrackingVehicleCoverage> Vehicles,
    string? Warning);

internal sealed record TrackingStoredSample(
    string VehicleIdentifier,
    DateTimeOffset EventTimeUtc,
    decimal Latitude,
    decimal Longitude);

internal sealed record TrackingLiveSample(
    string VehicleIdentifier,
    DateTimeOffset LastEventTimeUtc,
    DateTimeOffset LastReceivedAtUtc);
