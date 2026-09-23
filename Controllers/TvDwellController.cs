using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Read-only current geofence dwell evidence for the office wallboard.
/// Kept separate from route-progress so the proven RoadTech live-position path
/// remains unchanged while the TV can prioritise excessive on-site dwell.
/// </summary>
[ApiController, Route("api/v1/tv-display/dwell")]
public sealed class TvDwellController(
    TmsDbContext db,
    IConfiguration configuration,
    ILogger<TvDwellController> logger) : ControllerBase
{
    private static readonly TimeSpan ExcessiveDwell = TimeSpan.FromHours(1);

    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(displayKey) && Request.Query.TryGetValue("key", out var queryKey))
            displayKey = queryKey.FirstOrDefault();
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

        try
        {
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(
                db,
                day,
                GeofencePlanningMatch.PrepareLoads(loads),
                ct);

            var rows = snapshot.ActiveVisits
                .Where(visit => visit.LoadId is not null)
                .GroupBy(visit => visit.LoadId!.Value)
                .Select(group => group.OrderByDescending(visit => visit.EnteredAtUtc).First())
                .Select(visit =>
                {
                    var dwell = now - visit.EnteredAtUtc;
                    var minutes = Math.Max(0, (int)Math.Floor(dwell.TotalMinutes));
                    return new
                    {
                        loadId = visit.LoadId!.Value,
                        loadStopId = visit.LoadStopId,
                        geofenceName = visit.Fence.Name,
                        onSiteSinceUtc = visit.EnteredAtUtc,
                        dwellMinutes = minutes,
                        dwellOverOneHour = dwell >= ExcessiveDwell,
                        confirmed = visit.ConfirmedAtUtc is not null
                    };
                })
                .OrderByDescending(row => row.dwellMinutes)
                .ToList();

            return Ok(new
            {
                planningDate = day,
                calculatedAtUtc = now,
                thresholdMinutes = (int)ExcessiveDwell.TotalMinutes,
                activeVisitCount = rows.Count,
                excessiveDwellCount = rows.Count(row => row.dwellOverOneHour),
                runs = rows
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TV dwell evidence could not be reconstructed from geofence history.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                planningDate = day,
                calculatedAtUtc = now,
                thresholdMinutes = (int)ExcessiveDwell.TotalMinutes,
                activeVisitCount = 0,
                excessiveDwellCount = 0,
                runs = Array.Empty<object>(),
                message = "Geofence dwell evidence is temporarily unavailable."
            });
        }
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
