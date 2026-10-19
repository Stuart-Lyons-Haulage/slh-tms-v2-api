using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>What the TMS has learned from how planners build runs, and the suggestion for a day.</summary>
[ApiController, Route("api/v1/planner/learning"), Authorize]
public sealed class PlannerLearningController(TmsDbContext db, ILogger<PlannerLearningController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Insights([FromQuery] int days = 90, CancellationToken ct = default)
    {
        var history = await PlannerLearning.ReadRecentAsync(db, days, ct);
        return Ok(PlannerLearning.Learn(history, days, DateTimeOffset.UtcNow));
    }

    /// <summary>Per-order hints for a day: the usual run for this customer/destination, and a size check.</summary>
    [HttpGet("orders")]
    public async Task<IActionResult> OrderHints([FromQuery] DateOnly? date, CancellationToken ct = default)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var history = await PlannerLearning.ReadRecentAsync(db, 90, ct);
        if (history.Count == 0) return Ok(Array.Empty<PlannerLearning.OrderHint>());
        try
        {
            var orders = await PlanningRegisterStore.ReadOrdersAsync(db, planningDate, planningDate, ct);
            var sqlOrders = await db.TransportOrders.AsNoTracking().Where(order => order.CollectionDate == planningDate).Take(3000).ToListAsync(ct);
            var merged = sqlOrders.Concat(orders).GroupBy(order => order.Reference).Select(group => group.First()).ToList();
            var runs = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
                .Where(load => load.Status != LoadStatus.Cancelled)
                .Select(load => (load.Id, load.Reference)).ToList();
            var orderInputs = merged
                .Where(order => order.Status != OrderStatus.Cancelled)
                .Select(order => (order.Id, order.CustomerCode, Destination: order.StallNumber ?? string.Empty, Pallets: order.Pallets ?? 0));
            return Ok(PlannerLearning.HintsFor(history, orderInputs, runs, DateTimeOffset.UtcNow));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Planner learning order hints could not be built for {Date}.", planningDate);
            return Ok(Array.Empty<PlannerLearning.OrderHint>());
        }
    }
}
