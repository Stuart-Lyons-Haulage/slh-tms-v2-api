using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>Turns retained NWF workbook/email rows into durable capacity reservations.</summary>
public static class NwfBookingReservationSync
{
    public static async Task<Guid?> UpsertAsync(TmsDbContext db, StagedImport staged, JsonElement payload, string? actor, CancellationToken ct, Guid? sourceMovementId = null)
    {
        if (!string.Equals(Text(payload, "customerCode"), "NWF", StringComparison.OrdinalIgnoreCase)) return null;
        var jobType = Text(payload, "jobType") ?? "NWF booking";
        var incomingJobType = Text(payload, "jobType");
        var incomingPlannerNotes = Text(payload, "driverInstructions") ?? Text(payload, "plannerNotes");
        var date = Date(payload, "collectionDate");
        if (date is not DateOnly collectionDate) return null;
        var stableKey = Text(payload, "intakeNaturalKey") ?? Text(payload, "intakeMovementKey") ?? $"{collectionDate:yyyy-MM-dd}|{Text(payload, "collectionReference") ?? Text(payload, "nwfSalesOrderId") ?? staged.Id.ToString("N")}";
        stableKey = Clip(stableKey, 240)!;
        var existing = await db.BookingReservations.SingleOrDefaultAsync(row => row.CustomerCode == "NWF" && row.StableBookingKey == stableKey, ct);
        var sourceRowKey = Text(payload, "sourceRow") ?? Text(payload, "sourceRowKey");
        var incomingReferenceForIdentity = Text(payload, "collectionReference") ?? Text(payload, "loadReference") ?? Text(payload, "loadRef");
        var incomingCratePoForIdentity = Text(payload, "cratePo") ?? Text(payload, "nwfCratePoForGrower");
        if (existing is null)
        {
            // NWF can regenerate a workbook row with a different parser natural key while
            // retaining the real business identity. Reuse only an unambiguous exact match
            // on a retained source row, collection reference, or crate PO; otherwise leave
            // the new row separate for planner review rather than merging by guesswork.
            var identityCandidates = await db.BookingReservations
                .Where(row => row.CustomerCode == "NWF" &&
                    ((incomingReferenceForIdentity != null && row.CollectionReference == incomingReferenceForIdentity) ||
                     (incomingCratePoForIdentity != null && row.CratePurchaseOrder == incomingCratePoForIdentity)))
                .OrderByDescending(row => row.UpdatedAtUtc)
                .Take(2)
                .ToListAsync(ct);
            if (identityCandidates.Count == 1)
                existing = identityCandidates[0];

            if (existing is null && !string.IsNullOrWhiteSpace(sourceRowKey))
            {
                var sourceRowCandidates = await db.BookingReservationRevisions.AsNoTracking()
                    .Where(revision => revision.SourceRowKey == sourceRowKey)
                    .Select(revision => revision.BookingReservationId)
                    .Distinct()
                    .Take(2)
                    .ToListAsync(ct);
                if (sourceRowCandidates.Count == 1)
                    existing = await db.BookingReservations.SingleAsync(row => row.Id == sourceRowCandidates[0], ct);
            }
        }
        var units = Decimal(payload, "pallets") ?? Decimal(payload, "palletQty") ?? 0;
        var now = DateTimeOffset.UtcNow;
        var status = IsCancellation(payload)
            ? BookingReservationStatus.Cancelled
            : Bool(payload, "plannerReady") == true ? BookingReservationStatus.Confirmed : BookingReservationStatus.PreOrder;
        if (existing is null)
        {
            var row = new BookingReservation
            {
                CustomerCode = "NWF", BookingType = Clip(jobType, 80)!, StableBookingKey = stableKey,
                CollectionDate = collectionDate, DeliveryDate = Date(payload, "deliveryDate"), CollectionDepot = Clip(Text(payload, "collectionLocation") ?? Text(payload, "collectionSite") ?? Text(payload, "sellerName"), 200),
                DeliverySite = Clip(Text(payload, "deliveryLocation") ?? Text(payload, "deliveryAddress") ?? Text(payload, "nwfDepotDescription") ?? Text(payload, "stallNumber"), 200),
                CollectionReference = Clip(Text(payload, "collectionReference") ?? Text(payload, "loadReference") ?? Text(payload, "loadRef"), 120),
                CratePurchaseOrder = Clip(Text(payload, "cratePo") ?? Text(payload, "nwfCratePoForGrower"), 120), TransportPurchaseOrder = Clip(Text(payload, "transportPo") ?? Text(payload, "nwfPoRef"), 120),
                ReservedUnits = Math.Max(units, 0), UnitType = "PalletSpace", CompositionJson = payload.GetRawText(), PlannerNotes = Clip(incomingPlannerNotes, 1000),
                Status = status, SourceStagedImportId = staged.Id, SourceMovementId = sourceMovementId, CurrentRevisionNumber = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            db.BookingReservations.Add(row);
            db.BookingReservationRevisions.Add(Revision(row, payload, "Received", actor, now));
            db.OperationalHistoryEvents.Add(Event(row.Id, "BookingReservation", status == BookingReservationStatus.Cancelled ? "CancelledFromNwfIntake" : "ReceivedFromNwfIntake", new { staged.Id, row.Status, row.ReservedUnits }, actor, now));
            return row.Id;
        }

        var newUnits = Math.Max(units, 0);
        var activeAssignedUnits = await db.BookingReservationAllocations.AsNoTracking()
            .Where(item => item.BookingReservationId == existing.Id && item.IsActive)
            .SumAsync(item => (decimal?)item.Units, ct) ?? 0;
        var capacityConflict = newUnits < activeAssignedUnits;
        var incomingReference = incomingReferenceForIdentity;
        var incomingTransportPo = Text(payload, "transportPo") ?? Text(payload, "nwfPoRef");
        var incomingCollectionDate = Date(payload, "collectionDate");
        var incomingDeliveryDate = Date(payload, "deliveryDate");
        var incomingSourceMessageId = Text(payload, "sourceMessageId") ?? Text(payload, "sourceEmailMessageId");
        var latestSourceMessageId = await db.BookingReservationRevisions.AsNoTracking()
            .Where(revision => revision.BookingReservationId == existing.Id)
            .OrderByDescending(revision => revision.RevisionNumber)
            .Select(revision => revision.SourceMessageId)
            .FirstOrDefaultAsync(ct);
        var sourceChanged = !string.IsNullOrWhiteSpace(incomingSourceMessageId) &&
                            !string.Equals(latestSourceMessageId, incomingSourceMessageId, StringComparison.OrdinalIgnoreCase);
        var incomingDepot = Text(payload, "collectionLocation") ?? Text(payload, "collectionSite") ?? Text(payload, "sellerName");
        var incomingDeliverySite = Text(payload, "deliveryLocation") ?? Text(payload, "deliveryAddress") ?? Text(payload, "nwfDepotDescription") ?? Text(payload, "stallNumber");
        var incomingCratePo = Text(payload, "cratePo") ?? Text(payload, "nwfCratePoForGrower");
        var businessChanged = existing.ReservedUnits != newUnits ||
                      (incomingCollectionDate is DateOnly incomingDate && existing.CollectionDate != incomingDate) ||
                      (incomingDeliveryDate is DateOnly deliveryDate && existing.DeliveryDate != deliveryDate) ||
                      (!string.IsNullOrWhiteSpace(incomingDepot) && !string.Equals(existing.CollectionDepot, incomingDepot, StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrWhiteSpace(incomingDeliverySite) && !string.Equals(existing.DeliverySite, incomingDeliverySite, StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrWhiteSpace(incomingReference) && !string.Equals(existing.CollectionReference, incomingReference, StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrWhiteSpace(incomingCratePo) && !string.Equals(existing.CratePurchaseOrder, incomingCratePo, StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrWhiteSpace(incomingTransportPo) && !string.Equals(existing.TransportPurchaseOrder, incomingTransportPo, StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrWhiteSpace(incomingJobType) && !string.Equals(existing.BookingType, incomingJobType, StringComparison.OrdinalIgnoreCase)) ||
                      (!string.IsNullOrWhiteSpace(incomingPlannerNotes) && !string.Equals(existing.PlannerNotes, incomingPlannerNotes, StringComparison.OrdinalIgnoreCase)) ||
                      (sourceMovementId is Guid suppliedMovementId && existing.SourceMovementId != suppliedMovementId);
        var changed = businessChanged || sourceChanged;
        if (!changed) return existing.Id;
        var previous = new { existing.CollectionDate, existing.DeliveryDate, existing.CollectionDepot, existing.DeliverySite, existing.ReservedUnits, existing.CollectionReference, existing.CratePurchaseOrder, existing.TransportPurchaseOrder, existing.Status };
        if (incomingCollectionDate is DateOnly updatedCollectionDate) existing.CollectionDate = updatedCollectionDate;
        if (incomingDeliveryDate is DateOnly updatedDeliveryDate) existing.DeliveryDate = updatedDeliveryDate;
        existing.CollectionDepot = Clip(incomingDepot ?? existing.CollectionDepot, 200);
        existing.DeliverySite = Clip(incomingDeliverySite ?? existing.DeliverySite, 200);
        existing.ReservedUnits = newUnits;
        existing.CollectionReference = Clip(incomingReference ?? existing.CollectionReference, 120);
        existing.CratePurchaseOrder = Clip(incomingCratePo ?? existing.CratePurchaseOrder, 120);
        existing.TransportPurchaseOrder = Clip(incomingTransportPo ?? existing.TransportPurchaseOrder, 120);
        if (!string.IsNullOrWhiteSpace(incomingJobType)) existing.BookingType = Clip(incomingJobType, 80)!;
        if (!string.IsNullOrWhiteSpace(incomingPlannerNotes)) existing.PlannerNotes = Clip(incomingPlannerNotes, 1000);
        existing.SourceMovementId = sourceMovementId ?? existing.SourceMovementId;
        if (status == BookingReservationStatus.Cancelled)
            existing.Status = BookingReservationStatus.Cancelled;
        else if (businessChanged)
            existing.Status = status == BookingReservationStatus.Confirmed && existing.Status == BookingReservationStatus.PreOrder
                ? status
                : BookingReservationStatus.Amended;
        else if (status == BookingReservationStatus.Confirmed && existing.Status == BookingReservationStatus.PreOrder)
            existing.Status = status;
        existing.CurrentRevisionNumber++; existing.SourceStagedImportId = staged.Id; existing.CompositionJson = payload.GetRawText(); existing.UpdatedAtUtc = now;
        var changeNote = businessChanged ? "NWF snapshot amended existing reservation" : "New NWF source snapshot retained without business-field change";
        if (capacityConflict) changeNote += $"; amended capacity is below {activeAssignedUnits:0.##} already assigned units";
        db.BookingReservationRevisions.Add(Revision(existing, payload, changeNote, actor, now));
        db.OperationalHistoryEvents.Add(Event(existing.Id, "BookingReservation", status == BookingReservationStatus.Cancelled ? "CancelledFromNwfIntake" : businessChanged ? "AmendedFromNwfIntake" : "SourceSnapshotRetained", new { previous, current = new { existing.ReservedUnits, existing.CollectionReference, existing.TransportPurchaseOrder, existing.Status }, staged.Id }, actor, now));
        if (capacityConflict)
            db.OperationalHistoryEvents.Add(Event(existing.Id, "BookingReservation", "CapacityConflictDetected", new { amendedCapacity = newUnits, activeAssignedUnits }, actor, now));
        return existing.Id;
    }

    private static BookingReservationRevision Revision(BookingReservation row, JsonElement payload, string note, string? actor, DateTimeOffset now) => new() { BookingReservationId = row.Id, RevisionNumber = row.CurrentRevisionNumber, Status = row.Status, SourceRowKey = Clip(Text(payload, "sourceRow") ?? Text(payload, "sourceRowKey"), 120), SourceMessageId = Clip(Text(payload, "sourceMessageId") ?? Text(payload, "sourceEmailMessageId"), 500), SourceAttachmentIdentity = Clip(Text(payload, "sourceAttachmentName"), 500), PayloadJson = payload.GetRawText(), ChangeNote = note, Actor = Clip(actor, 200), CreatedAtUtc = now };
    private static OperationalHistoryEvent Event(Guid id, string type, string eventType, object payload, string? actor, DateTimeOffset now) => new() { EntityId = id, EntityType = type, EventType = eventType, Actor = Clip(actor, 200), PayloadJson = JsonSerializer.Serialize(payload), OccurredAtUtc = now };
    private static string? Text(JsonElement payload, string name) => payload.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.ValueKind != JsonValueKind.Undefined ? value.ToString().Trim() : null;
    private static bool? Bool(JsonElement payload, string name) => bool.TryParse(Text(payload, name), out var value) ? value : null;
    private static bool IsCancellation(JsonElement payload)
    {
        if (Bool(payload, "cancelled") == true || Bool(payload, "canceled") == true || Bool(payload, "deleted") == true) return true;
        var status = Text(payload, "status") ?? Text(payload, "bookingStatus") ?? Text(payload, "intakeStatus");
        return status is not null && (status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) || status.Equals("canceled", StringComparison.OrdinalIgnoreCase) || status.Equals("deleted", StringComparison.OrdinalIgnoreCase));
    }
    private static decimal? Decimal(JsonElement payload, string name) => decimal.TryParse(Text(payload, name), out var value) ? value : null;
    private static DateOnly? Date(JsonElement payload, string name) => DateOnly.TryParse(Text(payload, name), out var value) ? value : null;
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}
