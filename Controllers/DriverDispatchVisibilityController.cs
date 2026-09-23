using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/dispatch/driver-visibility")]
[Authorize]
public sealed class DriverDispatchVisibilityController(
    TmsDbContext db,
    ILogger<DriverDispatchVisibilityController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet]
    public async Task<ActionResult<DriverDispatchVisibilitySnapshot>> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        return Ok(await DriverDispatchVisibilityStore.ReadAsync(db, planningDate, logger, ct));
    }
}