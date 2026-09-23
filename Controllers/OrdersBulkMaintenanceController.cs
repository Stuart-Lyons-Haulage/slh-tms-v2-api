using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/orders"), Authorize]
public sealed class OrdersBulkMaintenanceController(TmsDbContext db, ILogger<OrdersBulkMaintenanceController> logger) : ControllerBase
{
    [HttpDelete("open"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CancelAllOpen(CancellationToken ct)
    {
        var warnings = new List<string>();
        var removedStops = 0;
        var cancelledOrders = 0;
        var cancelledLoads = 0;
        var archivedRegisterOrders = 0;
        var archivedRegisterLoads = 0;

        // Primary planning tables. Each operation is deliberately independent so a
        // legacy/missing planning table cannot prevent the order reset completing.
        try
        {
            removedStops = await db.LoadStops
                .Where(stop => stop.OrderId != null && db.TransportOrders.Any(order =>
                    order.Id == stop.OrderId.Value &&
                    order.Status != OrderStatus.Cancelled &&
                    order.Status != OrderStatus.Delivered))
                .ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Primary load-stop cleanup failed during clear-all reset.");
            warnings.Add("Some primary load stops could not be removed automatically.");
            db.ChangeTracker.Clear();
        }

        try
        {
            cancelledLoads = await db.Loads
                .Where(load => load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed)
                .ExecuteUpdateAsync(setters => setters.SetProperty(load => load.Status, LoadStatus.Cancelled), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Primary load cancellation failed during clear-all reset.");
            warnings.Add("Some primary runs could not be cancelled automatically.");
            db.ChangeTracker.Clear();
        }

        try
        {
            cancelledOrders = await db.TransportOrders
                .Where(order => order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Delivered)
                .ExecuteUpdateAsync(setters => setters.SetProperty(order => order.Status, OrderStatus.Cancelled), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Primary order cancellation failed during clear-all reset.");
            warnings.Add("Some primary orders could not be cancelled automatically.");
            db.ChangeTracker.Clear();
        }

        // The portal can deliberately fall back to StagedImports when legacy planning
        // tables are unavailable. Archive those records too, otherwise they reappear as
        // ReadyToPlan even after the primary TransportOrders row has been cancelled.
        try
        {
            var registerRows = await db.StagedImports
                .Where(row =>
                    row.EntityType == "order" ||
                    row.EntityType == "register:order" ||
                    row.EntityType == "planningload")
                .ToListAsync(ct);

            var now = DateTimeOffset.UtcNow;
            foreach (var row in registerRows)
            {
                if (row.EntityType == "planningload")
                {
                    archivedRegisterLoads++;
                    row.EntityType = "archived:planningload";
                }
                else
                {
                    archivedRegisterOrders++;
                    row.EntityType = "archived:order";
                }

                // Free the original idempotency key so the same customer file can be
                // imported again after an intentional planning reset.
                row.IdempotencyKey = $"cleared:{row.Id:N}:{Guid.NewGuid():N}";
                row.Status = StagingStatus.Rejected;
                row.ReviewedAtUtc = now;
                row.ReviewedBy = User.Identity?.Name;
                row.ReviewNote = "Archived by Clear all open jobs. Audit payload retained; original import key released for re-import.";
            }

            if (registerRows.Count > 0)
                await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Fallback planning-register reset failed during clear-all reset.");
            warnings.Add("The audited fallback register could not be fully archived. Retry the reset before re-importing.");
            db.ChangeTracker.Clear();
        }

        var totalOrders = cancelledOrders + archivedRegisterOrders;
        var totalRuns = cancelledLoads + archivedRegisterLoads;
        var warningText = warnings.Count == 0 ? null : string.Join(" ", warnings.Distinct());

        return Ok(new
        {
            cancelled = totalOrders,
            primaryCancelled = cancelledOrders,
            archivedRegisterOrders,
            cancelledRuns = totalRuns,
            primaryCancelledRuns = cancelledLoads,
            archivedRegisterLoads,
            removedStops,
            warnings,
            message = warningText is null
                ? $"Planning reset complete. Cleared {totalOrders} open job record(s), {totalRuns} open run record(s), and {removedStops} linked stop(s). Delivered/completed history was retained."
                : $"Planning reset completed with warnings. Cleared {totalOrders} open job record(s) and {totalRuns} open run record(s). {warningText}"
        });
    }
}
