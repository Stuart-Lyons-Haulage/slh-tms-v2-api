using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofence-recovery")]
[Authorize]
public sealed class GeofenceRecoveryController(TmsDbContext db, ILogger<GeofenceRecoveryController> logger) : ControllerBase
{
    [HttpPost("ensure"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Ensure(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var day = UkOperatingDate(now);
            List<Load> loads;
            try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, day, ct); }
            catch { loads = []; db.ChangeTracker.Clear(); }

            var statuses = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, day, loads, ct);
            var linked = statuses.Count(x => x.SiteId != null);

            logger.LogInformation(
                "DDL-free geofence recovery checked {FenceCount} approved fences, {LinkedCount} site links, {VisitCount} derived visit(s), {TrackingCount} tracking event(s).",
                statuses.Count, linked, snapshot.Visits.Count, snapshot.TrackingEventCount);

            return Ok(new
            {
                ready = statuses.Count > 0,
                activeBefore = statuses.Count,
                activeAfter = statuses.Count,
                seeded = 0,
                siteMatched = linked,
                linked,
                visitsBefore = snapshot.Visits.Count,
                visitsAfter = snapshot.Visits.Count,
                replayedTrackingEvents = snapshot.TrackingEventCount,
                source = "EmbeddedSLHGeofences+RoadTechTracking",
                recoveredAtUtc = now
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "DDL-free geofence recovery check failed.");
            db.ChangeTracker.Clear();
            return Ok(new
            {
                ready = EmbeddedGeofenceEngine.ApprovedFences.Count > 0,
                activeAfter = EmbeddedGeofenceEngine.ApprovedFences.Count,
                source = "EmbeddedSLHGeofencesSafeFallback",
                warning = exception.GetBaseException().Message,
                recoveredAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
