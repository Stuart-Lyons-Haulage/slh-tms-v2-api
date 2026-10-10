using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class CustomerNotificationsController(CustomerNotificationService notifications) : ControllerBase
{
    [HttpPost("morning-briefing/preview")]
    public async Task<ActionResult<MorningBriefingPreview>> MorningBriefingPreview([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await notifications.PreviewMorningBriefingAsync(date, ct));

    [HttpGet("log")]
    public async Task<IActionResult> NotificationLog(
        [FromQuery] Guid customerId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken ct)
    {
        if (to < from) return BadRequest(new { error = "The 'to' date must be on or after the 'from' date." });
        return Ok(await notifications.ReadLogAsync(customerId, from, to, ct));
    }
}
