using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Planner-specific persistence for the live run builder. Unlike the general operational
/// stop editor, a Draft run is allowed to become temporarily empty when its last order is
/// returned to Orders to Plan. This keeps the durable run structure aligned with the live
/// pallet allocation without requiring a manual Save Run action.
/// </summary>
[ApiController]
[Route("api/v1/planning-control/runs")]
[Authorize]
public sealed class PlannerAutosaveController(TmsDbContext db) : ControllerBase
{
    [HttpPut("{id:guid}/stops")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStops(Guid id, List<UpdateLoadStopRequest> request, CancellationToken ct)
    {
        Load? load = null;
        var registerBacked = false;

        try
        {
            load = await db.Loads
                .Include(item => item.Stops)
                .SingleOrDefaultAsync(item => item.Id == id, ct);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        if (load is null)
        {
            load = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            registerBacked = load is not null;
        }

        if (load is null) return NotFound(new { message = "The selected run could not be found." });
        if (request.Any(stop => string.IsNullOrWhiteSpace(stop.Name)))
            return BadRequest(new { message = "Every supplied stop must have a name." });
        if (request.Count == 0 && load.Status != LoadStatus.Draft)
            return BadRequest(new { message = "Only a draft run can have all stops cleared." });

        var nextStops = request.Select((stop, index) => new LoadStop
        {
            LoadId = load.Id,
            OrderId = stop.OrderId,
            Sequence = index + 1,
            Name = stop.Name.Trim(),
            Address = stop.Address,
            Latitude = stop.Latitude,
            Longitude = stop.Longitude,
            PlannedArrivalUtc = stop.PlannedArrivalUtc,
            PlannerNote = string.IsNullOrWhiteSpace(stop.PlannerNote) ? null : stop.PlannerNote.Trim()[..Math.Min(stop.PlannerNote.Trim().Length, 1000)]
        }).ToList();

        if (registerBacked)
        {
            load.Stops = nextStops;
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        }
        else
        {
            db.LoadStops.RemoveRange(load.Stops);
            load.Stops = nextStops;
            await db.SaveChangesAsync(ct);
        }

        return Ok(load);
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
