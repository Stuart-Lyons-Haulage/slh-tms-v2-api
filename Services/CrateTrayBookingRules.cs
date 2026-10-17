using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps Booking & Capacity limited to equipment movements. Ordinary NWF pallet
/// orders remain operational orders and must never become capacity reservations.
/// </summary>
public static class CrateTrayBookingRules
{
    private static readonly string[] SignalFields =
    [
        "jobType", "orderType", "bookingType", "unitType", "handlingUnitType", "intakeProfile"
    ];

    private static readonly string[] IdentityFields =
    [
        "transportPo", "cratePo", "nwfCratePoForGrower", "collectionReference",
        "loadReference", "loadRef", "customerPo", "productPo", "poNumber"
    ];

    public static bool IsPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return false;
        if (SignalFields.Select(field => Text(payload, field)).Any(IsEquipmentText)) return true;
        return HasValue(payload, "cratePo") || HasValue(payload, "nwfCratePoForGrower") ||
               HasValue(payload, "crateCollectionSite");
    }

    public static bool IsReservation(BookingReservation reservation)
    {
        if (IsEquipmentText(reservation.BookingType) || IsEquipmentText(reservation.UnitType) ||
            !string.IsNullOrWhiteSpace(reservation.CratePurchaseOrder)) return true;
        try
        {
            using var document = JsonDocument.Parse(reservation.CompositionJson);
            return IsPayload(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsManualReservation(string? bookingType, string? unitType, JsonElement composition) =>
        IsEquipmentText(bookingType) || IsEquipmentText(unitType) || IsPayload(composition);

    public static int MatchScore(BookingReservation reservation, TransportOrder order, JsonElement orderPayload)
    {
        if (!IsReservation(reservation) || !IsPayload(orderPayload)) return 0;
        if (order.CollectionDate < reservation.CollectionDate.AddDays(-1) ||
            order.CollectionDate > reservation.CollectionDate.AddDays(1)) return 0;

        var score = reservation.SourceMovementId is Guid reservationMovement &&
                    order.SourceMovementId == reservationMovement ? 120 : 0;
        var shared = IdentityTokens(reservation).Intersect(IdentityTokens(orderPayload), StringComparer.OrdinalIgnoreCase).Count();
        if (shared > 0) score = Math.Max(score, 80 + Math.Min(shared, 4) * 5);
        if (score == 0) return 0;

        if (order.CollectionDate == reservation.CollectionDate) score += 10;
        if (SamePlace(reservation.CollectionDepot, FirstText(orderPayload, "collectionLocation", "collectionSite", "sellerName") ?? order.SellerName)) score += 4;
        if (SamePlace(reservation.DeliverySite, FirstText(orderPayload, "deliveryLocation", "deliverySite", "stallNumber") ?? order.StallNumber)) score += 4;
        return score;
    }

    public static async Task<JsonElement?> CurrentOrderPayloadAsync(TmsDbContext db, TransportOrder order, CancellationToken ct)
    {
        if (order.SourceMovementId is not Guid movementId) return null;
        var revisionId = await db.OrderMovements.AsNoTracking()
            .Where(item => item.Id == movementId)
            .Select(item => item.CurrentRevisionId)
            .SingleOrDefaultAsync(ct);
        if (revisionId is not Guid currentRevisionId) return null;
        var json = await db.OrderRevisions.AsNoTracking()
            .Where(item => item.Id == currentRevisionId)
            .Select(item => item.PayloadJson)
            .SingleOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HashSet<string> IdentityTokens(BookingReservation reservation)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddToken(result, reservation.CollectionReference);
        AddToken(result, reservation.CratePurchaseOrder);
        AddToken(result, reservation.TransportPurchaseOrder);
        try
        {
            using var document = JsonDocument.Parse(reservation.CompositionJson);
            AddTokens(result, document.RootElement);
        }
        catch (JsonException) { }
        return result;
    }

    private static HashSet<string> IdentityTokens(JsonElement payload)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTokens(result, payload);
        return result;
    }

    private static void AddTokens(HashSet<string> result, JsonElement payload)
    {
        foreach (var field in IdentityFields) AddToken(result, Text(payload, field));
        if (TryGet(payload, "intakeMatchKeys", out var keys) && keys.ValueKind == JsonValueKind.Array)
            foreach (var key in keys.EnumerateArray()) AddToken(result, key.ToString());
    }

    private static void AddToken(HashSet<string> result, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var values = new[] { value }.Concat(value.Split(new[] { '/', '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var candidate in values)
        {
            var token = Canonical(candidate);
            if (token.Length >= 6 && token is not "NWF" and not "ALDI" && !token.StartsWith("EMAIL", StringComparison.Ordinal))
                result.Add(token);
        }
    }

    private static bool IsEquipmentText(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("tray", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("IFCO", StringComparison.OrdinalIgnoreCase));

    private static bool HasValue(JsonElement payload, string name) => !string.IsNullOrWhiteSpace(Text(payload, name));
    private static string? FirstText(JsonElement payload, params string[] names) => names.Select(name => Text(payload, name)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGet(payload, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var text = value.ToString().Trim();
        return text.Length == 0 ? null : text;
    }

    private static bool TryGet(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.ValueKind == JsonValueKind.Object)
            foreach (var property in payload.EnumerateObject())
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static bool SamePlace(string? left, string? right)
    {
        var a = Canonical(left);
        var b = Canonical(right);
        return a.Length >= 4 && b.Length >= 4 && (a == b || a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal));
    }

    private static string Canonical(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public static class CrateTrayBookingMatcher
{
    public static async Task<Guid?> TryMatchAsync(
        TmsDbContext db,
        TransportOrder order,
        JsonElement orderPayload,
        string? actor,
        CancellationToken ct)
    {
        if (!CrateTrayBookingRules.IsPayload(orderPayload) || order.Pallets is not > 0) return null;
        if (await db.BookingReservationAllocations.AnyAsync(item => item.TransportOrderId == order.Id && item.IsActive, ct) ||
            db.BookingReservationAllocations.Local.Any(item => item.TransportOrderId == order.Id && item.IsActive)) return null;

        var persisted = await db.BookingReservations
            .Where(item => item.CollectionDate >= order.CollectionDate.AddDays(-1) &&
                           item.CollectionDate <= order.CollectionDate.AddDays(1) &&
                           item.Status != BookingReservationStatus.Cancelled &&
                           item.Status != BookingReservationStatus.Superseded &&
                           item.Status != BookingReservationStatus.Expired)
            .ToListAsync(ct);
        var candidates = persisted.Concat(db.BookingReservations.Local)
            .DistinctBy(item => item.Id)
            .Where(CrateTrayBookingRules.IsReservation)
            .Select(item => new { Reservation = item, Score = CrateTrayBookingRules.MatchScore(item, order, orderPayload) })
            .Where(item => item.Score > 0)
            .ToList();
        if (candidates.Count == 0) return null;

        var candidateIds = candidates.Select(candidate => candidate.Reservation.Id).ToArray();
        var assignedByReservation = (await db.BookingReservationAllocations.AsNoTracking()
                .Where(item => item.IsActive && candidateIds.Contains(item.BookingReservationId))
                .GroupBy(item => item.BookingReservationId)
                .Select(group => new { Id = group.Key, Units = group.Sum(item => item.Units) })
                .ToListAsync(ct))
            .ToDictionary(item => item.Id, item => item.Units);
        foreach (var local in db.BookingReservationAllocations.Local.Where(item => item.IsActive))
            assignedByReservation[local.BookingReservationId] = assignedByReservation.GetValueOrDefault(local.BookingReservationId) + local.Units;

        candidates = candidates
            .Where(item => item.Reservation.ReservedUnits - assignedByReservation.GetValueOrDefault(item.Reservation.Id) >= order.Pallets.Value)
            .ToList();
        if (candidates.Count == 0) return null;
        var bestScore = candidates.Max(item => item.Score);
        var best = candidates.Where(item => item.Score == bestScore).ToList();
        if (best.Count != 1) return null;

        var reservation = best[0].Reservation;
        var units = order.Pallets.Value;
        var assigned = assignedByReservation.GetValueOrDefault(reservation.Id);
        var now = DateTimeOffset.UtcNow;
        var allocation = new BookingReservationAllocation
        {
            BookingReservationId = reservation.Id,
            TransportOrderId = order.Id,
            Destination = order.StallNumber,
            Units = units,
            UnitType = reservation.UnitType,
            Note = "Automatically matched by exact crate/tray reference",
            CreatedBy = actor ?? "Automatic intake",
            CreatedAtUtc = now
        };
        db.BookingReservationAllocations.Add(allocation);
        reservation.Status = assigned + units == reservation.ReservedUnits
            ? BookingReservationStatus.Assigned
            : BookingReservationStatus.PartiallyAssigned;
        reservation.UpdatedAtUtc = now;
        db.OperationalHistoryEvents.Add(new OperationalHistoryEvent
        {
            EntityType = "BookingReservation",
            EntityId = reservation.Id,
            EventType = "AutomaticallyMatchedToCrateTrayOrder",
            Actor = actor ?? "Automatic intake",
            PayloadJson = JsonSerializer.Serialize(new { order.Id, order.Reference, units, matchScore = bestScore }),
            OccurredAtUtc = now
        });
        return allocation.Id;
    }
}
