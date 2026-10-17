using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/geofence-history")]
[Authorize]
public sealed class GeofenceHistoryReplayController(
    TmsDbContext db,
    DotTrackingClient client,
    DotTrackingTelemetryStore store,
    ILogger<GeofenceHistoryReplayService> logger) : ControllerBase
{
    [HttpPost("rebuild-today")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<ActionResult<GeofenceHistoryReplayResult>> RebuildToday(CancellationToken ct)
    {
        var today = UkDate(DateTimeOffset.UtcNow);
        return Ok(await Replay().ReplayAsync(today, ct));
    }

    [HttpPost("rebuild")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<ActionResult<GeofenceHistoryReplayResult>> Rebuild([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await Replay().ReplayAsync(date, ct));

    private GeofenceHistoryReplayService Replay() => new(client, store, db, logger);

    private static DateOnly UkDate(DateTimeOffset utcNow)
    {
        try
        {
            var local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utcNow, "Europe/London");
            return DateOnly.FromDateTime(local.DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(utcNow.UtcDateTime);
        }
    }
}
