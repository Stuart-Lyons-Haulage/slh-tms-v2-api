using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/dispatch")]
[Authorize]
public sealed class BackloadDispatchController(BackloadOperationsService service) : ControllerBase
{
    [HttpPost("accept-backload")]
    public async Task<ActionResult<AcceptBackloadResult>> AcceptBackload([FromBody] AcceptBackloadRequest request, CancellationToken ct)
    {
        try
        {
            var actor = User.Identity?.Name ?? "TMS dispatcher";
            return Ok(await service.AcceptAsync(request, actor, ct));
        }
        catch (KeyNotFoundException exception) { return NotFound(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return Conflict(new { error = exception.Message }); }
    }

    [HttpPost("decline-backload")]
    public async Task<IActionResult> DeclineBackload([FromBody] DeclineBackloadRequest request, CancellationToken ct)
    {
        try
        {
            var actor = User.Identity?.Name ?? "TMS dispatcher";
            await service.RecordDeclineAsync(request, actor, ct);
            return Ok(new { declined = true });
        }
        catch (KeyNotFoundException exception) { return NotFound(new { error = exception.Message }); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
    }
}
