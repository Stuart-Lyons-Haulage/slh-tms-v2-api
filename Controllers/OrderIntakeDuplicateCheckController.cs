using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/order-intake/duplicate-check")]
[Authorize]
public sealed class OrderIntakeDuplicateCheckController(
    TmsDbContext db,
    ILogger<OrderIntakeDuplicateCheckController> logger) : ControllerBase
{
    [HttpGet("staging/{stagingId:guid}/comparison")]
    [Authorize(Policy = "TmsRead")]
    public async Task<IActionResult> CompareStagedOrder(Guid stagingId, CancellationToken ct)
    {
        var staged = await db.StagedImports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == stagingId && x.EntityType == "order", ct);
        if (staged is null) return NotFound(new { message = "Staged order not found." });

        try
        {
            using var document = JsonDocument.Parse(staged.PayloadJson);
            var incoming = OrderSnapshot.FromPayload(document.RootElement);
            var reference = incoming.OrderReference ?? incoming.Po;
            if (string.IsNullOrWhiteSpace(reference))
                return Ok(new { classification = "New order", reference, changes = Array.Empty<object>(), current = (object?)null, incoming });

            TransportOrder? current;
            try
            {
                current = await db.TransportOrders.AsNoTracking()
                    .Where(x => x.Reference == reference && x.Status != OrderStatus.Cancelled)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .FirstOrDefaultAsync(ct);
            }
            catch (Exception ex) when (IsSchemaUnavailable(ex))
            {
                logger.LogWarning(ex,
                    "Staged-order comparison could not read live TransportOrders for staging item {StagingId}; comparison is unavailable and will not be reported as a new order.",
                    stagingId);
                return LiveOrderComparisonUnavailable();
            }

            if (current is null)
                return Ok(new { classification = "New order", reference, changes = Array.Empty<object>(), current = (object?)null, incoming });

            var existing = OrderSnapshot.FromLive(current);
            var changes = BuildChanges(existing, incoming);
            var classification = changes.Count == 0 ? "Exact duplicate" : "Amendment/update";
            return Ok(new
            {
                classification,
                reference,
                liveOrderId = current.Id,
                changes,
                current = existing,
                incoming
            });
        }
        catch (JsonException)
        {
            return UnprocessableEntity(new { message = "Staged order payload is not valid JSON." });
        }
    }

    [HttpPost]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Check([FromBody] OrderIntakeDuplicateCheckRequest request, CancellationToken ct)
    {
        var candidate = OrderSnapshot.FromRequest(request);
        var matches = new List<DuplicateMatch>();

        try
        {
            var staged = await db.StagedImports.AsNoTracking()
                .Where(x => x.EntityType == "order" &&
                    (x.Status == StagingStatus.PendingReview || x.Status == StagingStatus.Approved || x.Status == StagingStatus.Promoted))
                .OrderByDescending(x => x.ReceivedAtUtc)
                .Take(3000)
                .ToListAsync(ct);

            foreach (var item in staged)
            {
                try
                {
                    using var document = JsonDocument.Parse(item.PayloadJson);
                    var existing = OrderSnapshot.FromPayload(document.RootElement);
                    if (Classify(candidate, existing) is { } matchClassification)
                    {
                        matches.Add(new DuplicateMatch(
                            matchClassification,
                            matchClassification == "Possible duplicate" ? "Medium" : "High",
                            "staging",
                            item.Id.ToString(),
                            existing.OrderReference ?? existing.Po,
                            item.Status.ToString(),
                            existing.Customer,
                            existing.Po,
                            existing.CollectionDate,
                            existing.DeliveryDate,
                            existing.CollectionLocation,
                            existing.DeliveryLocation,
                            existing.Pallets,
                            item.ReceivedAtUtc));
                    }
                }
                catch (JsonException)
                {
                    // Malformed legacy evidence remains reviewable but must not block a new candidate.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Mailbox duplicate check could not read staged orders.");
        }

        try
        {
            var liveQuery = db.TransportOrders.AsNoTracking().Where(x => x.Status != OrderStatus.Cancelled);
            if (request.CollectionDate is not null)
            {
                var from = request.CollectionDate.Value.AddDays(-7);
                var to = request.CollectionDate.Value.AddDays(7);
                liveQuery = liveQuery.Where(x => x.CollectionDate >= from && x.CollectionDate <= to);
            }

            var live = await liveQuery.OrderByDescending(x => x.CreatedAtUtc).Take(3000).ToListAsync(ct);
            foreach (var order in live)
            {
                var existing = OrderSnapshot.FromLive(order);
                if (Classify(candidate, existing) is { } matchClassification)
                {
                    matches.Add(new DuplicateMatch(
                        matchClassification,
                        matchClassification == "Possible duplicate" ? "Medium" : "High",
                        "live-order",
                        order.Id.ToString(),
                        order.Reference,
                        order.Status.ToString(),
                        order.CustomerCode,
                        existing.Po,
                        order.CollectionDate,
                        order.DeliveryDate,
                        order.SellerName,
                        order.StallNumber,
                        order.Pallets,
                        order.CreatedAtUtc));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Mailbox duplicate check could not read live TransportOrders; staged-order matching remains available.");
        }

        var ordered = matches
            .OrderBy(x => Rank(x.Classification))
            .ThenByDescending(x => x.ObservedAtUtc)
            .Take(25)
            .ToList();
        var classification = ordered.FirstOrDefault()?.Classification ?? "New order";
        var confidence = ordered.FirstOrDefault()?.Confidence ?? "High";

        return Ok(new
        {
            classification,
            confidence,
            primaryIdentifier = string.IsNullOrWhiteSpace(candidate.Po) ? candidate.OrderReference : candidate.Po,
            matchCount = ordered.Count,
            matches = ordered,
            rule = "PO/purchase-order is the primary cross-message identity when available; otherwise customer/date/location/reference signatures are used conservatively. No record is deleted, promoted or amended by this endpoint."
        });
    }

    internal static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("no such table", StringComparison.OrdinalIgnoreCase);
    }

    internal static ObjectResult LiveOrderComparisonUnavailable() => new(new
    {
        code = "LiveOrderComparisonUnavailable",
        message = "The live order register could not be checked. Do not treat this staged order as new until comparison is available."
    })
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable
    };

    private static List<object> BuildChanges(OrderSnapshot current, OrderSnapshot incoming)
    {
        var changes = new List<object>();
        AddChange(changes, "Customer", current.Customer, incoming.Customer);
        AddChange(changes, "Collection date", current.CollectionDate?.ToString("yyyy-MM-dd"), incoming.CollectionDate?.ToString("yyyy-MM-dd"));
        AddChange(changes, "Delivery date", current.DeliveryDate?.ToString("yyyy-MM-dd"), incoming.DeliveryDate?.ToString("yyyy-MM-dd"));
        AddChange(changes, "Collection site", current.CollectionLocation, incoming.CollectionLocation);
        AddChange(changes, "Delivery site", current.DeliveryLocation, incoming.DeliveryLocation);
        AddChange(changes, "Pallets", current.Pallets?.ToString(), incoming.Pallets?.ToString());
        return changes;
    }

    private static void AddChange(List<object> changes, string field, string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(to)) return;
        if (Normalise(from) == Normalise(to)) return;
        changes.Add(new { field, from = string.IsNullOrWhiteSpace(from) ? "—" : from, to });
    }

    internal static string? Classify(OrderSnapshot candidate, OrderSnapshot existing)
    {
        var candidatePo = Normalise(candidate.Po);
        var existingPo = Normalise(existing.Po);
        var strongPoMatch = candidatePo.Length >= 3 && existingPo.Length >= 3 &&
            (candidatePo == existingPo || Normalise(existing.OrderReference).StartsWith(candidatePo, StringComparison.OrdinalIgnoreCase));

        if (strongPoMatch)
        {
            if (CompleteComparable(candidate) && CompleteComparable(existing) && EquivalentCore(candidate, existing))
                return "Exact duplicate";
            if (HasMaterialConflict(candidate, existing))
                return "Amendment/update";
            return "Possible duplicate";
        }

        if (CompleteFallback(candidate) && CompleteFallback(existing) && FallbackSignature(candidate) == FallbackSignature(existing))
            return "Exact duplicate";

        var score = SimilarityScore(candidate, existing);
        return score >= 5 ? "Possible duplicate" : null;
    }

    private static bool CompleteComparable(OrderSnapshot value) =>
        !string.IsNullOrWhiteSpace(value.Customer) && value.CollectionDate is not null && value.DeliveryDate is not null &&
        !string.IsNullOrWhiteSpace(value.CollectionLocation) && !string.IsNullOrWhiteSpace(value.DeliveryLocation) && value.Pallets is not null;

    private static bool CompleteFallback(OrderSnapshot value) =>
        !string.IsNullOrWhiteSpace(value.Customer) && value.CollectionDate is not null && value.DeliveryDate is not null &&
        !string.IsNullOrWhiteSpace(value.CollectionLocation) && !string.IsNullOrWhiteSpace(value.DeliveryLocation) &&
        !string.IsNullOrWhiteSpace(value.OrderReference);

    private static bool EquivalentCore(OrderSnapshot left, OrderSnapshot right) =>
        Same(left.Customer, right.Customer) && left.CollectionDate == right.CollectionDate && left.DeliveryDate == right.DeliveryDate &&
        Same(left.CollectionLocation, right.CollectionLocation) && Same(left.DeliveryLocation, right.DeliveryLocation) && left.Pallets == right.Pallets;

    private static bool HasMaterialConflict(OrderSnapshot left, OrderSnapshot right)
    {
        if (!BothBlankOrSame(left.Customer, right.Customer)) return true;
        if (left.CollectionDate is not null && right.CollectionDate is not null && left.CollectionDate != right.CollectionDate) return true;
        if (left.DeliveryDate is not null && right.DeliveryDate is not null && left.DeliveryDate != right.DeliveryDate) return true;
        if (!BothBlankOrSame(left.CollectionLocation, right.CollectionLocation)) return true;
        if (!BothBlankOrSame(left.DeliveryLocation, right.DeliveryLocation)) return true;
        if (left.Pallets is not null && right.Pallets is not null && left.Pallets != right.Pallets) return true;
        return false;
    }

    private static int SimilarityScore(OrderSnapshot left, OrderSnapshot right)
    {
        var score = 0;
        if (SameNonBlank(left.Customer, right.Customer)) score++;
        if (left.CollectionDate is not null && left.CollectionDate == right.CollectionDate) score++;
        if (left.DeliveryDate is not null && left.DeliveryDate == right.DeliveryDate) score++;
        if (SameNonBlank(left.CollectionLocation, right.CollectionLocation)) score++;
        if (SameNonBlank(left.DeliveryLocation, right.DeliveryLocation)) score++;
        if (SameNonBlank(left.OrderReference, right.OrderReference)) score += 2;
        if (left.Pallets is not null && left.Pallets == right.Pallets) score++;
        return score;
    }

    private static string FallbackSignature(OrderSnapshot value) => string.Join('|', new[]
    {
        Normalise(value.Customer), value.CollectionDate?.ToString("yyyyMMdd") ?? string.Empty,
        value.DeliveryDate?.ToString("yyyyMMdd") ?? string.Empty, Normalise(value.CollectionLocation),
        Normalise(value.DeliveryLocation), Normalise(value.OrderReference)
    });

    private static bool Same(string? left, string? right) => Normalise(left) == Normalise(right);
    private static bool SameNonBlank(string? left, string? right) => Normalise(left).Length > 0 && Same(left, right);
    private static bool BothBlankOrSame(string? left, string? right)
    {
        var a = Normalise(left); var b = Normalise(right);
        return a.Length == 0 || b.Length == 0 || a == b;
    }
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static int Rank(string classification) => classification switch
    {
        "Exact duplicate" => 0,
        "Amendment/update" => 1,
        "Possible duplicate" => 2,
        _ => 3
    };

    internal sealed record OrderSnapshot(
        string? Customer,
        string? Po,
        string? OrderReference,
        DateOnly? CollectionDate,
        DateOnly? DeliveryDate,
        string? CollectionLocation,
        string? DeliveryLocation,
        int? Pallets)
    {
        internal static OrderSnapshot FromRequest(OrderIntakeDuplicateCheckRequest request) => new(
            request.Customer,
            request.Po ?? request.PurchaseOrder,
            request.OrderReference,
            request.CollectionDate,
            request.DeliveryDate,
            request.CollectionLocation,
            request.DeliveryLocation,
            request.Pallets);

        internal static OrderSnapshot FromLive(TransportOrder order) => new(
            order.CustomerCode,
            order.Reference,
            order.Reference,
            order.CollectionDate,
            order.DeliveryDate,
            order.SellerName,
            order.StallNumber,
            order.Pallets);

        internal static OrderSnapshot FromPayload(JsonElement payload) => new(
            Text(payload, "customer") ?? Text(payload, "customerCode") ?? Text(payload, "customer_supplier"),
            Text(payload, "customerPo") ?? Text(payload, "po") ?? Text(payload, "purchaseOrder") ?? Text(payload, "purchase_order") ?? Text(payload, "poNumber"),
            Text(payload, "orderReference") ?? Text(payload, "order_reference") ?? Text(payload, "poNumber"),
            Date(payload, "collectionDate") ?? Date(payload, "collection_date"),
            Date(payload, "deliveryDate") ?? Date(payload, "delivery_date"),
            Text(payload, "collectionLocation") ?? Text(payload, "collection_location") ?? Text(payload, "collectionSite") ?? Text(payload, "collection_site") ?? Text(payload, "sellerName"),
            Text(payload, "deliveryLocation") ?? Text(payload, "delivery_location") ?? Text(payload, "deliverySite") ?? Text(payload, "delivery_site") ?? Text(payload, "stallNumber"),
            Int(payload, "pallets") ?? Int(payload, "palletQty") ?? Int(payload, "palletQuantity"));

        private static string? Text(JsonElement payload, string name)
        {
            if (!Try(payload, name, out var value)) return null;
            return value.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            };
        }
        private static int? Int(JsonElement payload, string name) => int.TryParse(Text(payload, name), out var value) ? value : null;
        private static DateOnly? Date(JsonElement payload, string name) => DateOnly.TryParse(Text(payload, name), out var value) ? value : null;
        private static bool Try(JsonElement payload, string name, out JsonElement value)
        {
            if (payload.TryGetProperty(name, out value)) return true;
            foreach (var property in payload.EnumerateObject())
                if (Normalise(property.Name) == Normalise(name)) { value = property.Value; return true; }
            value = default;
            return false;
        }
    }
}

public sealed record OrderIntakeDuplicateCheckRequest(
    string? Customer,
    string? Po,
    string? PurchaseOrder,
    string? OrderReference,
    DateOnly? CollectionDate,
    DateOnly? DeliveryDate,
    string? CollectionLocation,
    string? DeliveryLocation,
    int? Pallets,
    string? SourceMessageId = null,
    string? SourceAttachmentName = null);

public sealed record DuplicateMatch(
    string Classification,
    string Confidence,
    string Source,
    string RecordId,
    string? Reference,
    string Status,
    string? Customer,
    string? Po,
    DateOnly? CollectionDate,
    DateOnly? DeliveryDate,
    string? CollectionLocation,
    string? DeliveryLocation,
    int? Pallets,
    DateTimeOffset ObservedAtUtc);
