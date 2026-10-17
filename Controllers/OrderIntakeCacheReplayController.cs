using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Operational recovery for Info mailbox intake.
///
/// The normal parser should create one or more order rows from an email. When a flow/parser
/// failure means the email evidence exists but the order rows do not, this controller lets an
/// authorised operator force the retained email evidence into Pending Review as a manual
/// review order. It deliberately does not approve or promote anything to live loads.
/// </summary>
[ApiController]
[Route("api/v1/order-intake/cache")]
[Authorize]
public sealed class OrderIntakeCacheReplayController(TmsDbContext db, ILogger<OrderIntakeCacheReplayController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex DateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PalletRegex = new(
        @"\b(?<qty>\d{1,3})\s*(?:pt|plt|plts|pallets?|p)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] DateOnly? date = null,
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null,
        [FromQuery] int take = 250,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var (from, to) = ResolveWindow(date, fromUtc, toUtc);

        var evidenceRows = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "email-evidence" && item.ReceivedAtUtc >= from && item.ReceivedAtUtc < to)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(take)
            .ToListAsync(ct);

        var messageIds = evidenceRows
            .Select(TryReadEvidence)
            .Where(item => !string.IsNullOrWhiteSpace(item.MessageId))
            .Select(item => item.MessageId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var existingOrders = messageIds.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await ExistingOrderCountsByMessageId(messageIds, ct);

        var records = evidenceRows.Select(row =>
        {
            var evidence = TryReadEvidence(row);
            var count = !string.IsNullOrWhiteSpace(evidence.MessageId) && existingOrders.TryGetValue(evidence.MessageId, out var found)
                ? found
                : 0;
            return new
            {
                evidenceId = row.Id,
                row.IdempotencyKey,
                row.ReceivedAtUtc,
                evidence.MessageId,
                evidence.SenderAddress,
                evidence.Subject,
                attachmentCount = evidence.Attachments.Count,
                nonInlineAttachmentCount = evidence.Attachments.Count(item => item.IsInline != true),
                existingOrderCount = count,
                canForceReview = count == 0,
                candidateCustomer = InferCustomer(evidence),
                candidateDate = ExtractPlanningDate(evidence)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            };
        }).ToList();

        return Ok(new { fromUtc = from, toUtc = to, count = records.Count, records });
    }

    [HttpPost("{evidenceId:guid}/force-review"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ForceReview(Guid evidenceId, CancellationToken ct)
    {
        var evidenceRow = await db.StagedImports
            .SingleOrDefaultAsync(item => item.Id == evidenceId && item.EntityType == "email-evidence", ct);
        if (evidenceRow is null)
            return NotFound(new { error = "email_evidence_not_found" });

        var result = await ForceReviewOne(evidenceRow, ct);
        await db.SaveChangesAsync(ct);
        return Accepted(result);
    }

    [HttpPost("force-review"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ForceReviewDate(
        [FromQuery] DateOnly? date = null,
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null,
        [FromQuery] int take = 500,
        CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 1000);
        var (from, to) = ResolveWindow(date, fromUtc, toUtc);

        var evidenceRows = await db.StagedImports
            .Where(item => item.EntityType == "email-evidence" && item.ReceivedAtUtc >= from && item.ReceivedAtUtc < to)
            .OrderBy(item => item.ReceivedAtUtc)
            .Take(take)
            .ToListAsync(ct);

        var results = new List<object>();
        foreach (var evidenceRow in evidenceRows)
            results.Add(await ForceReviewOne(evidenceRow, ct));

        await db.SaveChangesAsync(ct);
        return Accepted(new { fromUtc = from, toUtc = to, checkedEvidence = evidenceRows.Count, results });
    }

    private async Task<object> ForceReviewOne(StagedImport evidenceRow, CancellationToken ct)
    {
        var evidence = TryReadEvidence(evidenceRow);
        if (string.IsNullOrWhiteSpace(evidence.MessageId))
        {
            return new
            {
                evidenceId = evidenceRow.Id,
                status = "skipped",
                reason = "cached_evidence_has_no_message_id"
            };
        }

        var existing = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.PayloadJson.Contains(evidence.MessageId))
            .Select(item => new { item.Id, item.Status })
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            return new
            {
                evidenceId = evidenceRow.Id,
                evidence.MessageId,
                status = "existing_order_found",
                existingOrderCount = existing.Count,
                existingOrders = existing.Select(item => new { stagedImportId = item.Id, status = item.Status.ToString() })
            };
        }

        var planningDate = ExtractPlanningDate(evidence) ?? DateOnly.FromDateTime(evidence.ReceivedAtUtc?.DateTime ?? evidenceRow.ReceivedAtUtc.DateTime);
        var idempotencyKey = $"manual-email-review:{CompactKey(evidence.MessageId)}";
        if (idempotencyKey.Length > 200) idempotencyKey = idempotencyKey[..200];

        var alreadyManual = await db.StagedImports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (alreadyManual is not null)
        {
            return new
            {
                evidenceId = evidenceRow.Id,
                evidence.MessageId,
                status = "manual_review_already_created",
                stagedImportId = alreadyManual.Id,
                stagingStatus = alreadyManual.Status.ToString()
            };
        }

        var actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "Order intake cache replay";
        var payload = BuildManualReviewPayload(evidenceRow, evidence, planningDate);
        var staged = new StagedImport
        {
            EntityType = "order",
            IdempotencyKey = idempotencyKey,
            PayloadJson = payload.ToJsonString(JsonOptions),
            Status = StagingStatus.PendingReview,
            Source = $"Forced from cached Info mailbox evidence / {(evidence.SenderAddress ?? "unknown sender").Trim()}",
            ReceivedAtUtc = evidence.ReceivedAtUtc ?? evidenceRow.ReceivedAtUtc,
            ReviewNote = "Forced into Pending Review from cached email evidence because automatic intake did not produce an order row. Planner must review source email before approval."
        };

        db.StagedImports.Add(staged);
        db.StagedImportEvents.Add(StagingAudit.Create(staged, "ManualForceReview", null, staged.ReviewNote, actor));
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = evidenceRow.Id,
            EventType = "ForceReviewRequested",
            NewStatus = evidenceRow.Status,
            PayloadJson = JsonSerializer.Serialize(new
            {
                evidenceRow.Id,
                createdStagedOrderId = staged.Id,
                evidence.MessageId,
                evidence.Subject,
                evidence.SenderAddress,
                planningDate = planningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            }),
            Note = $"Cached email evidence forced into Pending Review as staged order {staged.Id}.",
            Actor = actor
        });

        logger.LogWarning(
            "Cached order evidence {EvidenceId} / message {MessageId} forced into Pending Review as {StagedImportId}.",
            evidenceRow.Id, evidence.MessageId, staged.Id);

        return new
        {
            evidenceId = evidenceRow.Id,
            evidence.MessageId,
            status = "created_manual_review_order",
            stagedImportId = staged.Id,
            planningDate = planningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            customer = InferCustomer(evidence),
            evidence.Subject,
            evidence.SenderAddress
        };
    }

    private static JsonObject BuildManualReviewPayload(StagedImport evidenceRow, CachedEmailEvidence evidence, DateOnly planningDate)
    {
        var customer = InferCustomer(evidence);
        var pallets = ExtractPallets(evidence);
        var compact = CompactKey(evidence.MessageId ?? evidenceRow.Id.ToString("N"));
        var bodyPreview = Clip(PlainText(evidence.BodyText, evidence.BodyHtml), 12000);

        var attachments = new JsonArray();
        foreach (var attachment in evidence.Attachments)
        {
            attachments.Add(new JsonObject
            {
                ["name"] = attachment.Name,
                ["contentType"] = attachment.ContentType,
                ["contentId"] = attachment.ContentId,
                ["size"] = attachment.Size,
                ["isInline"] = attachment.IsInline
            });
        }

        return new JsonObject
        {
            ["poNumber"] = $"EMAIL-{compact[..Math.Min(compact.Length, 24)]}",
            ["customerCode"] = customer,
            ["customerName"] = CustomerName(customer),
            ["collectionDate"] = planningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = planningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = null,
            ["collectionSiteName"] = null,
            ["deliverySiteName"] = null,
            ["destinationName"] = null,
            ["marketName"] = CustomerName(customer),
            ["stallNumber"] = null,
            ["jobType"] = "Manual Email Review",
            ["driverInstructions"] = "Forced from cached email evidence. Review source email/attachment and correct fields before approval.",
            ["plannerReady"] = false,
            ["intakeStatus"] = "ManualEmailReview",
            ["intakeConfidence"] = "Low",
            ["intakeWarnings"] = new JsonArray
            {
                "Automatic intake did not produce an order row; this record was manually forced from cached email evidence.",
                "Collection site, delivery site and pallets may need correcting before approval."
            },
            ["intakeParser"] = "Cached Email Force Review",
            ["sourceEvidenceId"] = evidenceRow.Id.ToString(),
            ["sourceEvidenceKey"] = evidenceRow.IdempotencyKey,
            ["sourceMailbox"] = evidence.Mailbox,
            ["sourceSender"] = evidence.SenderAddress,
            ["sourceSenderName"] = evidence.SenderName,
            ["sourceEmailSubject"] = evidence.Subject,
            ["sourceSubject"] = evidence.Subject,
            ["sourceEmailReceivedAt"] = evidence.ReceivedAtUtc,
            ["sourceReceivedAtUtc"] = evidence.ReceivedAtUtc,
            ["sourceEmailMessageId"] = evidence.MessageId,
            ["sourceMessageId"] = evidence.MessageId,
            ["sourceInternetMessageId"] = evidence.InternetMessageId,
            ["sourceConversationId"] = evidence.ConversationId,
            ["sourceEmailWebLink"] = evidence.WebLink,
            ["sourceWebLink"] = evidence.WebLink,
            ["sourceBodyFormat"] = evidence.BodyFormat,
            ["sourceBodyText"] = bodyPreview,
            ["sourceBodyPreviewTruncated"] = (PlainText(evidence.BodyText, evidence.BodyHtml)?.Length ?? 0) > 12000,
            ["sourceAttachments"] = attachments,
            ["importSource"] = "OrderIntakeCache/ManualForceReview",
            ["reviewStatus"] = "Pending Review"
        };
    }

    private async Task<Dictionary<string, int>> ExistingOrderCountsByMessageId(IReadOnlyCollection<string> messageIds, CancellationToken ct)
    {
        var orders = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order")
            .Select(item => item.PayloadJson)
            .ToListAsync(ct);

        var counts = messageIds.ToDictionary(item => item, _ => 0, StringComparer.Ordinal);
        foreach (var payload in orders)
        {
            foreach (var messageId in messageIds)
            {
                if (payload.Contains(messageId, StringComparison.Ordinal))
                    counts[messageId]++;
            }
        }

        return counts;
    }

    private static (DateTimeOffset FromUtc, DateTimeOffset ToUtc) ResolveWindow(DateOnly? date, DateTimeOffset? fromUtc, DateTimeOffset? toUtc)
    {
        if (date is not null)
        {
            var from = new DateTimeOffset(date.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return (from, from.AddDays(1));
        }

        var now = DateTimeOffset.UtcNow;
        return (fromUtc ?? now.AddDays(-1), toUtc ?? now.AddDays(1));
    }

    private static CachedEmailEvidence TryReadEvidence(StagedImport row)
    {
        try
        {
            return JsonSerializer.Deserialize<CachedEmailEvidence>(row.PayloadJson, JsonOptions) ?? new CachedEmailEvidence();
        }
        catch (JsonException)
        {
            return new CachedEmailEvidence();
        }
    }

    private static DateOnly? ExtractPlanningDate(CachedEmailEvidence evidence)
    {
        var source = $"{evidence.Subject}\n{evidence.BodyText}\n{string.Join("\n", evidence.Attachments.Select(item => item.Name))}";
        var match = DateRegex.Match(source);
        if (!match.Success) return null;

        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var yearText = match.Groups["year"].Value;
        var baseYear = evidence.ReceivedAtUtc?.Year ?? DateTimeOffset.UtcNow.Year;
        var year = string.IsNullOrWhiteSpace(yearText)
            ? baseYear
            : yearText.Length == 2
                ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture)
                : int.Parse(yearText, CultureInfo.InvariantCulture);

        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int? ExtractPallets(CachedEmailEvidence evidence)
    {
        var source = $"{evidence.Subject}\n{evidence.BodyText}";
        var match = PalletRegex.Match(source);
        return match.Success && int.TryParse(match.Groups["qty"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pallets)
            ? pallets
            : null;
    }

    private static string InferCustomer(CachedEmailEvidence evidence)
    {
        var value = $"{evidence.SenderAddress} {evidence.SenderName} {evidence.Subject} {evidence.BodyText} {string.Join(" ", evidence.Attachments.Select(item => item.Name))}";
        if (Contains(value, "NWAY") || Contains(value, "NWF") || Contains(value, "Natures Way") || Contains(value, "Nature's Way")) return "NWF";
        if (Contains(value, "Summer Berry") || (evidence.SenderAddress ?? string.Empty).EndsWith("@summerberry.co.uk", StringComparison.OrdinalIgnoreCase)) return "SUMMERBERRY";
        if (Contains(value, "Langmead") || (evidence.SenderAddress ?? string.Empty).EndsWith("@langmeadherbs.co.uk", StringComparison.OrdinalIgnoreCase) || (evidence.SenderAddress ?? string.Empty).EndsWith("@langmeadfarms.co.uk", StringComparison.OrdinalIgnoreCase)) return "LANGMEADS";
        if (Contains(value, "Barfoots") || (evidence.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase)) return "BARFOOTS";
        if (Contains(value, "Greenhouse")) return "GREENHOUSE";
        if (Contains(value, "Aldi")) return "ALDI";
        if (Contains(value, "Morrisons")) return "MORRISONS";
        if (Contains(value, "Waitrose") || Contains(value, "Weightrose")) return "WAITROSE";
        if (Contains(value, "Costco")) return "COSTCO";
        if (Contains(value, "Sainsbury")) return "SAINSBURY";
        if (Contains(value, "Ocado")) return "OCADO";
        return "EMAIL";
    }

    private static string CustomerName(string code) => code switch
    {
        "NWF" => "Natures Way",
        "SUMMERBERRY" => "Summer Berry",
        "LANGMEADS" => "Langmeads",
        "BARFOOTS" => "Barfoots",
        "GREENHOUSE" => "Greenhouse Growers",
        "ALDI" => "Aldi",
        "MORRISONS" => "Morrisons",
        "WAITROSE" => "Waitrose",
        "COSTCO" => "Costco",
        "SAINSBURY" => "Sainsbury",
        "OCADO" => "Ocado",
        _ => "Email Order"
    };

    private static bool Contains(string value, string fragment) =>
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    private static string CompactKey(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length <= 96 ? compact : compact[^96..];
    }

    private static string? PlainText(string? bodyText, string? bodyHtml)
    {
        if (!string.IsNullOrWhiteSpace(bodyText)) return bodyText;
        if (string.IsNullOrWhiteSpace(bodyHtml)) return null;
        var withoutTags = Regex.Replace(bodyHtml, "<[^>]+>", " ");
        return Regex.Replace(System.Net.WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
    }

    private static string? Clip(string? value, int limit) =>
        string.IsNullOrEmpty(value) || value.Length <= limit ? value : value[..limit];

    private sealed class CachedEmailEvidence
    {
        public string? MessageId { get; set; }
        public string? InternetMessageId { get; set; }
        public string? ConversationId { get; set; }
        public string? Mailbox { get; set; }
        public string? SenderAddress { get; set; }
        public string? SenderName { get; set; }
        public string? Subject { get; set; }
        public DateTimeOffset? ReceivedAtUtc { get; set; }
        public string? BodyText { get; set; }
        public string? BodyHtml { get; set; }
        public string? BodyFormat { get; set; }
        public string? Importance { get; set; }
        public string? WebLink { get; set; }
        public string? CorrelationId { get; set; }
        public List<CachedAttachment> Attachments { get; set; } = [];
    }

    private sealed class CachedAttachment
    {
        public string? Name { get; set; }
        public string? ContentType { get; set; }
        public string? ContentId { get; set; }
        public long? Size { get; set; }
        public bool? IsInline { get; set; }
    }
}
