using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/tracking/state")]
[Authorize]
public sealed class TrackingStateController(
    DotTrackingClient trackingClient,
    DotTrackingOptions options,
    IConfiguration configuration,
    ILogger<TrackingStateController> logger) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var capturedAtUtc = trackingClient.LiveSnapshotCapturedAtUtc;
            var staleAfter = TimeSpan.FromMinutes(Math.Max(2, options.StaleAfterMinutes));

            // Availability follows the central ingestion snapshot rather than performing an
            // independent RoadTech probe. This keeps diagnostics aligned with exactly the same
            // provider read consumed by wallboards, ETA, dispatch and operations.
            if (capturedAtUtc is null || checkedAtUtc - capturedAtUtc.Value > staleAfter)
            {
                return Ok(new
                {
                    trackingState = "Unavailable",
                    checkedAtUtc,
                    provider = "RoadTech",
                    recordCount = (int?)null,
                    snapshotCapturedAtUtc = capturedAtUtc,
                    warning = capturedAtUtc is null
                        ? "RoadTech tracking has not produced a successful central snapshot yet. Stored TMS planning and tracking evidence remains authoritative until the provider recovers."
                        : "The central RoadTech snapshot is stale. Stored TMS planning and tracking evidence remains authoritative until the provider recovers."
                });
            }

            var records = await trackingClient.GetLatestVehicleEventsAsync(ct);
            return Ok(new
            {
                trackingState = "Available",
                checkedAtUtc,
                provider = "RoadTech",
                recordCount = records.Count,
                snapshotCapturedAtUtc = capturedAtUtc,
                snapshotAgeSeconds = Math.Round((checkedAtUtc - capturedAtUtc.Value).TotalSeconds)
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RoadTech central snapshot availability check failed.");
            return Ok(new
            {
                trackingState = "Unavailable",
                checkedAtUtc,
                provider = "RoadTech",
                recordCount = (int?)null,
                warning = "RoadTech tracking is temporarily unavailable. Stored TMS planning and tracking evidence remains authoritative until the provider recovers."
            });
        }
    }
}
