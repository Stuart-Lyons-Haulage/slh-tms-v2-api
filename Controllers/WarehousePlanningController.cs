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

    /// <summary>Warehouse confirms what was actually received or loaded for a movement.</summary>
    [HttpPost("checks"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RecordCheck([FromBody] WarehouseCheckRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MovementKey)) return BadRequest(new { message = "movementKey is required." });
        if (request.ActualQuantity < 0 || request.ActualQuantity > 999) return BadRequest(new { message = "Enter the quantity actually handled (0-999)." });
        var user = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? User.FindFirst("oid")?.Value;
        return Ok(await service.RecordCheckAsync(request, user, ct));
    }
}
