using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/warehouse"), Authorize]
public sealed class WarehousePlanningController(WarehouseMovementService service) : ControllerBase
{
    [HttpGet("daily")]
    public async Task<IActionResult> Daily([FromQuery] DateOnly date, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        return Ok(await service.BuildDailyAsync(date, ct));
    }
}
