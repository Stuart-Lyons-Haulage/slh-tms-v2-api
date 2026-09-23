using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>Read-only Beta Optimiser endpoints. These routes analyse planning evidence only and never mutate live runs.</summary>
[ApiController]
[Route("api/v1/beta-optimiser")]
[Authorize]
public sealed class BetaOptimiserController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    ILoggerFactory loggerFactory,
    ILogger<BetaOptimiserController> logger) : ControllerBase
{
    [HttpGet("day")]
    public async Task<IActionResult> AnalyseDay([FromQuery] DateOnly planningDate, CancellationToken ct)
    {
        try
        {
            return Ok(await Service().AnalyseDayAsync(planningDate, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var safePlanningDate = planningDate.ToString().Replace("\r", "").Replace("\n", "");
            logger.LogError(ex, "Beta Optimiser day analysis failed for {PlanningDate}.", safePlanningDate);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaOptimiserUnavailable",
                message = "Beta Optimiser could not complete the read-only analysis. No planning data was changed."
            });
        }
    }

    [HttpPost("planner-csv/compare")]
    public async Task<IActionResult> ComparePlannerCsv(
        [FromBody] BetaPlannerComparisonRequest request,
        CancellationToken ct)
    {
        try
        {
            return Ok(await Service().AnalysePlannerRoutesAsync(request, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var safePlanningDate = SanitizeForLog(request?.PlanningDate.ToString());
            logger.LogError(ex, "Beta Optimiser planner CSV comparison failed for {PlanningDate}.", safePlanningDate);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaOptimiserPlannerComparisonUnavailable",
                message = "The uploaded planner benchmark could not be analysed. No planning data was changed."
            });
        }
    }

    private static string SanitizeForLog(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);
    }

    private BetaOptimiserService Service()
    {
        var provider = new AzureMapsHgvRouteProvider(
            maps,
            loggerFactory.CreateLogger<AzureMapsHgvRouteProvider>());
        var engine = new BetaRouteOptimisationEngine(provider);
        return new BetaOptimiserService(
            db,
            engine,
            loggerFactory.CreateLogger<BetaOptimiserService>());
    }
}
