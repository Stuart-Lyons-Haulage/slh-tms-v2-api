using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Read-only production health probe for the data dependencies used by Planner and Planner Import.
/// It exercises the resilient SQL/register read paths without creating or changing operational data.
/// </summary>
[ApiController, Route("api/v1/health/planner-import")]
public sealed class PlannerImportHealthController(
    TmsDbContext db,
    ILogger<PlannerImportHealthController> logger) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var driverCount = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);
            var vehicleCount = await db.Vehicles.AsNoTracking().CountAsync(vehicle => vehicle.Active, ct);
            var trailerCount = await db.Trailers.AsNoTracking().CountAsync(trailer => trailer.Active, ct);
            var siteCount = await db.Sites.AsNoTracking().CountAsync(site => site.Active, ct);
            var stagingCount = await db.StagedImports.AsNoTracking().CountAsync(ct);

            var orderSource = "SQL";
            var loadSource = "SQL";
            int orderCount;
            int loadCount;

            try
            {
                orderCount = await db.TransportOrders.AsNoTracking().CountAsync(ct);
            }
            catch (Exception exception) when (SchemaUnavailable(exception))
            {
                db.ChangeTracker.Clear();
                orderCount = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Count;
                orderSource = "audited register fallback";
            }

            try
            {
                loadCount = await db.Loads.AsNoTracking().CountAsync(ct);
            }
            catch (Exception exception) when (SchemaUnavailable(exception))
            {
                db.ChangeTracker.Clear();
                loadCount = (await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Count;
                loadSource = "audited register fallback";
            }

            return Ok(new
            {
                status = "healthy",
                checkedAtUtc,
                dependencies = new
                {
                    activeDrivers = driverCount,
                    activeVehicles = vehicleCount,
                    activeTrailers = trailerCount,
                    activeSites = siteCount,
                    stagingRows = stagingCount,
                    orders = new { count = orderCount, source = orderSource },
                    loads = new { count = loadCount, source = loadSource }
                },
                plannerImportRoute = "/api/v1/planning/import-plan",
                message = "Planner and Planner Import read dependencies are available. This probe is read-only."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Planner/import health probe failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "unhealthy",
                checkedAtUtc,
                error = exception.GetType().Name,
                message = exception.GetBaseException().Message
            });
        }
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }
}
