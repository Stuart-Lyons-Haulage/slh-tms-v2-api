using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Resilient routing used by Driver Dispatch. It resolves missing stop coordinates from
/// Site Master/linked geofences first and Azure Maps address search as a final fallback,
/// so a single unmapped planner stop cannot make an otherwise valid run unusable.
/// </summary>
[ApiController, Route("api/v1/driver-dispatch-routes"), Authorize]
public sealed class DriverDispatchRouteController(
    TmsDbContext db,
    AzureMapsRouteClient maps,
    ILogger<DriverDispatchRouteController> logger) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var load = await FindLoadAsync(id, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        PlannerSourceMasterDataResolver? masterData = null;
        try
        {
            masterData = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(exception, "Site Master resolution was unavailable for Driver Dispatch route {LoadId}.", id);
        }

        var points = new List<(decimal Longitude, decimal Latitude)>();
        var unresolved = new List<object>();

        foreach (var stop in load.Stops.OrderBy(item => item.Sequence))
        {
            var resolved = OperationalStopCoordinates.Resolve(stop, masterData);
            if (resolved is null && !string.IsNullOrWhiteSpace(stop.Address))
            {
                try
                {
                    var coordinate = await maps.SearchCoordinate($"{stop.Name}, {stop.Address}", ct);
                    if (coordinate is not null)
                        resolved = (coordinate.Value.Longitude, coordinate.Value.Latitude);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogDebug(exception, "Azure Maps address fallback failed for Driver Dispatch stop {StopId}.", stop.Id);
                }
            }

            if (resolved is null)
            {
                unresolved.Add(new { stop.Id, stop.Sequence, stop.Name, stop.Address });
                continue;
            }

            points.Add(resolved.Value);
        }

        if (points.Count < 2)
        {
            return BadRequest(new
            {
                message = "This run could not be routed because fewer than two stops could be mapped.",
                mappedStops = points.Count,
                totalStops = load.Stops.Count,
                unresolvedStops = unresolved
            });
        }

        return Ok(await maps.Directions(points, ct));
    }

    private async Task<Load?> FindLoadAsync(Guid id, CancellationToken ct)
    {
        var registered = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
        if (registered is not null)
        {
            await RunOperationalStore.EnrichAsync(db, [registered], ct);
            return registered;
        }

        try
        {
            var load = await db.Loads.AsNoTracking()
                .Include(item => item.Stops)
                .SingleOrDefaultAsync(item => item.Id == id, ct);
            if (load is not null)
            {
                await RunOperationalStore.EnrichAsync(db, [load], ct);
                return load;
            }
        }
        catch (Exception exception) when (PlanningResilience.SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        return null;
    }
}
