using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/site-geofence-sync")]
[Authorize]
public sealed class SiteGeofenceSyncController(TmsDbContext db, ILogger<SiteGeofenceSyncController> logger) : ControllerBase
{
    [HttpGet("sites")]
    public async Task<IActionResult> Sites(CancellationToken ct)
        => Ok(await SiteGeofenceMasterSync.GetStatusAsync(db, ct));

    [HttpPost("sync-sites"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> SyncSites(CancellationToken ct)
    {
        try
        {
            return Ok(await SiteGeofenceMasterSync.SyncAsync(db, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Site/geofence master sync failed.");
            db.ChangeTracker.Clear();
            var sites = await SiteGeofenceMasterSync.GetStatusAsync(db, ct);
            return Ok(new SiteGeofenceSyncResult(
                0,
                0,
                0,
                0,
                sites.Count(x => x.NeedsReview),
                sites,
                new[] { $"Site/geofence sync could not complete automatically: {ex.GetBaseException().Message}" }));
        }
    }

    [HttpPost("geofences/{id:guid}/link"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> LinkGeofence(Guid id, LinkGeofenceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.SiteCode))
            return BadRequest(new { error = "A canonical SITE### code is required." });

        try
        {
            return Ok(await SiteGeofenceMasterSync.LinkGeofenceAsync(db, id, request.SiteCode, ct));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public sealed record LinkGeofenceRequest(string SiteCode);
