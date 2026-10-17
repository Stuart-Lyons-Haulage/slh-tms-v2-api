using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/tracking/geofences")]
public sealed class TrackingGeofenceHealthController(
    TmsDbContext db,
    IServiceProvider services,
    DotTrackingOptions options,
    ILogger<TrackingGeofenceHealthController> logger) : ControllerBase
{
    private static readonly TimeSpan EvidenceWindow = TimeSpan.FromHours(24);

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Summary(CancellationToken ct)
    {
        var result = await BuildAsync(includeDetails: false, ct);
        return result.Error is null
            ? Ok(result.Summary)
            : StatusCode(StatusCodes.Status503ServiceUnavailable, result.Error);
    }

    [HttpGet("vehicles")]
    [Authorize(Policy = "TmsAccess")]
    public async Task<IActionResult> Vehicles(CancellationToken ct)
    {
        var result = await BuildAsync(includeDetails: true, ct);
        return result.Error is null
            ? Ok(new { result.Summary, vehicles = result.Vehicles })
            : StatusCode(StatusCodes.Status503ServiceUnavailable, result.Error);
    }

    private async Task<BuildResult> BuildAsync(bool includeDetails, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (!options.IsConfigured)
        {
            return new BuildResult(null, [], new
            {
                status = "unconfigured",
                configured = false,
                checkedAtUtc = now,
                message = "RoadTech tracking is not configured."
            });
        }

        try
        {
            var trackingClient = services.GetRequiredService<DotTrackingClient>();
            var providerRows = await trackingClient.GetLatestVehicleEventsAsync(ct);
            var latestByIdentifier = providerRows
                .Select(DotTelemetryRecord.FromProvider)
                .Where(record => record.Latitude is not null && record.Longitude is not null)
                .GroupBy(record => Normalise(record.VehicleIdentifier), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
                .OrderBy(record => record.VehicleIdentifier)
                .ToList();

            var since = now - EvidenceWindow;
            var storedTrackingEvents24h = await db.VehicleTrackingEvents.AsNoTracking()
                .CountAsync(item => item.ProviderName == "RoadTech Falcon" && item.EventTimeUtc >= since, ct);
            var latestStoredTrackingEventUtc = await db.VehicleTrackingEvents.AsNoTracking()
                .Where(item => item.ProviderName == "RoadTech Falcon")
                .OrderByDescending(item => item.EventTimeUtc)
                .Select(item => (DateTimeOffset?)item.EventTimeUtc)
                .FirstOrDefaultAsync(ct);

            var visits = await db.GeofenceVisits.AsNoTracking()
                .Where(visit => visit.EnteredAtUtc >= since || visit.ExitedAtUtc >= since || visit.ExitedAtUtc == null)
                .OrderByDescending(visit => visit.EnteredAtUtc)
                .ToListAsync(ct);

            var latestVisitByIdentifier = visits
                .GroupBy(visit => Normalise(visit.VehicleIdentifier), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group
                    .OrderByDescending(visit => LatestEvent(visit))
                    .First(), StringComparer.OrdinalIgnoreCase);

            var trackerIdentifiers = latestByIdentifier.Select(record => Normalise(record.VehicleIdentifier)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var withEvidence = trackerIdentifiers.Count(identifier => latestVisitByIdentifier.ContainsKey(identifier));
            var activeVisits = latestVisitByIdentifier.Values.Count(visit => visit.ExitedAtUtc is null);
            var latestGeofenceEventUtc = visits.Count == 0 ? (DateTimeOffset?)null : visits.Max(LatestEvent);

            var summary = new
            {
                status = latestByIdentifier.Count == 0 ? "attention" : withEvidence == latestByIdentifier.Count ? "healthy" : "attention",
                configured = true,
                evidenceWindowHours = (int)EvidenceWindow.TotalHours,
                providerVehicles = latestByIdentifier.Count,
                storedTrackingEvents24h,
                latestStoredTrackingEventUtc,
                vehiclesWithGeofenceEvidence24h = withEvidence,
                vehiclesWithoutGeofenceEvidence24h = Math.Max(0, latestByIdentifier.Count - withEvidence),
                activeGeofenceVisits = activeVisits,
                newestProviderEventUtc = latestByIdentifier.Count == 0 ? (DateTimeOffset?)null : latestByIdentifier.Max(record => record.EventTimeUtc),
                latestGeofenceEventUtc,
                checkedAtUtc = now,
                note = "Provider freshness, stored tracking freshness and geofence evidence are reported separately so ingestion faults can be distinguished from missing fence crossings."
            };

            if (!includeDetails) return new BuildResult(summary, [], null);

            var vehicles = await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).ToListAsync(ct);
            var masterByAlias = new Dictionary<string, Vehicle>(StringComparer.OrdinalIgnoreCase);
            foreach (var vehicle in vehicles)
            {
                foreach (var alias in new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation })
                {
                    if (string.IsNullOrWhiteSpace(alias)) continue;
                    var key = Normalise(alias);
                    if (!masterByAlias.ContainsKey(key)) masterByAlias[key] = vehicle;
                }
            }

            var fenceIds = latestVisitByIdentifier.Values.Select(visit => visit.GeofenceId).Distinct().ToList();
            var fences = await db.SiteGeofences.AsNoTracking().Where(fence => fenceIds.Contains(fence.Id)).ToDictionaryAsync(fence => fence.Id, ct);

            var rows = latestByIdentifier.Select(record =>
            {
                var key = Normalise(record.VehicleIdentifier);
                latestVisitByIdentifier.TryGetValue(key, out var visit);
                masterByAlias.TryGetValue(key, out var vehicle);
                SiteGeofence? fence = null;
                if (visit is not null) fences.TryGetValue(visit.GeofenceId, out fence);

                var health = vehicle is null ? "UnmatchedMasterVehicle"
                    : visit?.ExitedAtUtc is null && visit is not null ? "AtGeofence"
                    : visit is not null ? "RecentGeofenceEvent"
                    : "NoGeofenceEvent24h";

                return new
                {
                    trackerVehicle = record.VehicleIdentifier,
                    masterRegistration = vehicle?.Registration,
                    masterVehicleId = vehicle?.Id,
                    health,
                    latestGpsUtc = record.EventTimeUtc,
                    gpsAgeMinutes = Math.Max(0, Math.Round((now - record.EventTimeUtc).TotalMinutes, 1)),
                    record.Latitude,
                    record.Longitude,
                    lastGeofenceEnterUtc = visit?.EnteredAtUtc,
                    lastGeofenceExitUtc = visit?.ExitedAtUtc,
                    geofenceStatus = visit?.Status,
                    geofenceName = fence?.Name,
                    siteReferenceNumber = fence?.SiteNumber,
                    siteId = fence?.SiteId
                };
            }).ToList();

            return new BuildResult(summary, rows.Cast<object>().ToList(), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Vehicle geofence health check failed.");
            return new BuildResult(null, [], new
            {
                status = "upstream-failure",
                configured = true,
                error = exception.GetType().Name,
                message = exception.Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    private static DateTimeOffset LatestEvent(GeofenceVisit visit) => visit.ExitedAtUtc ?? visit.LastInsideAtUtc;
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record BuildResult(object? Summary, List<object> Vehicles, object? Error);
}
