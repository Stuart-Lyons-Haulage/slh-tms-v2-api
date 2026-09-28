using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class InvoiceRecordService
{
    public static async Task EnsureDraftForCompletedLoadAsync(TmsDbContext db, Load load, string? actor, CancellationToken ct)
    {
        if (load.Status != LoadStatus.Completed || await db.InvoiceRecords.AnyAsync(item => item.LoadId == load.Id && item.Status != InvoiceRecordStatus.Cancelled, ct)) return;
        var orderIds = load.Stops.Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToArray();
        var orders = orderIds.Length == 0
            ? []
            : await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).OrderBy(order => order.Reference).ToListAsync(ct);
        var reservationByOrder = orderIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await db.BookingReservationAllocations.AsNoTracking()
                .Where(allocation => allocation.IsActive && allocation.TransportOrderId != null && orderIds.Contains(allocation.TransportOrderId.Value))
                .GroupBy(allocation => allocation.TransportOrderId!.Value)
                .Select(group => new { OrderId = group.Key, ReservationId = group.Select(item => item.BookingReservationId).First() })
                .ToDictionaryAsync(item => item.OrderId, item => item.ReservationId, ct);
        var reservationIds = reservationByOrder.Values.Distinct().ToArray();
        var customer = orders.Select(order => order.CustomerCode).FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        if (string.IsNullOrWhiteSpace(customer)) return;
        var now = DateTimeOffset.UtcNow;
        var record = new InvoiceRecord
        {
            CustomerCode = customer, LoadId = load.Id, InvoiceDate = load.PlanningDate, Status = InvoiceRecordStatus.Ready,
            Notes = "Automatically prepared from completed operational load; commercial values require accounts confirmation.",
            BookingReservationId = reservationIds.Length == 1 ? reservationIds[0] : null,
            PayloadJson = JsonSerializer.Serialize(new { load.Reference, load.Status, orderIds, reservationIds }), CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.InvoiceRecords.Add(record);
        foreach (var order in orders)
            db.InvoiceRecordLines.Add(new InvoiceRecordLine { InvoiceRecordId = record.Id, LoadId = load.Id, TransportOrderId = order.Id, BookingReservationId = reservationByOrder.GetValueOrDefault(order.Id), Description = order.Reference, Quantity = order.Pallets, OperationalReferenceSnapshot = $"{order.Reference} · {order.CustomerCode} · {order.CollectionDate:yyyy-MM-dd}" });
        db.OperationalHistoryEvents.Add(new OperationalHistoryEvent
        {
            EntityType = "InvoiceRecord", EntityId = record.Id, EventType = "PreparedFromCompletedLoad", Actor = actor,
            PayloadJson = JsonSerializer.Serialize(new { record.LoadId, load.Reference, orderIds, record.Status }), OccurredAtUtc = now
        });
    }
}
