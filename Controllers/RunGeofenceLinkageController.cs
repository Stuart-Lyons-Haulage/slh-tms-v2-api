using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning/geofence-linkage")]
[Authorize(Policy = "TmsAccess")]
public sealed class RunGeofenceLinkageController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly date, CancellationToken ct)
    {
        // Use the same merged planning source and reconstructed + durable RoadTech evidence
        // as Run Progress. Build evidence against the full order-level plan first, then collapse
        // the display journey to physical visits so two orders handled at Selsey/Runcton/etc.
        // do not appear as two separate wallboard stops.
        var loads = (await PlanningResilience.ReadLoadsAsync(db, date, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();

        await RunOperationalStore.EnrichAsync(db, loads, ct);
        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        var geofenceLoads = GeofencePlanningMatch.PrepareLoads(loads);
        var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, date, geofenceLoads, ct);
        snapshot = await EmbeddedGeofenceEvidenceMerge.MergeDurableProjectionAsync(db, snapshot, loads, ct);
        var visits = snapshot.Visits
            .Where(visit => visit.LoadId is not null)
            .OrderBy(visit => visit.EnteredAtUtc)
            .ToList();

        // The wallboard is a vehicle-visit view, not an order-line view. Collapse only after
        // evidence has been reconstructed so historic visits attached to any of the original
        // duplicate order stop IDs remain available to the representative physical stop.
        WallboardPhysicalStops.Apply(loads);

        var rows = loads
            .OrderBy(load => load.Stops.Where(stop => stop.PlannedArrivalUtc is not null)
                .Select(stop => stop.PlannedArrivalUtc)
                .Min() ?? DateTimeOffset.MaxValue)
            .ThenBy(load => load.Reference)
            .SelectMany(load =>
            {
                var stops = OperationalStopOrdering.Order(load.Stops);
                var finalSequence = stops.Count;
                var displayRun = RunDisplayLabel.For(load);

                return stops.Select((stop, index) =>
                {
                    var resolution = resolver.Resolve(stop.Name);
                    var stopVisits = visits
                        .Where(visit => visit.LoadId == load.Id)
                        .Where(visit => visit.LoadStopId == stop.Id || GeofencePlanningMatch.SamePhysicalSite(stop, visit.Fence))
                        .OrderBy(visit => visit.EnteredAtUtc)
                        .ToList();
                    var latestVisit = stopVisits.LastOrDefault();
                    var issue = !resolution.SiteMatched
                        ? "SiteNameNotResolved"
                        : !resolution.GeofenceLinked
                            ? "SiteMatchedGeofenceUnlinked"
                            : null;
                    var operationalSequence = index + 1;

                    return new
                    {
                        loadId = load.Id,
                        run = displayRun,
                        vehicleId = load.VehicleId,
                        stopId = stop.Id,
                        sequence = operationalSequence,
                        stopName = stop.Name,
                        finalDelivery = operationalSequence == finalSequence,
                        siteMatched = resolution.SiteMatched,
                        siteCode = resolution.SiteNumber,
                        siteName = resolution.SiteName,
                        geofenceLinked = resolution.GeofenceLinked,
                        geofenceName = latestVisit?.Fence.Name ?? resolution.GeofenceName,
                        issue,
                        visitRecorded = latestVisit is not null,
                        latestEnterUtc = latestVisit?.EnteredAtUtc,
                        latestExitUtc = latestVisit?.ExitedAtUtc,
                        confirmedAtUtc = latestVisit?.ConfirmedAtUtc,
                        evidence = resolution.EvidenceNote
                    };
                });
            })
            .ToList();

        var issues = rows.Where(row => row.issue is not null).ToList();
        var hitRuns = rows.Where(row => row.visitRecorded).Select(row => row.loadId).Distinct().Count();
        return Ok(new
        {
            planningDate = date,
            runs = loads.Count,
            stops = rows.Count,
            siteNameUnresolved = issues.Count(row => row.issue == "SiteNameNotResolved"),
            siteMatchedButGeofenceUnlinked = issues.Count(row => row.issue == "SiteMatchedGeofenceUnlinked"),
            linkedStops = rows.Count(row => row.siteMatched && row.geofenceLinked),
            stopsWithVisitEvidence = rows.Count(row => row.visitRecorded),
            runsWithVisitEvidence = hitRuns,
            issues,
            records = rows,
            checkedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
