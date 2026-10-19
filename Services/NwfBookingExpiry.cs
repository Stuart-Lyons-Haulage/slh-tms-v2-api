using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// An NWF crate/tray booking that never got a Transport PO lapses once its collection day
/// has gone: it stops holding space in Booking & Capacity. Bookings with an order already
/// assigned, or confirmed, are left alone.
/// </summary>
public static class NwfBookingExpiry
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static async Task<int> RunAsync(TmsDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, London).DateTime);
        var stale = await db.BookingReservations
            .Where(row => row.CustomerCode == "NWF" && row.CollectionDate < today &&
                          (row.Status == BookingReservationStatus.PreOrder || row.Status == BookingReservationStatus.AwaitingDetails) &&
                          (row.TransportPurchaseOrder == null || row.TransportPurchaseOrder == ""))
            .Take(500)
            .ToListAsync(ct);
        if (stale.Count == 0) return 0;
        var ids = stale.Select(row => row.Id).ToList();
        var assigned = (await db.BookingReservationAllocations.AsNoTracking()
            .Where(item => ids.Contains(item.BookingReservationId) && item.IsActive)
            .Select(item => item.BookingReservationId).Distinct().ToListAsync(ct)).ToHashSet();
        var expired = 0;
        foreach (var row in stale.Where(row => !assigned.Contains(row.Id)))
        {
            var previous = row.Status;
            row.Status = BookingReservationStatus.Expired;
            row.UpdatedAtUtc = now;
            db.OperationalHistoryEvents.Add(new OperationalHistoryEvent
            {
                EntityId = row.Id,
                EntityType = "BookingReservation",
                EventType = "ExpiredNoTransportPo",
                Actor = "NWF booking rule",
                PayloadJson = JsonSerializer.Serialize(new { previous = previous.ToString(), row.CollectionDate, reason = "Collection day passed without an NWF Transport PO" }),
                OccurredAtUtc = now
            });
            expired++;
        }
        if (expired > 0) await db.SaveChangesAsync(ct);
        return expired;
    }
}
