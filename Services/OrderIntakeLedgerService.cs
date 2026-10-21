using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class OrderIntakeLedgerService(TmsDbContext db)
{
    public async Task<object> ListAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 2000);
        var staged = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == "order")
            .OrderByDescending(x => x.ReceivedAtUtc).Take(take).ToListAsync(ct);
        var stagedIds = staged.Select(x => x.Id).ToList();
        var revisions = await db.OrderRevisions.AsNoTracking().Where(x => stagedIds.Contains(x.StagedImportId)).ToListAsync(ct);
        var movementIds = revisions.Select(x => x.MovementId).Distinct().ToList();
        var movements = await db.OrderMovements.AsNoTracking().Where(x => movementIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var revisionIds = revisions.Select(x => x.Id).ToList();
        var lines = await db.OrderSourceLines.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
        var linkedOrders = await db.TransportOrders.AsNoTracking().Where(x => stagedIds.Contains(x.SourceStagedImportId ?? Guid.Empty)).ToListAsync(ct);
        var linkedOrderIds = linkedOrders.Select(x => x.Id).ToList();
        var plannedOrderIds = (await db.LoadStops.AsNoTracking().Where(x => x.OrderId != null && linkedOrderIds.Contains(x.OrderId.Value))
            .Select(x => x.OrderId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var revisionByStaged = revisions.GroupBy(x => x.StagedImportId).ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.RevisionNumber).First());

        var records = staged.Select(item =>
        {
            revisionByStaged.TryGetValue(item.Id, out var revision);
            var movement = revision is not null && movements.TryGetValue(revision.MovementId, out var found) ? found : null;
            List<OrderSourceLine> sourceLines = revision is null ? [] : lines.Where(x => x.RevisionId == revision.Id).ToList();
            using var payload = Parse(item.PayloadJson);
            var orders = linkedOrders.Where(x => x.SourceStagedImportId == item.Id).ToList();
            return new
            {
                stagedImportId = item.Id,
                movementId = movement?.Id,
                messageId = revision?.MessageId ?? Text(payload.RootElement, "sourceEmailMessageId", "messageId"),
                attachmentIdentity = revision?.AttachmentIdentity,
                sender = Text(payload.RootElement, "sourceSender"),
                recipients = Raw(payload.RootElement, "sourceToRecipients"),
                subject = Text(payload.RootElement, "sourceEmailSubject"),
                attachmentFilenames = ArrayTexts(payload.RootElement, "sourceAttachments", "name"),
                attachmentVersion = Text(payload.RootElement, "attachmentVersion", "parserVersion"),
                customer = movement?.CustomerCode ?? Text(payload.RootElement, "customerCode"),
                sourceOrderReference = Text(payload.RootElement, "poNumber", "reference", "loadReference", "transportPo"),
                collectionDate = Text(payload.RootElement, "collectionDate"),
                deliveryDate = Text(payload.RootElement, "deliveryDate"),
                collectionSite = Text(payload.RootElement, "collectionSite", "collectionLocation", "sellerName"),
                destination = Text(payload.RootElement, "deliverySite", "deliveryLocation", "marketName"),
                intermediateStops = Raw(payload.RootElement, "intermediateStops", "movementLegs"),
                quantity = Number(payload.RootElement, "pallets", "quantity", "units"),
                unit = Text(payload.RootElement, "handlingUnitType", "unit", "palletType"),
                movementClassification = Text(payload.RootElement, "movementClassification", "movementType", "orderType"),
                item.ReceivedAtUtc,
                stagingStatus = item.Status.ToString(),
                lifecycleStatus = movement?.LifecycleStatus.ToString(),
                revisionNumber = revision?.RevisionNumber,
                revision?.ParserTemplate,
                revision?.ParserVersion,
                sourceLineCount = sourceLines.Count,
                palletsReceived = sourceLines.Sum(x => x.Pallets ?? 0),
                validationErrors = ReadWarnings(payload.RootElement),
                linkedTmsOrderIds = orders.Select(x => x.Id).ToList(),
                plannerStatus = orders.Count == 0 ? "NoLiveOrder" : orders.All(x => plannedOrderIds.Contains(x.Id)) ? "SavedRun" : "Unplanned",
                sourceLines = sourceLines.Select(x => new { x.SourceRowKey, x.CollectionSite, x.DeliverySite, x.CollectionDate, x.DeliveryDate, x.Pallets, x.PalletType, x.LoadReference }),
                item.ReviewedAtUtc,
                item.ReviewedBy,
                item.ReviewNote
            };
        }).ToList();
        return new { count = records.Count, records };
    }

    public async Task<(StagedImport Item, OrderRevision? Revision, OrderMovement? Movement)> FindAsync(Guid stagedImportId, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleOrDefaultAsync(x => x.Id == stagedImportId && x.EntityType == "order", ct)
            ?? throw new KeyNotFoundException("Order intake record not found.");
        var revision = await db.OrderRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.StagedImportId == stagedImportId, ct);
        var movement = revision is null ? null : await db.OrderMovements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == revision.MovementId, ct);
        return (item, revision, movement);
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return JsonDocument.Parse("{}"); }
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static string? Raw(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                return value.GetRawText();
        return null;
    }

    private static decimal? Number(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && (value.TryGetDecimal(out var number) || value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), out number)))
                return number;
        return null;
    }

    private static IReadOnlyList<string> ArrayTexts(JsonElement root, string arrayName, string propertyName)
    {
        if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array) return [];
        return array.EnumerateArray().Select(x => x.TryGetProperty(propertyName, out var value) ? value.GetString() : null)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
    }

    private static IReadOnlyList<string> ReadWarnings(JsonElement root)
    {
        if (!root.TryGetProperty("warnings", out var warnings) || warnings.ValueKind != JsonValueKind.Array) return [];
        return warnings.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
    }
}
