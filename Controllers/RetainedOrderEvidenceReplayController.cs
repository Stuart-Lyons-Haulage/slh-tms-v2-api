using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/order-intake")]
[Authorize]
public sealed class RetainedOrderEvidenceReplayController(
    TmsDbContext db,
    StagingService stagingService,
    ILogger<OrderIntakeController> intakeLogger) : ControllerBase
{
    [HttpPost("replay-retained-evidence"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Replay(
        [FromBody] RetainedOrderEvidenceReplayRequest request,
        CancellationToken ct)
    {
        var receivedFromUtc = request.ReceivedFromUtc ?? DateTimeOffset.UtcNow.AddDays(-4);
        var minimumPlanningDate = request.MinimumPlanningDate ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var maximumPlanningDate = request.MaximumPlanningDate;
        if (maximumPlanningDate is not null && maximumPlanningDate < minimumPlanningDate)
            return BadRequest(new { error = "maximum_planning_date_before_minimum_planning_date" });
        var maxMessages = Math.Clamp(request.MaxMessages ?? 500, 1, 1000);

        var evidenceRows = await db.StagedImports
            .AsNoTracking()
            .Where(item => item.EntityType == "email-evidence" &&
                           item.ReceivedAtUtc >= receivedFromUtc)
            .OrderBy(item => item.ReceivedAtUtc)
            .Take(maxMessages)
            .ToListAsync(ct);

        var canonical = new OrderIntakeController(db, stagingService, intakeLogger)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContext }
        };

        var summary = new ReplaySummary();
        summary.LegacyMappingExceptionsArchived = await ArchiveLegacyMappingExceptions(
            receivedFromUtc,
            minimumPlanningDate,
            maximumPlanningDate,
            ct);
        foreach (var evidence in evidenceRows)
        {
            ct.ThrowIfCancellationRequested();
            summary.EvidenceScanned++;

            MailboxEmailIntakeRequest mailboxRequest;
            try
            {
                mailboxRequest = Rehydrate(evidence.PayloadJson);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                summary.InvalidEvidence++;
                summary.Messages.Add(new ReplayMessageResult(
                    evidence.Id,
                    null,
                    null,
                    "invalid-evidence",
                    0,
                    0,
                    ex.GetBaseException().Message));
                continue;
            }

            var parsed = await canonical.ParseForReplay(mailboxRequest, ct);
            if (parsed.Orders.Count == 0)
            {
                summary.UnmatchedEvidence++;
                continue;
            }

            var eligibleOrders = parsed.Orders
                .Where(order => IsOnOrAfter(order.Payload, minimumPlanningDate) &&
                                (maximumPlanningDate is null || IsOnOrBefore(order.Payload, maximumPlanningDate.Value)))
                .ToList();

            if (eligibleOrders.Count == 0)
            {
                summary.BeforeMinimumDate++;
                continue;
            }

            summary.MessagesMatched++;
            summary.EligibleOrders += eligibleOrders.Count;

            var filtered = parsed with { Orders = eligibleOrders };
            var keys = eligibleOrders
                .Select(order => OrderIntakeController.BuildOrderIdempotencyKey(mailboxRequest.MessageId, order.SourceKey))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var archivedForRefresh = 0;
            var archivedOriginalIds = new List<Guid>();
            if (request.RefreshUnamendedPending != false &&
                !string.IsNullOrWhiteSpace(mailboxRequest.MessageId))
            {
                // The old parser's source key can differ from the canonical parser's
                // source key. Match retained message evidence in memory after a
                // bounded pending-review query so replay always replaces the stale
                // import without relying on provider-specific JSON string translation.
                var pendingCandidates = await db.StagedImports
                    .Where(item => item.EntityType == "order" &&
                                   item.Status == StagingStatus.PendingReview &&
                                   item.ReceivedAtUtc >= receivedFromUtc &&
                                   (item.Source == null || !item.Source.StartsWith("Info mailbox replay")))
                    .ToListAsync(ct);

                var existingPending = pendingCandidates
                    .Where(item => keys.Contains(item.IdempotencyKey) ||
                                   item.PayloadJson.Contains(mailboxRequest.MessageId, StringComparison.Ordinal))
                    .ToList();

                foreach (var pending in existingPending)
                {
                    var manuallyAmended = await db.StagedImportEvents.AsNoTracking()
                        .AnyAsync(item => item.StagedImportId == pending.Id &&
                                         item.EventType == "Amended", ct);
                    if (manuallyAmended)
                    {
                        summary.ManuallyAmendedPreserved++;
                        continue;
                    }

                    var previous = pending.Status;
                    pending.Status = StagingStatus.Archived;
                    pending.IdempotencyKey = $"archived-replay:{pending.Id:N}";
                    pending.ReviewedAtUtc = DateTimeOffset.UtcNow;
                    pending.ReviewedBy = "Retained evidence replay";
                    pending.ReviewNote = "Superseded by a fresh parse of the original retained email evidence.";
                    db.StagedImportEvents.Add(StagingAudit.Create(
                        pending,
                        "ReplayArchived",
                        previous,
                        pending.ReviewNote,
                        "Retained evidence replay"));
                    archivedOriginalIds.Add(pending.Id);
                    archivedForRefresh++;
                }

                if (archivedForRefresh > 0)
                {
                    await db.SaveChangesAsync(ct);
                    db.ChangeTracker.Clear();
                    summary.PendingArchivedForRefresh += archivedForRefresh;
                }
            }

            await canonical.StageParsedForReplay(mailboxRequest, filtered, ct);

            // Replay must never revive the exact stale rows it just superseded. Re-read
            // the captured original IDs after staging and enforce the archive contract.
            if (archivedOriginalIds.Count > 0)
            {
                db.ChangeTracker.Clear();
                var originals = await db.StagedImports
                    .Where(item => archivedOriginalIds.Contains(item.Id))
                    .ToListAsync(ct);

                var repaired = 0;
                foreach (var original in originals)
                {
                    if (original.Status == StagingStatus.Archived) continue;

                    var amended = await db.StagedImportEvents.AsNoTracking()
                        .AnyAsync(item => item.StagedImportId == original.Id &&
                                         item.EventType == "Amended", ct);
                    if (amended) continue;

                    var previous = original.Status;
                    original.Status = StagingStatus.Archived;
                    original.IdempotencyKey = $"archived-replay:{original.Id:N}";
                    original.ReviewedAtUtc = DateTimeOffset.UtcNow;
                    original.ReviewedBy = "Retained evidence replay";
                    original.ReviewNote = "Superseded by a fresh parse of the original retained email evidence.";
                    db.StagedImportEvents.Add(StagingAudit.Create(
                        original,
                        "ReplayArchiveReasserted",
                        previous,
                        original.ReviewNote,
                        "Retained evidence replay"));
                    repaired++;
                }

                if (repaired > 0)
                    await db.SaveChangesAsync(ct);
            }

            var stagedNow = await db.StagedImports.AsNoTracking()
                .CountAsync(item => item.EntityType == "order" &&
                                    item.Status == StagingStatus.PendingReview &&
                                    keys.Contains(item.IdempotencyKey), ct);

            summary.PendingAfterReplay += stagedNow;
            summary.Messages.Add(new ReplayMessageResult(
                evidence.Id,
                mailboxRequest.MessageId,
                mailboxRequest.Subject,
                "replayed",
                eligibleOrders.Count,
                archivedForRefresh,
                null));
        }

        return Ok(new
        {
            receivedFromUtc,
            minimumPlanningDate = minimumPlanningDate.ToString("yyyy-MM-dd"),
            maximumPlanningDate = maximumPlanningDate?.ToString("yyyy-MM-dd"),
            maxMessages,
            refreshUnamendedPending = request.RefreshUnamendedPending != false,
            summary.EvidenceScanned,
            summary.MessagesMatched,
            summary.EligibleOrders,
            summary.PendingArchivedForRefresh,
            summary.ManuallyAmendedPreserved,
            summary.PendingAfterReplay,
            summary.LegacyMappingExceptionsArchived,
            summary.UnmatchedEvidence,
            summary.BeforeMinimumDate,
            summary.InvalidEvidence,
            messages = summary.Messages
        });
    }

    private async Task<int> ArchiveLegacyMappingExceptions(
        DateTimeOffset receivedFromUtc,
        DateOnly minimumPlanningDate,
        DateOnly? maximumPlanningDate,
        CancellationToken ct)
    {
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" &&
                           item.Status == StagingStatus.PendingReview &&
                           item.ReceivedAtUtc >= receivedFromUtc &&
                           ((item.Source != null && item.Source.StartsWith("Info mailbox mapping exception")) ||
                            item.PayloadJson.Contains("\"intakeStatus\":\"MappingException\"")))
            .ToListAsync(ct);

        var archived = 0;
        foreach (var item in candidates)
        {
            if (!PayloadIsOnOrAfter(item.PayloadJson, minimumPlanningDate)) continue;
            if (maximumPlanningDate is not null && !PayloadIsOnOrBefore(item.PayloadJson, maximumPlanningDate.Value)) continue;
            var manuallyAmended = await db.StagedImportEvents.AsNoTracking()
                .AnyAsync(evt => evt.StagedImportId == item.Id && evt.EventType == "Amended", ct);
            if (manuallyAmended) continue;

            var previous = item.Status;
            item.Status = StagingStatus.Archived;
            item.IdempotencyKey = $"archived-mapping:{item.Id:N}";
            item.ReviewedAtUtc = DateTimeOffset.UtcNow;
            item.ReviewedBy = "Retained evidence replay";
            item.ReviewNote = "Archived legacy mapping-exception placeholder; verified-format intake now retains unmatched mail as evidence only.";
            db.StagedImportEvents.Add(StagingAudit.Create(
                item,
                "LegacyMappingArchived",
                previous,
                item.ReviewNote,
                "Retained evidence replay"));
            archived++;
        }

        if (archived > 0) await db.SaveChangesAsync(ct);
        return archived;
    }

    private static bool PayloadIsOnOrAfter(string payloadJson, DateOnly minimumPlanningDate)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return IsOnOrAfter(document.RootElement, minimumPlanningDate);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool PayloadIsOnOrBefore(string payloadJson, DateOnly maximumPlanningDate)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return IsOnOrBefore(document.RootElement, maximumPlanningDate);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static MailboxEmailIntakeRequest Rehydrate(string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;
        var messageId = Text(root, "messageId");
        if (string.IsNullOrWhiteSpace(messageId))
            throw new JsonException("Retained evidence has no messageId.");

        var attachments = new List<MailboxAttachmentRequest>();
        if (TryProperty(root, "attachments", out var attachmentArray) &&
            attachmentArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in attachmentArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                attachments.Add(new MailboxAttachmentRequest(
                    Text(item, "name"),
                    Text(item, "contentType"),
                    Text(item, "contentBase64"),
                    Bool(item, "isInline"),
                    Text(item, "contentId"),
                    Long(item, "size")));
            }
        }

        return new MailboxEmailIntakeRequest(
            messageId,
            Text(root, "internetMessageId"),
            Text(root, "mailbox"),
            Text(root, "senderAddress"),
            Text(root, "senderName"),
            Text(root, "subject"),
            DateTimeOffset.TryParse(Text(root, "receivedAtUtc"), out var received) ? received : null,
            Text(root, "bodyText"),
            Text(root, "bodyHtml"),
            Text(root, "webLink"),
            attachments,
            Text(root, "conversationId"),
            Clone(root, "toRecipients"),
            Clone(root, "ccRecipients"),
            Text(root, "bodyFormat"),
            Text(root, "importance"),
            Text(root, "correlationId"));
    }

    private static bool IsOnOrAfter(JsonElement payload, DateOnly minimumPlanningDate)
    {
        var collection = Date(payload, "collectionDate");
        var delivery = Date(payload, "deliveryDate");

        DateOnly? planningDate = collection is DateOnly c && delivery is DateOnly d && c < d
            ? c
            : collection ?? delivery;

        return planningDate is DateOnly value && value >= minimumPlanningDate;
    }

    private static bool IsOnOrBefore(JsonElement payload, DateOnly maximumPlanningDate)
    {
        var collection = Date(payload, "collectionDate");
        var delivery = Date(payload, "deliveryDate");

        DateOnly? planningDate = collection is DateOnly c && delivery is DateOnly d && c < d
            ? c
            : collection ?? delivery;

        return planningDate is DateOnly value && value <= maximumPlanningDate;
    }

    private static DateOnly? Date(JsonElement root, string name) =>
        DateOnly.TryParse(Text(root, name), out var value) ? value : null;

    private static string? Text(JsonElement root, string name)
    {
        if (!TryProperty(root, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => null
        };
    }

    private static bool? Bool(JsonElement root, string name) =>
        bool.TryParse(Text(root, name), out var value) ? value : null;

    private static long? Long(JsonElement root, string name) =>
        long.TryParse(Text(root, name), out var value) ? value : null;

    private static JsonElement? Clone(JsonElement root, string name) =>
        TryProperty(root, name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.Clone()
            : null;

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private sealed class ReplaySummary
    {
        public int EvidenceScanned { get; set; }
        public int MessagesMatched { get; set; }
        public int EligibleOrders { get; set; }
        public int PendingArchivedForRefresh { get; set; }
        public int ManuallyAmendedPreserved { get; set; }
        public int PendingAfterReplay { get; set; }
        public int LegacyMappingExceptionsArchived { get; set; }
        public int UnmatchedEvidence { get; set; }
        public int BeforeMinimumDate { get; set; }
        public int InvalidEvidence { get; set; }
        public List<ReplayMessageResult> Messages { get; } = [];
    }
}

public sealed record RetainedOrderEvidenceReplayRequest(
    DateTimeOffset? ReceivedFromUtc = null,
    DateOnly? MinimumPlanningDate = null,
    DateOnly? MaximumPlanningDate = null,
    bool? RefreshUnamendedPending = true,
    int? MaxMessages = 500);

public sealed record ReplayMessageResult(
    Guid EvidenceId,
    string? MessageId,
    string? Subject,
    string Status,
    int EligibleOrders,
    int ArchivedForRefresh,
    string? Error);
