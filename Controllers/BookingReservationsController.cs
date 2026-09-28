using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/booking-reservations"), Authorize]
public sealed class BookingReservationsController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? status, [FromQuery] string? customerCode, CancellationToken ct)
    {
        var query = db.BookingReservations.AsNoTracking();
        if (from is DateOnly start) query = query.Where(row => row.CollectionDate >= start);
        if (to is DateOnly end) query = query.Where(row => row.CollectionDate <= end);
        if (!string.IsNullOrWhiteSpace(customerCode)) query = query.Where(row => row.CustomerCode == customerCode);
        if (Enum.TryParse<BookingReservationStatus>(status, true, out var parsedStatus)) query = query.Where(row => row.Status == parsedStatus);

        var reservations = await query.OrderBy(row => row.CollectionDate).ThenBy(row => row.CustomerCode).ThenBy(row => row.CollectionReference).Take(1000).ToListAsync(ct);
        var ids = reservations.Select(row => row.Id).ToArray();
        var allocationTotals = await db.BookingReservationAllocations.AsNoTracking()
            .Where(row => ids.Contains(row.BookingReservationId) && row.IsActive)
            .GroupBy(row => row.BookingReservationId)
            .Select(group => new { Id = group.Key, Units = group.Sum(row => row.Units), Count = group.Count() })
            .ToDictionaryAsync(row => row.Id, row => new AllocationTotal(row.Units, row.Count), ct);

        return Ok(reservations.Select(row => ToDto(row, allocationTotals.GetValueOrDefault(row.Id))).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var row = await db.BookingReservations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return NotFound();
        var revisions = await db.BookingReservationRevisions.AsNoTracking().Where(item => item.BookingReservationId == id).OrderBy(item => item.RevisionNumber).ToListAsync(ct);
        var allocations = await db.BookingReservationAllocations.AsNoTracking().Where(item => item.BookingReservationId == id).OrderBy(item => item.CreatedAtUtc).ToListAsync(ct);
        var activeAllocations = allocations.Where(item => item.IsActive).ToArray();
        return Ok(new { reservation = ToDto(row, new AllocationTotal(activeAllocations.Sum(item => item.Units), activeAllocations.Length)), revisions, allocations });
    }

    [HttpPost]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Create(CreateBookingReservationRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerCode) || string.IsNullOrWhiteSpace(request.BookingType)) return BadRequest("CustomerCode and BookingType are required.");
        if (request.ReservedUnits < 0) return BadRequest("ReservedUnits cannot be negative.");

        var key = string.IsNullOrWhiteSpace(request.StableBookingKey)
            ? BuildStableKey(request.CustomerCode, request.CollectionDate, request.CollectionReference, request.CratePurchaseOrder, request.SourceRowKey)
            : request.StableBookingKey.Trim();
        var existing = await db.BookingReservations.SingleOrDefaultAsync(row => row.CustomerCode == request.CustomerCode && row.StableBookingKey == key, ct);
        if (existing is not null) return Ok(new { id = existing.Id, existing = true, status = existing.Status.ToString() });

        var now = DateTimeOffset.UtcNow;
        var row = new BookingReservation
        {
            CustomerCode = request.CustomerCode.Trim(), BookingType = request.BookingType.Trim(), StableBookingKey = key,
            CollectionDate = request.CollectionDate, DeliveryDate = request.DeliveryDate, CollectionDepot = Clip(request.CollectionDepot, 200),
            DeliverySite = Clip(request.DeliverySite, 200), CollectionReference = Clip(request.CollectionReference, 120),
            CratePurchaseOrder = Clip(request.CratePurchaseOrder, 120), TransportPurchaseOrder = Clip(request.TransportPurchaseOrder, 120),
            ReservedUnits = request.ReservedUnits, UnitType = Clip(request.UnitType, 40) ?? "PalletSpace",
            CompositionJson = request.Composition.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : request.Composition.GetRawText(),
            PlannerNotes = Clip(request.PlannerNotes, 1000), Status = request.Status ?? BookingReservationStatus.PreOrder,
            SourceStagedImportId = request.SourceStagedImportId, CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.BookingReservations.Add(row);
        db.BookingReservationRevisions.Add(NewRevision(row, request.SourceRowKey, request.SourceMessageId, request.SourceAttachmentIdentity, request.RevisionPayload, "Received", User.Identity?.Name, now));
        db.OperationalHistoryEvents.Add(Event(row.Id, "BookingReservation", "Received", new { row.Status, row.CollectionReference, row.ReservedUnits, row.SourceStagedImportId }, User.Identity?.Name, now));
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = row.Id }, new { id = row.Id, existing = false, status = row.Status.ToString() });
    }

    [HttpPost("{id:guid}/revisions")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Revise(Guid id, ReviseBookingReservationRequest request, CancellationToken ct)
    {
        var row = await db.BookingReservations.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return NotFound();
        if (row.Status == BookingReservationStatus.Cancelled || row.Status == BookingReservationStatus.Superseded) return Conflict("A cancelled or superseded booking cannot be amended.");

        var previous = new { row.CollectionDate, row.DeliveryDate, row.CollectionDepot, row.DeliverySite, row.CollectionReference, row.CratePurchaseOrder, row.TransportPurchaseOrder, row.ReservedUnits, row.UnitType, row.CompositionJson, row.PlannerNotes, row.Status };
        if (request.CollectionDate is DateOnly collectionDate) row.CollectionDate = collectionDate;
        if (request.DeliveryDate.HasValue) row.DeliveryDate = request.DeliveryDate.Value;
        if (request.CollectionDepot is not null) row.CollectionDepot = Clip(request.CollectionDepot, 200);
        if (request.DeliverySite is not null) row.DeliverySite = Clip(request.DeliverySite, 200);
        if (request.CollectionReference is not null) row.CollectionReference = Clip(request.CollectionReference, 120);
        if (request.CratePurchaseOrder is not null) row.CratePurchaseOrder = Clip(request.CratePurchaseOrder, 120);
        if (request.TransportPurchaseOrder is not null) row.TransportPurchaseOrder = Clip(request.TransportPurchaseOrder, 120);
        if (request.ReservedUnits is decimal units)
        {
            if (units < 0) return BadRequest("ReservedUnits cannot be negative.");
            var activeAssigned = await db.BookingReservationAllocations
                .Where(item => item.BookingReservationId == id && item.IsActive)
                .SumAsync(item => (decimal?)item.Units, ct) ?? 0;
            if (units < activeAssigned)
                return Conflict($"Reserved capacity cannot be reduced below the {activeAssigned:0.##} units already matched to transport orders. Unmatch or amend the matches first.");
            row.ReservedUnits = units;
        }
        if (request.UnitType is not null) row.UnitType = Clip(request.UnitType, 40) ?? row.UnitType;
        if (request.Composition.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) row.CompositionJson = request.Composition.GetRawText();
        if (request.PlannerNotes is not null) row.PlannerNotes = Clip(request.PlannerNotes, 1000);
        row.CurrentRevisionNumber++;
        row.Status = BookingReservationStatus.Amended;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        var now = row.UpdatedAtUtc;
        var revision = NewRevision(row, request.SourceRowKey, request.SourceMessageId, request.SourceAttachmentIdentity, request.RevisionPayload, request.ChangeNote, User.Identity?.Name, now);
        db.BookingReservationRevisions.Add(revision);
        db.OperationalHistoryEvents.Add(Event(row.Id, "BookingReservation", "Amended", new { previous, current = new { row.CollectionDate, row.DeliveryDate, row.CollectionDepot, row.DeliverySite, row.CollectionReference, row.CratePurchaseOrder, row.TransportPurchaseOrder, row.ReservedUnits, row.UnitType, row.CompositionJson, row.PlannerNotes }, request.ChangeNote }, User.Identity?.Name, now));
        await db.SaveChangesAsync(ct);
        return Ok(new { id, row.CurrentRevisionNumber, status = row.Status.ToString() });
    }

    [HttpPost("{id:guid}/allocate")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, AllocateBookingReservationRequest request, CancellationToken ct)
    {
        var row = await db.BookingReservations.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return NotFound();
        if (row.Status == BookingReservationStatus.Cancelled || row.Status == BookingReservationStatus.Superseded) return Conflict("This booking is no longer assignable.");
        if (request.Units <= 0) return BadRequest("Units must be greater than zero.");
        var assigned = await db.BookingReservationAllocations.Where(item => item.BookingReservationId == id && item.IsActive).SumAsync(item => (decimal?)item.Units, ct) ?? 0;
        if (assigned + request.Units > row.ReservedUnits) return Conflict($"Allocation exceeds reserved capacity. Remaining: {row.ReservedUnits - assigned:0.##}.");
        if (request.TransportOrderId is Guid orderId && !await db.TransportOrders.AnyAsync(order => order.Id == orderId, ct)) return BadRequest("TransportOrderId was not found.");
        var allocation = new BookingReservationAllocation { BookingReservationId = id, TransportOrderId = request.TransportOrderId, Destination = Clip(request.Destination, 200), Units = request.Units, UnitType = Clip(request.UnitType, 40) ?? row.UnitType, Note = Clip(request.Note, 200), CreatedBy = User.Identity?.Name };
        db.BookingReservationAllocations.Add(allocation);
        row.Status = assigned + request.Units == row.ReservedUnits ? BookingReservationStatus.Assigned : BookingReservationStatus.PartiallyAssigned;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.OperationalHistoryEvents.Add(Event(id, "BookingReservation", "Allocated", new { allocation.Id, request.TransportOrderId, request.Destination, request.Units, row.Status }, User.Identity?.Name, row.UpdatedAtUtc));
        await db.SaveChangesAsync(ct);
        return Ok(new { allocation.Id, status = row.Status.ToString(), remainingUnits = row.ReservedUnits - assigned - request.Units });
    }

    [HttpGet("{id:guid}/candidate-orders")]
    public async Task<IActionResult> CandidateOrders(Guid id, CancellationToken ct)
    {
        var reservation = await db.BookingReservations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (reservation is null) return NotFound();
        if (reservation.Status is BookingReservationStatus.Cancelled or BookingReservationStatus.Superseded or BookingReservationStatus.Expired)
            return Conflict("This booking is no longer assignable.");
        var existingOrderIds = await db.BookingReservationAllocations.AsNoTracking()
            .Where(item => item.BookingReservationId == id && item.IsActive && item.TransportOrderId != null)
            .Select(item => item.TransportOrderId!.Value).ToListAsync(ct);
        var query = db.TransportOrders.AsNoTracking()
            .Where(order => order.CustomerCode == reservation.CustomerCode &&
                order.CollectionDate >= reservation.CollectionDate.AddDays(-1) &&
                order.CollectionDate <= reservation.CollectionDate.AddDays(1) &&
                order.Status != OrderStatus.Draft && order.Status != OrderStatus.Cancelled && order.Status != OrderStatus.Delivered);
        if (existingOrderIds.Count > 0) query = query.Where(order => !existingOrderIds.Contains(order.Id));
        var candidates = await query.OrderBy(order => order.CollectionDate).ThenBy(order => order.Reference).Take(100).ToListAsync(ct);
        var movementIds = candidates.Where(order => order.SourceMovementId is not null).Select(order => order.SourceMovementId!.Value).Distinct().ToArray();
        var revisionByMovement = movementIds.Length == 0
            ? new Dictionary<Guid, Guid>()
            : await db.OrderMovements.AsNoTracking().Where(movement => movementIds.Contains(movement.Id) && movement.CurrentRevisionId != null)
                .ToDictionaryAsync(movement => movement.Id, movement => movement.CurrentRevisionId!.Value, ct);
        var revisionIds = revisionByMovement.Values.ToArray();
        var sourceReferences = await db.OrderSourceLines.AsNoTracking().Where(line => revisionIds.Contains(line.RevisionId) && line.LoadReference != null)
            .Select(line => new { line.RevisionId, line.LoadReference }).ToListAsync(ct);
        var referenceByMovement = revisionByMovement.ToDictionary(
            pair => pair.Key,
            pair => sourceReferences.FirstOrDefault(source => source.RevisionId == pair.Value)?.LoadReference);
        return Ok(candidates
            .Select(order => new
            {
                order.Id, order.Reference, order.CustomerCode, order.CollectionDate, order.DeliveryDate, order.Pallets, order.Status, order.SellerName, order.MarketName,
                collectionReference = order.SourceMovementId is Guid movementId ? referenceByMovement.GetValueOrDefault(movementId) : null
            })
            .OrderByDescending(order => !string.IsNullOrWhiteSpace(reservation.CollectionReference) && string.Equals(order.collectionReference, reservation.CollectionReference, StringComparison.OrdinalIgnoreCase))
            .ThenBy(order => order.CollectionDate).ThenBy(order => order.Reference)
            .ToList());
    }

    [HttpPost("{id:guid}/match-order")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> MatchOrder(Guid id, MatchBookingOrderRequest request, CancellationToken ct)
    {
        var order = await db.TransportOrders.SingleOrDefaultAsync(item => item.Id == request.TransportOrderId, ct);
        if (order is null) return NotFound("TransportOrderId was not found.");
        if (order.Status is OrderStatus.Draft or OrderStatus.Cancelled or OrderStatus.Delivered) return Conflict("Only an approved, planned or live order can be matched to a retained booking.");
        var reservation = await db.BookingReservations.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (reservation is null) return NotFound();
        if (reservation.Status is BookingReservationStatus.Cancelled or BookingReservationStatus.Superseded or BookingReservationStatus.Expired)
            return Conflict("This booking is no longer assignable.");
        if (!string.Equals(order.CustomerCode, reservation.CustomerCode, StringComparison.OrdinalIgnoreCase)) return Conflict("The order customer does not match the retained booking.");
        if (order.CollectionDate < reservation.CollectionDate.AddDays(-1) || order.CollectionDate > reservation.CollectionDate.AddDays(1)) return Conflict("The order collection date is outside the retained booking window.");
        if (await db.BookingReservationAllocations.AnyAsync(item => item.BookingReservationId == id && item.IsActive && item.TransportOrderId == order.Id, ct))
            return Conflict("This transport order is already matched to the retained booking.");
        var units = request.Units ?? order.Pallets ?? 0;
        object result;
        try { result = await AllocateInternal(reservation, order.Id, units, order.Reference, request.Note, ct); }
        catch (InvalidOperationException exception) { return Conflict(exception.Message); }
        db.OperationalHistoryEvents.Add(Event(id, "BookingReservation", "MatchedToTransportOrder", new { order.Id, order.Reference, units }, User.Identity?.Name, reservation.UpdatedAtUtc));
        await db.SaveChangesAsync(ct);
        return Ok(result);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Cancel(Guid id, CancelBookingReservationRequest request, CancellationToken ct)
    {
        var row = await db.BookingReservations.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return NotFound();
        if (row.Status == BookingReservationStatus.Cancelled) return Ok(new { id, status = row.Status.ToString() });
        row.Status = BookingReservationStatus.Cancelled; row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        row.CurrentRevisionNumber++;
        db.BookingReservationRevisions.Add(NewRevision(row, null, null, null, JsonSerializer.SerializeToElement(new { request.Reason, cancelledAtUtc = row.UpdatedAtUtc }), request.Reason ?? "Cancelled by planner", User.Identity?.Name, row.UpdatedAtUtc));
        db.OperationalHistoryEvents.Add(Event(id, "BookingReservation", "Cancelled", new { request.Reason }, User.Identity?.Name, row.UpdatedAtUtc));
        await db.SaveChangesAsync(ct);
        return Ok(new { id, status = row.Status.ToString() });
    }

    [HttpPost("{id:guid}/unmatch")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Unmatch(Guid id, UnmatchBookingAllocationRequest request, CancellationToken ct)
    {
        var row = await db.BookingReservations.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (row is null) return NotFound();
        var allocation = await db.BookingReservationAllocations.SingleOrDefaultAsync(item => item.Id == request.AllocationId && item.BookingReservationId == id, ct);
        if (allocation is null) return NotFound("Booking allocation was not found.");
        if (!allocation.IsActive) return Ok(new { id, allocationId = allocation.Id, status = row.Status.ToString(), existing = true });

        allocation.IsActive = false;
        allocation.Note = string.Join(" | ", new[] { allocation.Note, request.Reason ?? "Unmatched by planner" }.Where(value => !string.IsNullOrWhiteSpace(value)));
        var assignedBeforeUnmatch = await db.BookingReservationAllocations.Where(item => item.BookingReservationId == id && item.IsActive).SumAsync(item => (decimal?)item.Units, ct) ?? 0;
        var assigned = Math.Max(0, assignedBeforeUnmatch - allocation.Units);
        row.Status = assigned == 0
            ? (row.SourceMovementId is not null ? BookingReservationStatus.Confirmed : BookingReservationStatus.PreOrder)
            : BookingReservationStatus.PartiallyAssigned;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.OperationalHistoryEvents.Add(Event(id, "BookingReservation", "UnmatchedFromTransportOrder", new { allocation.Id, allocation.TransportOrderId, allocation.Units, request.Reason }, User.Identity?.Name, row.UpdatedAtUtc));
        await db.SaveChangesAsync(ct);
        return Ok(new { id, allocationId = allocation.Id, status = row.Status.ToString(), remainingUnits = row.ReservedUnits - assigned });
    }

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct) => Ok(await db.OperationalHistoryEvents.AsNoTracking().Where(row => row.EntityType == "BookingReservation" && row.EntityId == id).OrderBy(row => row.OccurredAtUtc).ToListAsync(ct));

    [HttpGet("{id:guid}/source-evidence")]
    public async Task<IActionResult> SourceEvidence(Guid id, CancellationToken ct)
    {
        var reservation = await db.BookingReservations.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (reservation is null) return NotFound();
        if (reservation.SourceStagedImportId is not Guid stagedId) return NotFound(new { message = "This reservation has no retained staged source record." });
        var staged = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == stagedId, ct);
        if (staged is null) return NotFound(new { message = "The retained staged source record could not be found." });

        string? evidenceKey = null;
        string? messageId = null;
        try
        {
            using var payload = JsonDocument.Parse(staged.PayloadJson);
            evidenceKey = payload.RootElement.TryGetProperty("sourceEvidenceKey", out var key) ? key.GetString() : null;
            messageId = payload.RootElement.TryGetProperty("sourceMessageId", out var message) ? message.GetString() : null;
        }
        catch (JsonException) { }

        StagedImport? evidence = null;
        if (!string.IsNullOrWhiteSpace(evidenceKey))
            evidence = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == evidenceKey, ct);
        return Ok(new
        {
            reservationId = reservation.Id,
            stagingId = staged.Id,
            staged.Source,
            staged.ReceivedAtUtc,
            stagedPayloadJson = staged.PayloadJson,
            sourceMessageId = messageId,
            sourceEvidenceId = evidence?.Id,
            sourceEvidencePayloadJson = evidence?.PayloadJson
        });
    }

    private static BookingReservationRevision NewRevision(BookingReservation row, string? sourceRowKey, string? messageId, string? attachmentIdentity, JsonElement payload, string? note, string? actor, DateTimeOffset now) => new()
    {
        BookingReservationId = row.Id, RevisionNumber = row.CurrentRevisionNumber, Status = row.Status, SourceRowKey = Clip(sourceRowKey, 120), SourceMessageId = Clip(messageId, 500), SourceAttachmentIdentity = Clip(attachmentIdentity, 500), PayloadJson = payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : payload.GetRawText(), ChangeNote = Clip(note, 1000), Actor = Clip(actor, 200), CreatedAtUtc = now
    };

    private static OperationalHistoryEvent Event(Guid id, string type, string eventType, object payload, string? actor, DateTimeOffset now) => new() { EntityId = id, EntityType = type, EventType = eventType, Actor = Clip(actor, 200), PayloadJson = JsonSerializer.Serialize(payload), OccurredAtUtc = now };
    private static string BuildStableKey(string customer, DateOnly date, string? reference, string? cratePo, string? rowKey) => $"{customer.Trim().ToUpperInvariant()}|{date:yyyy-MM-dd}|{(reference ?? cratePo ?? rowKey ?? Guid.NewGuid().ToString("N")).Trim().ToUpperInvariant()}";
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
    private static BookingReservationDto ToDto(BookingReservation row, AllocationTotal? total) => new(row.Id, row.CustomerCode, row.BookingType, row.StableBookingKey, row.CollectionDate, row.DeliveryDate, row.CollectionDepot, row.DeliverySite, row.CollectionReference, row.CratePurchaseOrder, row.TransportPurchaseOrder, row.ReservedUnits, row.UnitType, row.Status.ToString(), row.CurrentRevisionNumber, total?.Units ?? 0, total?.Count ?? 0, row.SourceStagedImportId, row.SourceMovementId, row.UpdatedAtUtc);

    private async Task<object> AllocateInternal(BookingReservation row, Guid? orderId, decimal units, string? destination, string? note, CancellationToken ct)
    {
        if (units <= 0) throw new InvalidOperationException("Units must be greater than zero.");
        var assigned = await db.BookingReservationAllocations.Where(item => item.BookingReservationId == row.Id && item.IsActive).SumAsync(item => (decimal?)item.Units, ct) ?? 0;
        if (assigned + units > row.ReservedUnits) throw new InvalidOperationException($"Allocation exceeds reserved capacity. Remaining: {row.ReservedUnits - assigned:0.##}.");
        var allocation = new BookingReservationAllocation { BookingReservationId = row.Id, TransportOrderId = orderId, Destination = Clip(destination, 200), Units = units, UnitType = row.UnitType, Note = Clip(note, 200), CreatedBy = User.Identity?.Name };
        db.BookingReservationAllocations.Add(allocation);
        row.Status = assigned + units == row.ReservedUnits ? BookingReservationStatus.Assigned : BookingReservationStatus.PartiallyAssigned;
        row.UpdatedAtUtc = DateTimeOffset.UtcNow;
        return new { allocation.Id, status = row.Status.ToString(), remainingUnits = row.ReservedUnits - assigned - units };
    }
}

public sealed record BookingReservationDto(Guid Id, string CustomerCode, string BookingType, string StableBookingKey, DateOnly CollectionDate, DateOnly? DeliveryDate, string? CollectionDepot, string? DeliverySite, string? CollectionReference, string? CratePurchaseOrder, string? TransportPurchaseOrder, decimal ReservedUnits, string UnitType, string Status, int RevisionNumber, decimal AssignedUnits, int AllocationCount, Guid? SourceStagedImportId, Guid? SourceMovementId, DateTimeOffset UpdatedAtUtc);
public sealed record AllocationTotal(decimal Units, int Count);
public sealed record CreateBookingReservationRequest(string CustomerCode, string BookingType, DateOnly CollectionDate, DateOnly? DeliveryDate, decimal ReservedUnits, string? StableBookingKey = null, string? CollectionDepot = null, string? DeliverySite = null, string? CollectionReference = null, string? CratePurchaseOrder = null, string? TransportPurchaseOrder = null, string? UnitType = null, JsonElement Composition = default, string? PlannerNotes = null, BookingReservationStatus? Status = null, Guid? SourceStagedImportId = null, string? SourceRowKey = null, string? SourceMessageId = null, string? SourceAttachmentIdentity = null, JsonElement RevisionPayload = default);
public sealed record ReviseBookingReservationRequest(DateOnly? CollectionDate = null, DateOnly? DeliveryDate = null, decimal? ReservedUnits = null, string? CollectionDepot = null, string? DeliverySite = null, string? CollectionReference = null, string? CratePurchaseOrder = null, string? TransportPurchaseOrder = null, string? UnitType = null, JsonElement Composition = default, string? PlannerNotes = null, string? ChangeNote = null, string? SourceRowKey = null, string? SourceMessageId = null, string? SourceAttachmentIdentity = null, JsonElement RevisionPayload = default);
public sealed record AllocateBookingReservationRequest(decimal Units, Guid? TransportOrderId = null, string? Destination = null, string? UnitType = null, string? Note = null);
public sealed record CancelBookingReservationRequest(string? Reason = null);
public sealed record UnmatchBookingAllocationRequest(Guid AllocationId, string? Reason = null);
public sealed record MatchBookingOrderRequest(Guid TransportOrderId, decimal? Units = null, string? Note = null);
