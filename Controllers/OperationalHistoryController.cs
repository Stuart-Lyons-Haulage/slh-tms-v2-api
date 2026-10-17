using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/operational-history"), Authorize]
public sealed class OperationalHistoryController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string entityType, [FromQuery] Guid entityId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(entityType) || entityId == Guid.Empty) return BadRequest("entityType and entityId are required.");
        return Ok(await db.OperationalHistoryEvents.AsNoTracking().Where(row => row.EntityType == entityType && row.EntityId == entityId).OrderBy(row => row.OccurredAtUtc).ToListAsync(ct));
    }

    [HttpGet("load/{loadId:guid}")]
    public async Task<IActionResult> LoadChain(Guid loadId, CancellationToken ct)
    {
        var load = await db.Loads.AsNoTracking().Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == loadId, ct);
        if (load is null)
        {
            db.ChangeTracker.Clear();
            load = await PlanningRegisterStore.GetLoadAsync(db, loadId, ct);
        }
        if (load is null) return NotFound();

        var orderIds = load.Stops.Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToArray();
        var reservationIds = orderIds.Length == 0
            ? []
            : await db.BookingReservationAllocations.AsNoTracking()
                .Where(item => item.TransportOrderId != null && orderIds.Contains(item.TransportOrderId.Value))
                .Select(item => item.BookingReservationId).Distinct().ToArrayAsync(ct);
        var invoiceIds = await db.InvoiceRecords.AsNoTracking().Where(item => item.LoadId == loadId).Select(item => item.Id).ToArrayAsync(ct);

        var events = await db.OperationalHistoryEvents.AsNoTracking()
            .Where(item =>
                item.EntityType == "Load" && item.EntityId == loadId ||
                item.EntityType == "TransportOrder" && orderIds.Contains(item.EntityId) ||
                item.EntityType == "BookingReservation" && reservationIds.Contains(item.EntityId) ||
                item.EntityType == "InvoiceRecord" && invoiceIds.Contains(item.EntityId))
            .OrderBy(item => item.OccurredAtUtc)
            .ToListAsync(ct);
        var driverEvents = await db.DriverStatusLogs.AsNoTracking().Where(item => item.LoadId == loadId).OrderBy(item => item.CapturedAtUtc).ToListAsync(ct);
        var combined = events.Select(item => new HistoryChainItem(item.Id, item.EntityType, item.EntityId, item.EventType, item.Actor, item.PayloadJson, item.OccurredAtUtc))
            .Concat(driverEvents.Select(item => new HistoryChainItem(item.Id, "DriverStatusLog", item.LoadId, item.Status, item.CapturedBy, JsonSerializer.Serialize(new { item.Notes, item.DriverId }), item.CapturedAtUtc)))
            .OrderBy(item => item.OccurredAtUtc)
            .ToArray();
        return Ok(new { loadId, orderIds, reservationIds, invoiceIds, events = combined });
    }
}

public sealed record HistoryChainItem(Guid Id, string EntityType, Guid EntityId, string EventType, string? Actor, string PayloadJson, DateTimeOffset OccurredAtUtc);
