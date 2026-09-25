using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;
[ApiController, Route("api/v1/staging")]
[Authorize]
public sealed class StagingController(TmsDbContext db, StagingService service) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] StagingStatus? status,
        [FromQuery] string? entityType,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        // Review is an operational queue, not an export. Keeping this bounded stops a
        // large historical staging table (and its source JSON) from timing out the UI.
        take = Math.Clamp(take, 1, 200);
        var query = db.StagedImports.AsNoTracking().AsQueryable();
        query = query.Where(x => x.Status == (status ?? StagingStatus.PendingReview));
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType.Trim().ToLowerInvariant());

        return Ok(await query.OrderByDescending(x => x.ReceivedAtUtc).Take(take).ToListAsync(ct));
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Stage(StageImportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey is required", HttpContext.TraceIdentifier));
        if (request.IdempotencyKey.Length > 200) return BadRequest(new ErrorResponse("invalid_idempotency_key", "IdempotencyKey must be 200 characters or fewer.", HttpContext.TraceIdentifier));
        if (IsExplicitZeroPalletOrder(request))
            return Ok(new { ignored = true, reason = "zero_pallet_order", message = "The source row has zero pallets and was retained as source evidence rather than staged as a transport order." });
        var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == request.IdempotencyKey, ct);
        if (existing is not null)
        {
            TmsMetrics.Shared.RecordImportBatch(1, 1, "staging_single");
            return Ok(service.ToResponse(existing, Request));
        }
        try
        {
            var item = service.Create(request);
            db.StagedImports.Add(item);
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Received"));
            await db.SaveChangesAsync(ct);
            TmsMetrics.Shared.RecordImportBatch(1, 0, "staging_single");
            return Accepted(service.ToResponse(item, Request));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse("invalid_staging_record", ex.Message, HttpContext.TraceIdentifier));
        }
    }

    [HttpPost("batch"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> StageBatch(List<StageImportRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0 || requests.Count > 500) return BadRequest(new ErrorResponse("invalid_batch", "Submit between 1 and 500 records.", HttpContext.TraceIdentifier));
        if (requests.Any(request => string.IsNullOrWhiteSpace(request.IdempotencyKey))) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every record needs an IdempotencyKey.", HttpContext.TraceIdentifier));
        if (requests.Any(request => request.IdempotencyKey.Length > 200)) return BadRequest(new ErrorResponse("invalid_idempotency_key", "Every IdempotencyKey must be 200 characters or fewer.", HttpContext.TraceIdentifier));
        var filteredRequests = requests.Where(request => !IsExplicitZeroPalletOrder(request)).ToList();
        var skippedZeroPallets = requests.Count - filteredRequests.Count;
        if (filteredRequests.Count == 0)
            return Accepted(new { received = requests.Count, existing = 0, created = 0, skippedZeroPallets, records = Array.Empty<StageImportResponse>() });
        var keys = filteredRequests.Select(request => request.IdempotencyKey).ToList();
        if (keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keys.Count) return BadRequest(new ErrorResponse("duplicate_batch_key", "Idempotency keys must be unique within the batch.", HttpContext.TraceIdentifier));
        var existing = await db.StagedImports.AsNoTracking().Where(item => keys.Contains(item.IdempotencyKey)).ToDictionaryAsync(item => item.IdempotencyKey, ct);
        var existingCount = existing.Count;
        var responses = new List<StageImportResponse>();
        try
        {
            foreach (var request in filteredRequests)
            {
                if (existing.TryGetValue(request.IdempotencyKey, out var item)) responses.Add(service.ToResponse(item, Request));
                else
                {
                    var created = service.Create(request);
                    db.StagedImports.Add(created);
                    db.StagedImportEvents.Add(StagingAudit.Create(created, "Received"));
                    responses.Add(service.ToResponse(created, Request));
                }
            }
            await db.SaveChangesAsync(ct);
            TmsMetrics.Shared.RecordImportBatch(filteredRequests.Count, existingCount, "staging_batch");
            return Accepted(new { received = requests.Count, existing = existingCount, created = responses.Count - existingCount, skippedZeroPallets, records = responses });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorResponse("invalid_staging_record", ex.Message, HttpContext.TraceIdentifier));
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        (await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct)) is { } x ? Ok(x) : NotFound();

    [HttpGet("{id:guid}/history")]
    public async Task<IActionResult> History(Guid id, CancellationToken ct)
    {
        if (!await db.StagedImports.AsNoTracking().AnyAsync(x => x.Id == id, ct)) return NotFound();
        var events = await db.StagedImportEvents.AsNoTracking()
            .Where(x => x.StagedImportId == id)
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);
        return Ok(events.Select(x => new
        {
            x.Id,
            x.StagedImportId,
            x.EventType,
            previousStatus = x.PreviousStatus?.ToString(),
            newStatus = x.NewStatus.ToString(),
            payload = JsonSerializer.Deserialize<JsonElement>(x.PayloadJson),
            x.Note,
            x.Actor,
            x.OccurredAtUtc
        }));
    }

    [HttpPost("pending/archive"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ArchivePending(ArchivePendingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ErrorResponse("archive_reason_required", "A reason is required to archive pending records.", HttpContext.TraceIdentifier));

        var pending = await db.StagedImports.Where(item => item.Status == StagingStatus.PendingReview).ToListAsync(ct);
        var actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "authorised user";
        foreach (var item in pending)
        {
            var previous = item.Status;
            item.Status = StagingStatus.Archived;
            item.ReviewedAtUtc = DateTimeOffset.UtcNow;
            item.ReviewedBy = actor;
            item.ReviewNote = request.Reason.Trim();
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Archived", previous, item.ReviewNote, actor));
        }
        await db.SaveChangesAsync(ct);
        return Ok(new { archived = pending.Count });
    }

    [HttpDelete("pending"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> ClearPending([FromQuery] string confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm, "CLEAR-PENDING", StringComparison.Ordinal))
            return BadRequest(new ErrorResponse("confirmation_required", "Add confirm=CLEAR-PENDING to clear pending staging records.", HttpContext.TraceIdentifier));

        return await ArchivePending(new ArchivePendingRequest("Archived through the legacy clear-pending operation."), ct);
    }

    [HttpPost("{id:guid}/approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Approve(Guid id, ReviewRequest request, CancellationToken ct)
    {
        var staged = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (staged is null) return NotFound();
        if (staged.EntityType == "order")
        {
            var readinessIssue = await OrderReadinessIssue(staged, ct);
            if (readinessIssue is not null)
                return BadRequest(new ErrorResponse("order_not_ready", readinessIssue, HttpContext.TraceIdentifier));
        }
        if (staged.EntityType == "order" && IsExplicitPreOrder(staged.PayloadJson))
        {
            return BadRequest(new ErrorResponse(
                "preorder_not_ready",
                "This NWF pre-order is awaiting customer instruction and cannot be accepted into live planning yet. A later NWF tracker snapshot will enrich/supersede it automatically.",
                HttpContext.TraceIdentifier));
        }

        try { return Ok(await service.ReviewAndPromote(id, true, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", ex.Message, HttpContext.TraceIdentifier)); }
        catch (DbUpdateException ex) { return BadRequest(new ErrorResponse("staging_promotion_failed", $"The order could not be approved because the planning schema is incomplete: {ex.GetBaseException().Message}", HttpContext.TraceIdentifier)); }
    }

    [HttpPost("{id:guid}/reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, ReviewRequest request, CancellationToken ct)
    {
        try { return Ok(await service.ReviewAndPromote(id, false, request.Note, User, ct)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex)
        {
            return Conflict(new ErrorResponse("staging_already_reviewed", ex.Message, HttpContext.TraceIdentifier));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ErrorResponse("staging_review_conflict", "This staged item was reviewed by another user. Refresh the review queue.", HttpContext.TraceIdentifier));
        }
        catch (DbUpdateException ex)
        {
            return UnprocessableEntity(new ErrorResponse("staging_review_persistence_failed", $"The rejection could not be recorded: {ex.GetBaseException().Message}", HttpContext.TraceIdentifier));
        }
    }

    private static bool IsExplicitZeroPalletOrder(StageImportRequest request)
    {
        if (!string.Equals(request.EntityType, "order", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGetProperty(request.Payload, "pallets", out var pallets)
            && !TryGetProperty(request.Payload, "palletQty", out pallets)
            && !TryGetProperty(request.Payload, "palletQuantity", out pallets))
            return false;

        return pallets.ValueKind switch
        {
            JsonValueKind.Number => pallets.TryGetDecimal(out var number) && number <= 0,
            JsonValueKind.String => decimal.TryParse(pallets.GetString(), out var number) && number <= 0,
            _ => false
        };
    }

    private async Task<string?> OrderReadinessIssue(StagedImport staged, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(staged.PayloadJson);
        var payload = document.RootElement;
        var reference = Text(payload, "poNumber");
        var customerCode = Text(payload, "customerCode");
        var collection = Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName");
        var delivery = Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "destination") ?? Text(payload, "stallNumber");
        if (string.IsNullOrWhiteSpace(reference)) return "Order reference is missing.";
        if (string.IsNullOrWhiteSpace(customerCode)) return "Customer is missing from the order master data.";
        if (!await db.Customers.AsNoTracking().AnyAsync(item => item.Active && item.Code == customerCode, ct)) return $"Customer {customerCode} is not present in active Customer Master.";
        if (!DateOnly.TryParse(Text(payload, "collectionDate"), out _)) return "Collection date is missing or invalid.";
        if (string.IsNullOrWhiteSpace(collection)) return "Collection point is missing.";
        if (string.IsNullOrWhiteSpace(delivery)) return "Delivery point is missing.";
        var pallets = Text(payload, "pallets") ?? Text(payload, "palletQty") ?? Text(payload, "palletQuantity") ?? Text(payload, "quantity");
        var backhaul = (Text(payload, "jobType") ?? string.Empty).Contains("backhaul", StringComparison.OrdinalIgnoreCase) ||
                       (Text(payload, "jobType") ?? string.Empty).Contains("backload", StringComparison.OrdinalIgnoreCase);
        if (!backhaul && (!int.TryParse(pallets, out var palletCount) || palletCount <= 0))
            return "Pallet quantity is missing or not greater than zero.";
        if (!IsTrue(payload, "plannerReady"))
            return "The parser has not marked this order planner-ready; review the source evidence first.";
        if (!string.Equals(Text(payload, "intakeConfidence"), "High", StringComparison.OrdinalIgnoreCase))
            return "Intake confidence is not High; explicit source review is required.";
        if (payload.TryGetProperty("intakeWarnings", out var warnings) &&
            warnings.ValueKind == JsonValueKind.Array &&
            warnings.GetArrayLength() > 0)
            return "Source/intake warnings remain; resolve them before approval.";

        var alignment = await OrderSiteMasterAlignment.ResolveAsync(db, payload, ct);
        if (!alignment.CollectionMatched)
            return $"Collection point '{collection}' is not matched to active Site Master.";
        if (!alignment.DeliveryMatched)
            return $"Delivery point '{delivery}' is not matched to active Site Master or Market Master.";

        return null;
    }

    private static string? Text(JsonElement payload, string name)
    {
        if (!payload.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : value.ValueKind == JsonValueKind.Number
                ? value.GetRawText()
                : null;
    }

    private static bool IsTrue(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        (value.ValueKind == JsonValueKind.True ||
         value.ValueKind == JsonValueKind.String &&
         bool.TryParse(value.GetString(), out var parsed) &&
         parsed);

    private static bool IsExplicitPreOrder(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (TryGetProperty(root, "plannerReady", out var plannerReady) && plannerReady.ValueKind == JsonValueKind.False)
                return true;
            if (TryGetProperty(root, "intakeStatus", out var status) && status.ValueKind == JsonValueKind.String &&
                string.Equals(status.GetString(), "PreOrder", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

public sealed record ArchivePendingRequest(string Reason);
