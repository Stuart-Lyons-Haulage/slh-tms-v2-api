using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/dispatch/history"), Authorize]
public sealed class DispatchHistoryController(TmsDbContext db) : ControllerBase
{
    private static readonly LoadStatus[] ExecutedStatuses =
        [LoadStatus.Dispatched, LoadStatus.InProgress, LoadStatus.Completed];

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly date, CancellationToken ct)
    {
        var lookback = date.AddDays(-28);
        var loads = await db.Loads.AsNoTracking()
            .Include(load => load.Stops)
            .Where(load =>
                load.DriverId != null &&
                load.PlanningDate >= lookback &&
                load.PlanningDate < date &&
                load.Status != LoadStatus.Cancelled)
            .OrderByDescending(load => load.PlanningDate)
            .ThenByDescending(load => load.CreatedAtUtc)
            .ToListAsync(ct);

        var trailerIds = loads
            .Where(load => load.TrailerId != null)
            .Select(load => load.TrailerId!.Value)
            .Distinct()
            .ToArray();
        var trailers = await db.Trailers.AsNoTracking()
            .Where(trailer => trailerIds.Contains(trailer.Id))
            .ToDictionaryAsync(trailer => trailer.Id, ct);

        var rows = loads
            .GroupBy(load => load.DriverId!.Value)
            .Select(group =>
            {
                var ordered = group
                    .OrderByDescending(load => load.PlanningDate)
                    .ThenByDescending(load => load.CreatedAtUtc)
                    .ToList();
                var lastExecuted = ordered.FirstOrDefault(load => ExecutedStatuses.Contains(load.Status)) ?? ordered.First();
                var trailerLoad = ordered.FirstOrDefault(load => load.TrailerId != null);
                Trailer? trailer = null;
                if (trailerLoad?.TrailerId is Guid trailerId)
                    trailers.TryGetValue(trailerId, out trailer);

                var finalStop = OperationalStopOrdering.Order(lastExecuted.Stops).LastOrDefault();
                return new
                {
                    driverId = group.Key,
                    previousRunId = lastExecuted.Id,
                    previousRunReference = RunDisplayLabel.For(lastExecuted),
                    previousPlanningDate = lastExecuted.PlanningDate,
                    previousTrailerId = trailer?.Id,
                    previousTrailerNumber = trailer?.TrailerNumber,
                    previousTrailerPlanningDate = trailerLoad?.PlanningDate,
                    previousFinalStopName = finalStop?.Name,
                    previousFinalLatitude = finalStop?.Latitude,
                    previousFinalLongitude = finalStop?.Longitude
                };
            })
            .ToArray();

        return Ok(rows);
    }
}
