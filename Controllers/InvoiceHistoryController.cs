using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/invoice-history"), Authorize]
public sealed class InvoiceHistoryController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] string? customerCode, [FromQuery] string? status, [FromQuery] Guid? loadId, [FromQuery] Guid? transportOrderId, CancellationToken ct)
    {
        var query = db.InvoiceRecords.AsNoTracking();
        if (from is DateOnly start) query = query.Where(row => row.InvoiceDate >= start);
        if (to is DateOnly end) query = query.Where(row => row.InvoiceDate <= end);
        if (!string.IsNullOrWhiteSpace(customerCode)) query = query.Where(row => row.CustomerCode == customerCode);
        if (Enum.TryParse<InvoiceRecordStatus>(status, true, out var parsedStatus)) query = query.Where(row => row.Status == parsedStatus);
        if (loadId is Guid linkedLoad) query = query.Where(row => row.LoadId == linkedLoad);
        if (transportOrderId is Guid linkedOrder) query = query.Where(row => row.TransportOrderId == linkedOrder);

        var records = await query.OrderByDescending(row => row.InvoiceDate).ThenByDescending(row => row.CreatedAtUtc).Take(1000).ToListAsync(ct);
        var ids = records.Select(row => row.Id).ToArray();
        var lines = await db.InvoiceRecordLines.AsNoTracking().Where(row => ids.Contains(row.InvoiceRecordId)).ToListAsync(ct);
        var history = await db.OperationalHistoryEvents.AsNoTracking().Where(item => item.EntityType == "InvoiceRecord" && ids.Contains(item.EntityId)).OrderBy(item => item.OccurredAtUtc).ToListAsync(ct);
        return Ok(records.Select(row => new { record = row, lines = lines.Where(line => line.InvoiceRecordId == row.Id).OrderBy(line => line.Id).ToArray(), history = history.Where(item => item.EntityId == row.Id).ToArray() }).ToArray());
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var record = await db.InvoiceRecords.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, ct);
        if (record is null) return NotFound();
        var lines = await db.InvoiceRecordLines.AsNoTracking().Where(row => row.InvoiceRecordId == id).OrderBy(row => row.Id).ToListAsync(ct);
        var history = await db.OperationalHistoryEvents.AsNoTracking().Where(row => row.EntityType == "InvoiceRecord" && row.EntityId == id).OrderBy(row => row.OccurredAtUtc).ToListAsync(ct);
        return Ok(new { record, lines, history });
    }

    [HttpPost]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Create(CreateInvoiceRecordRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CustomerCode)) return BadRequest("CustomerCode is required.");
        if (request.NetAmount < 0 || request.VatAmount < 0 || request.GrossAmount < 0) return BadRequest("Invoice amounts cannot be negative.");
        if (request.LoadId is Guid loadId && !await db.Loads.AnyAsync(row => row.Id == loadId, ct)) return BadRequest("LoadId was not found.");
        if (request.TransportOrderId is Guid orderId && !await db.TransportOrders.AnyAsync(row => row.Id == orderId, ct)) return BadRequest("TransportOrderId was not found.");
        if (request.BookingReservationId is Guid reservationId && !await db.BookingReservations.AnyAsync(row => row.Id == reservationId, ct)) return BadRequest("BookingReservationId was not found.");

        var now = DateTimeOffset.UtcNow;
        var record = new InvoiceRecord
        {
            InvoiceNumber = Clip(request.InvoiceNumber, 120), CustomerCode = request.CustomerCode.Trim(), LoadId = request.LoadId,
            TransportOrderId = request.TransportOrderId, BookingReservationId = request.BookingReservationId, InvoiceDate = request.InvoiceDate,
            NetAmount = request.NetAmount, VatAmount = request.VatAmount, GrossAmount = request.GrossAmount,
            Status = request.Status ?? InvoiceRecordStatus.Draft, Notes = Clip(request.Notes, 2000), PayloadJson = request.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : request.Payload.GetRawText(),
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.InvoiceRecords.Add(record);
        foreach (var line in request.Lines ?? [])
        {
            if (line.BookingReservationId is Guid lineReservationId && !await db.BookingReservations.AnyAsync(row => row.Id == lineReservationId, ct)) return BadRequest("A line BookingReservationId was not found.");
            db.InvoiceRecordLines.Add(new InvoiceRecordLine { InvoiceRecordId = record.Id, LoadId = line.LoadId, TransportOrderId = line.TransportOrderId, BookingReservationId = line.BookingReservationId, Description = Clip(line.Description, 500), Quantity = line.Quantity, UnitAmount = line.UnitAmount, NetAmount = line.NetAmount, OperationalReferenceSnapshot = Clip(line.OperationalReferenceSnapshot, 500) });
        }
        db.OperationalHistoryEvents.Add(Event(record.Id, "InvoiceRecord", "Created", new { record.InvoiceNumber, record.CustomerCode, record.LoadId, record.TransportOrderId, record.BookingReservationId, record.Status }, now));
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = record.Id }, new { id = record.Id });
    }

    [HttpPost("{id:guid}/status")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SetStatus(Guid id, SetInvoiceStatusRequest request, CancellationToken ct)
    {
        var record = await db.InvoiceRecords.SingleOrDefaultAsync(row => row.Id == id, ct);
        if (record is null) return NotFound();
        if (!Enum.TryParse<InvoiceRecordStatus>(request.Status, true, out var status)) return BadRequest("Unknown invoice status.");
        var previous = record.Status;
        record.Status = status; record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        db.OperationalHistoryEvents.Add(Event(id, "InvoiceRecord", "StatusChanged", new { previous, current = status, request.Note }, record.UpdatedAtUtc));
        await db.SaveChangesAsync(ct);
        return Ok(new { id, status = status.ToString() });
    }

    private static OperationalHistoryEvent Event(Guid id, string type, string eventType, object payload, DateTimeOffset now) => new() { EntityId = id, EntityType = type, EventType = eventType, Actor = null, PayloadJson = JsonSerializer.Serialize(payload), OccurredAtUtc = now };
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}

public sealed record CreateInvoiceRecordRequest(string CustomerCode, DateOnly InvoiceDate, decimal NetAmount, decimal VatAmount, decimal GrossAmount, string? InvoiceNumber = null, Guid? LoadId = null, Guid? TransportOrderId = null, Guid? BookingReservationId = null, InvoiceRecordStatus? Status = null, string? Notes = null, JsonElement Payload = default, IReadOnlyList<CreateInvoiceLineRequest>? Lines = null);
public sealed record CreateInvoiceLineRequest(string? Description, decimal Quantity, decimal UnitAmount, decimal NetAmount, Guid? LoadId = null, Guid? TransportOrderId = null, Guid? BookingReservationId = null, string? OperationalReferenceSnapshot = null);
public sealed record SetInvoiceStatusRequest(string Status, string? Note = null);
