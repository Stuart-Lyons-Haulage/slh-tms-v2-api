using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning/optimiser"), Authorize]
public sealed class PlanningOptimiserController(
    PlanningOptimiserService service,
    TmsDbContext db,
    ILogger<PlanningOptimiserController> logger,
    ILogger<PlanningGeographicRepairService> geographicLogger) : ControllerBase
{
    [HttpPost("proposals"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Generate([FromBody] GeneratePlanProposalRequest request, CancellationToken ct)
    {
        try
        {
            var proposal = await service.GenerateAsync(request, User.Identity?.Name, ct);
            await new PlanningGeographicRepairService(db, geographicLogger).RepairAsync(
                proposal.Id,
                request.PlanningDate,
                proposal.EvidenceCapturedAtUtc,
                ct);
            var repaired = await service.GetAsync(proposal.Id, ct);
            return CreatedAtAction(nameof(Get), new { id = proposal.Id }, repaired ?? proposal);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            // Required schema changes are now applied only during application startup.
            // A live request must never mutate database schema or hide migration drift.
            logger.LogError(exception, "Planning optimiser schema is unavailable after startup migration validation.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "PlanningOptimiserSchemaUnavailable",
                message = "Planning proposal generation is unavailable because the required database schema is not healthy. No live runs were created or changed."
            });
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Planning optimiser proposal generation failed before any live plan changes were made.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "PlanningOptimiserUnavailable",
                message = "Planning proposal generation is temporarily unavailable. No live runs were created or changed; use manual planning while this is checked."
            });
        }
    }

    [HttpGet("proposals/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var proposal = await service.GetAsync(id, ct);
        return proposal is null ? NotFound() : Ok(proposal);
    }

    [HttpPost("proposals/{id:guid}/apply"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Apply(Guid id, [FromBody] ApplyPlanProposalRequest request, CancellationToken ct)
    {
        try
        {
            var result = await new PlanningProposalApplicationService(db).ApplyAsync(id, request, User.Identity?.Name, ct);
            return Ok(result);
        }
        catch (PlanProposalApplyException exception) when (exception.Code == "ProposalNotFound")
        {
            return NotFound(new { exception.Code, exception.Message });
        }
        catch (PlanProposalApplyException exception)
        {
            return Conflict(new { exception.Code, exception.Message });
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            logger.LogError(exception, "Planning optimiser apply schema is unavailable after startup migration validation.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "PlanningOptimiserSchemaUnavailable",
                message = "Applying the reviewed proposal is unavailable because the required database schema is not healthy. No additional live runs were created."
            });
        }
        catch (Exception exception) when (!ct.IsCancellationRequested)
        {
            logger.LogError(exception, "Planning optimiser proposal apply failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "PlanningOptimiserApplyUnavailable",
                message = "The reviewed proposal could not be applied just now. Refresh the planner before retrying so live planning is not duplicated."
            });
        }
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
