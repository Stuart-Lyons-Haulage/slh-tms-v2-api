using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/beta-optimiser")]
[Authorize]
public sealed class BetaDayPlanController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    IConfiguration configuration,
    ILoggerFactory loggerFactory,
    ILogger<BetaDayPlanController> logger,
    SiteTimingRuleStore timingRuleStore) : ControllerBase
{
    [HttpGet("day-plan")]
    public async Task<IActionResult> BuildDay([FromQuery] DateOnly planningDate, CancellationToken ct)
    {
        try
        {
            return Ok(await Service().BuildDayAsync(planningDate, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta full-day build failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaDayPlanUnavailable",
                message = "Beta could not complete the read-only day build. Planner and Dispatch were not changed."
            });
        }
    }

    [HttpPost("day-plan/proposal")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> BuildProposal([FromQuery] DateOnly planningDate, CancellationToken ct)
    {
        try
        {
            var proposalService = new BetaPlanProposalService(db, loggerFactory.CreateLogger<BetaPlanProposalService>());
            var proposal = await proposalService.GenerateAsync(planningDate, Service(), User.Identity?.Name, ct);
            return Ok(proposal);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta route proposal generation failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaRouteProposalUnavailable",
                message = "Beta could not create the review proposal. No Runs or Dispatch allocations were changed."
            });
        }
    }

    [HttpPost("day-plan/compare")]
    public async Task<IActionResult> Compare([FromBody] BetaPlannerComparisonRequest request, CancellationToken ct)
    {
        if (request.Routes.Count == 0)
            return BadRequest(new { code = "LyonsPlanEmpty", message = "The uploaded Lyons plan contains no included runs." });
        try
        {
            return Ok(await Service().CompareAsync(request, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Beta Lyons-plan comparison failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                code = "BetaDayPlanComparisonUnavailable",
                message = "Beta could not compare the uploaded Lyons plan. Planner and Dispatch were not changed."
            });
        }
    }

    private BetaDayPlanService Service()
    {
        var options = configuration.GetSection(BetaOptimiserOptions.SectionName)
            .Get<BetaOptimiserOptions>() ?? new BetaOptimiserOptions();
        options.Validate();

        var liveProvider = new AzureMapsHgvRouteProvider(
            maps,
            loggerFactory.CreateLogger<AzureMapsHgvRouteProvider>(),
            options);
        // One shared budget covers both the independent Beta build and the uploaded-plan
        // routing within this HTTP request. When exhausted, remaining routes are marked
        // unavailable instead of allowing the gateway to terminate the entire comparison.
        var provider = new BudgetedBetaHgvRouteProvider(
            liveProvider,
            loggerFactory.CreateLogger<BudgetedBetaHgvRouteProvider>(),
            options.RequestRoutingBudget);
        var builder = new BetaDayPlanBuilder(provider, options);
        return new BetaDayPlanService(db, builder, provider, loggerFactory.CreateLogger<BetaDayPlanService>(), timingRuleStore);
    }
}
