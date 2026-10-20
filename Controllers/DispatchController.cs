using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/dispatch")]
[Authorize]
public sealed class DispatchController(DispatchService dispatch) : ControllerBase
{

    [HttpGet("drivers")]
    public async Task<ActionResult<IReadOnlyList<DispatchDriverDto>>> Drivers([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await dispatch.GetDriversAsync(date, ct));

    [HttpGet("runs")]
    public async Task<ActionResult<IReadOnlyList<DispatchRunDto>>> Runs([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await dispatch.GetRunsAsync(date, ct));

    [HttpPost("available-times")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<ActionResult<IReadOnlyList<DispatchAvailableTimeDto>>> AvailableTimes(
        DispatchAvailableTimesRequest request,
        CancellationToken ct) =>
        Ok(await dispatch.GetAvailableTimesAsync(request, ct));

    [HttpPost("lock")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<ActionResult<DispatchLockResponse>> Lock(DispatchLockRequest request, CancellationToken ct)
    {
        var result = await dispatch.LockAsync(request, User.Identity?.Name, ct);
        return result.Success ? Ok(result) : Conflict(result);
    }
}
