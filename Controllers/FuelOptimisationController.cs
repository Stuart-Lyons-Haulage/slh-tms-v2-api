using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/fuel")]
[Authorize]
public sealed class FuelOptimisationController(FuelOptimisationService service) : ControllerBase
{
    [HttpGet("run/{runId:guid}/estimate")]
    public async Task<IActionResult> RunEstimate(Guid runId, CancellationToken ct)
    {
        var estimate = await service.EstimateRunAsync(runId, ct);
        return estimate is null
            ? NotFound(new { error = "The run could not be costed. Check that it exists, has route mileage or mapped stops, and that a fuel price is available." })
            : Ok(estimate);
    }

    [HttpGet("optimisation")]
    public async Task<ActionResult<FuelOptimisationSummary>> Optimisation([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await service.EstimatePlanningDateAsync(date, ct));
}
