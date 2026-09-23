using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/order-intake")]
[Authorize]
public sealed class OrderIntakeController(TmsDbContext db, StagingService stagingService, ILogger<OrderIntakeController> logger) : ControllerBase
{
    private readonly SpecialistMailboxOrderParser specialistParser = new();
    private readonly SainsburyHaulierPlanParser sainsburyParser = new();
    private readonly NwfDailyTrackerParser nwfParser = new();
    private readonly NwfWorkbookSnapshotParser nwfWorkbookParser = new();
    private readonly NwfPalletOrderCsvParser nwfCsvParser = new();
    private readonly NwfQuantityChangeParser nwfQuantityChangeParser = new();
    private const int SourceBodyPreviewLimit = 12000;
    private const int SourceBodyTextLimit = 200000;
    private const int SourceBodyHtmlLimit = 400000;
    private static readonly Regex DateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [HttpPost("email/preview"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Preview([FromBody] MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var parsed = await ParseEmail(request, ct);
        return Ok(new
        {
            ignored = parsed.IgnoredReason is not null,
            ignoredReason = parsed.IgnoredReason,
            warnings = parsed.Warnings,
            orderCount = parsed.Orders.Count,
            orders = parsed.Orders.Select(order => new
            {
                order.SourceKey,
                order.NaturalKey,
                payload = order.Payload,
                warnings = order.Warnings
            })
        });
    }

    [HttpPost("email"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Intake([FromBody] MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId))
            return BadRequest(new ErrorResponse("missing_message_id", "Mailbox message ID is required so repeated flow runs remain idempotent.", HttpContext.TraceIdentifier));

        // Always retain the source email before deciding whether it belongs in the
        // deliberately narrow automatic-order lane. Paused sources remain auditable
        // and can be replayed later without manufacturing review-queue orders today.
        await EnsureSourceEmailEvidence(request, ct);

        var parsed = await ParseEmail(request, ct);
        if (parsed.IgnoredReason is not null)
        {
            var linked = 0;
            if (parsed.IgnoredReason.Contains("Operational request", StringComparison.OrdinalIgnoreCase))
            {
                linked = await LinkOperationalUpdateToPendingOrders(request, ct);
            }
            return Ok(new { ignored = true, reason = parsed.IgnoredReason, staged = 0, existing = 0, superseded = 0, linked, warnings = parsed.Warnings, outlookCategory = (string?)null });
        }

        var staged = 0;
        var existing = 0;
        var records = new List<object>();

        var prepared = parsed.Orders.Select(order =>
        {
            var key = $"email:{CompactKey(request.MessageId)}:{order.SourceKey}";
            if (key.Length > 200) key = key[..200];
            return (Order: order, IdempotencyKey: key);
        }).ToList();

        var idempotencyKeys = prepared.Select(item => item.IdempotencyKey).Distinct(StringComparer.Ordinal).ToList();
        var existingByKey = idempotencyKeys.Count == 0
            ? new Dictionary<string, StagedImport>(StringComparer.Ordinal)
            : await db.StagedImports.AsNoTracking()
                .Where(item => idempotencyKeys.Contains(item.IdempotencyKey))
                .ToDictionaryAsync(item => item.IdempotencyKey, StringComparer.Ordinal, ct);

        var missingOrders = prepared
            .Where(item => !existingByKey.ContainsKey(item.IdempotencyKey))
            .Select(item => item.Order)
            .ToList();
        var superseded = await SupersedeOlderPendingBatch(missingOrders, parsed.Orders, request.MessageId, ct);
        var createdByKey = new Dictionary<string, StagedImport>(StringComparer.Ordinal);

        foreach (var preparedOrder in prepared)
        {
            if (existingByKey.TryGetValue(preparedOrder.IdempotencyKey, out var already) ||
                createdByKey.TryGetValue(preparedOrder.IdempotencyKey, out already))
            {
                existing++;
                records.Add(new
                {
                    stagingId = already.Id,
                    status = already.Status.ToString(),
                    existing = true,
                    reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{already.Id}"
                });
                continue;
            }

            var order = preparedOrder.Order;
            var stagedPayload = EnrichSourceEvidence(order.Payload, request);
            var item = stagingService.Create(new StageImportRequest(
                "order",
                preparedOrder.IdempotencyKey,
                stagedPayload,
                $"Info mailbox / {(request.SenderAddress ?? "unknown sender").Trim()}"));
            db.StagedImports.Add(item);
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Received"));
            createdByKey[preparedOrder.IdempotencyKey] = item;
            staged++;

            records.Add(new
            {
                stagingId = item.Id,
                status = item.Status.ToString(),
                existing = false,
                plannerReady = ReadBool(order.Payload, "plannerReady"),
                intakeStatus = ReadText(order.Payload, "intakeStatus"),
                warnings = order.Warnings,
                reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{item.Id}"
            });
        }

        if (staged > 0 || superseded > 0)
            await db.SaveChangesAsync(ct);

        TmsMetrics.Shared.RecordImportBatch(staged + existing, existing, "email_order");

        logger.LogInformation(
            "Info mailbox intake {MessageId}: staged {Staged}, existing {Existing}, superseded {Superseded}, parser warnings {Warnings}.",
            request.MessageId, staged, existing, superseded, parsed.Warnings.Count);

        return Accepted(new { ignored = false, staged, existing, superseded, warnings = parsed.Warnings, outlookCategory = "TMS Imported", records });
    }

    [HttpGet("source-email/{stagingId:guid}")]
    public async Task<IActionResult> SourceEmail(Guid stagingId, CancellationToken ct)
    {
        var staged = await db.StagedImports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == stagingId && item.EntityType == "order", ct);
        if (staged is null) return NotFound(new { error = "staged_order_not_found" });

        try
        {
            using var orderDocument = JsonDocument.Parse(staged.PayloadJson);
            var orderPayload = orderDocument.RootElement;
            var evidenceKey = ReadText(orderPayload, "sourceEvidenceKey");
            var messageId = ReadText(orderPayload, "sourceMessageId") ?? ReadText(orderPayload, "sourceEmailMessageId");
            if (string.IsNullOrWhiteSpace(evidenceKey) && !string.IsNullOrWhiteSpace(messageId))
                evidenceKey = SourceEvidenceKey(messageId);

            if (!string.IsNullOrWhiteSpace(evidenceKey))
            {
                var evidence = await db.StagedImports.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == evidenceKey, ct);
                if (evidence is not null)
                    return Content(evidence.PayloadJson, "application/json");
            }

            return Ok(new
            {
                messageId,
                internetMessageId = ReadText(orderPayload, "sourceInternetMessageId"),
                conversationId = ReadText(orderPayload, "sourceConversationId"),
                mailbox = ReadText(orderPayload, "sourceMailbox"),
                senderAddress = ReadText(orderPayload, "sourceSender"),
                senderName = ReadText(orderPayload, "sourceSenderName"),
                subject = ReadText(orderPayload, "sourceSubject") ?? ReadText(orderPayload, "sourceEmailSubject"),
                receivedAtUtc = ReadText(orderPayload, "sourceReceivedAtUtc") ?? ReadText(orderPayload, "sourceEmailReceivedAt"),
                bodyText = ReadText(orderPayload, "sourceBodyText"),
                bodyHtml = (string?)null,
                bodyFormat = ReadText(orderPayload, "sourceBodyFormat"),
                importance = ReadText(orderPayload, "sourceImportance"),
                webLink = ReadText(orderPayload, "sourceWebLink") ?? ReadText(orderPayload, "sourceEmailWebLink"),
                toRecipients = TryGetProperty(orderPayload, "sourceToRecipients", out var to) ? to.Clone() : default(JsonElement?),
                ccRecipients = TryGetProperty(orderPayload, "sourceCcRecipients", out var cc) ? cc.Clone() : default(JsonElement?),
                attachments = TryGetProperty(orderPayload, "sourceAttachments", out var attachments) ? attachments.Clone() : default(JsonElement?),
                bodyTruncated = ReadBool(orderPayload, "sourceBodyPreviewTruncated") ?? false,
                evidenceAvailable = false
            });
        }
        catch (JsonException)
        {
            return UnprocessableEntity(new { error = "staged_source_evidence_invalid" });
        }
    }

    internal Task<EmailIntakeParseResult> ParseForReplay(MailboxEmailIntakeRequest request, CancellationToken ct) =>
        ParseEmail(request, ct);

    internal async Task<IActionResult> StageParsedForReplay(
        MailboxEmailIntakeRequest request,
        EmailIntakeParseResult parsed,
        CancellationToken ct)
    {
        var staged = 0;
        var existing = 0;
        var records = new List<object>();

        var prepared = parsed.Orders.Select(order =>
        {
            var key = BuildOrderIdempotencyKey(request.MessageId, order.SourceKey);
            return (Order: order, IdempotencyKey: key);
        }).ToList();

        var idempotencyKeys = prepared.Select(item => item.IdempotencyKey).Distinct(StringComparer.Ordinal).ToList();
        var existingByKey = idempotencyKeys.Count == 0
            ? new Dictionary<string, StagedImport>(StringComparer.Ordinal)
            : await db.StagedImports.AsNoTracking()
                .Where(item => idempotencyKeys.Contains(item.IdempotencyKey))
                .ToDictionaryAsync(item => item.IdempotencyKey, StringComparer.Ordinal, ct);

        var missingOrders = prepared
            .Where(item => !existingByKey.ContainsKey(item.IdempotencyKey))
            .Select(item => item.Order)
            .ToList();
        var superseded = await SupersedeOlderPendingBatch(missingOrders, parsed.Orders, request.MessageId, ct);
        var createdByKey = new Dictionary<string, StagedImport>(StringComparer.Ordinal);

        foreach (var preparedOrder in prepared)
        {
            if (existingByKey.TryGetValue(preparedOrder.IdempotencyKey, out var already) ||
                createdByKey.TryGetValue(preparedOrder.IdempotencyKey, out already))
            {
                existing++;
                records.Add(new
                {
                    stagingId = already.Id,
                    status = already.Status.ToString(),
                    existing = true,
                    reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{already.Id}"
                });
                continue;
            }

            var order = preparedOrder.Order;
            var stagedPayload = EnrichSourceEvidence(order.Payload, request);
            var item = stagingService.Create(new StageImportRequest(
                "order",
                preparedOrder.IdempotencyKey,
                stagedPayload,
                $"Info mailbox replay / {(request.SenderAddress ?? "unknown sender").Trim()}"));
            db.StagedImports.Add(item);
            db.StagedImportEvents.Add(StagingAudit.Create(item, "Replayed"));
            createdByKey[preparedOrder.IdempotencyKey] = item;
            staged++;

            records.Add(new
            {
                stagingId = item.Id,
                status = item.Status.ToString(),
                existing = false,
                plannerReady = ReadBool(order.Payload, "plannerReady"),
                intakeStatus = ReadText(order.Payload, "intakeStatus"),
                warnings = order.Warnings,
                reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{item.Id}"
            });
        }

        if (staged > 0 || superseded > 0)
            await db.SaveChangesAsync(ct);

        TmsMetrics.Shared.RecordImportBatch(staged + existing, existing, "email_order_replay");

        logger.LogInformation(
            "Info mailbox replay {MessageId}: staged {Staged}, existing {Existing}, superseded {Superseded}, parser warnings {Warnings}.",
            request.MessageId, staged, existing, superseded, parsed.Warnings.Count);

        return Accepted(new { ignored = false, staged, existing, superseded, warnings = parsed.Warnings, outlookCategory = "TMS Imported", records });
    }

    internal static string BuildOrderIdempotencyKey(string messageId, string sourceKey)
    {
        var key = $"email:{CompactKey(messageId)}:{sourceKey}";
        return key.Length <= 200 ? key : key[..200];
    }

    private async Task<EmailIntakeParseResult> ParseEmail(MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        // Keep operational status messages out of the order-creation lane while still
        // allowing Intake() to link them to the existing staged order as evidence.
        // The generic parser is used here only for its explicit operational-message
        // classification; any generic orders it may infer are deliberately ignored.
        var operationalSignal = new EmailOrderIntakeService().Parse(request);
        if (operationalSignal.Orders.Count == 0 &&
            operationalSignal.IgnoredReason?.Contains("Operational request", StringComparison.OrdinalIgnoreCase) == true)
            return operationalSignal;

        // Info mailbox intake is deliberately parser-led. A sender/domain mapping,
        // retailer name or generic "looks like an order" heuristic must never create
        // a staging order. If none of the verified formats below recognise the
        // message, retain its source evidence and stop.
        var parsed = nwfQuantityChangeParser.TryParse(request)
            ?? nwfCsvParser.TryParse(request)
            ?? nwfWorkbookParser.TryParse(request)
            ?? nwfParser.TryParse(request)
            ?? sainsburyParser.TryParse(request)
            ?? specialistParser.TryParse(request)
            ?? new EmailIntakeParseResult(
                [],
                [],
                "No verified order format matched; source evidence retained.");

        if (parsed.Orders.Count == 0)
            return parsed;

        var aligned = await EmailOrderSiteMasterAlignment.AlignAsync(db, parsed, ct);
        var enriched = await NwfCrateReferenceLinker.EnrichAsync(db, aligned, request, ct);

        // A later tray/crate instruction can be matched uniquely back to the retained
        // NWF dump order. In that case the dump remains the order authority and this
        // email is source evidence only; do not stage a duplicate movement.
        var ordersToStage = enriched.Orders
            .Where(order => string.IsNullOrWhiteSpace(ReadText(order.Payload, "referenceLinkSourceStagedImportId")))
            .ToList();

        if (ordersToStage.Count == enriched.Orders.Count)
            return enriched;

        if (ordersToStage.Count == 0)
            return new EmailIntakeParseResult(
                [],
                enriched.Warnings,
                "NWF crate/tray load matched an existing staged order; source evidence retained without creating another order.");

        return enriched with { Orders = ordersToStage };
    }

    private static bool IsSimplifiedIntakeSource(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var source = string.Join("\n", new[]
        {
            request.SenderAddress,
            request.SenderName,
            request.Subject,
            request.BodyText,
            request.BodyHtml,
            string.Join(" ", (request.Attachments ?? []).Where(item => item.IsInline != true).Select(item => item.Name))
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return sender.EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@summerberry.co.uk", StringComparison.OrdinalIgnoreCase) ||
               IsNwfTransferSource(request) ||
               source.Contains("Natures Way", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Nature's Way", StringComparison.OrdinalIgnoreCase) ||
               Regex.IsMatch(source, @"\b(?:NWF|NWAY)\b", RegexOptions.IgnoreCase) ||
               source.Contains("Barfoots", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Greenhouse Growers", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Greenhouse", StringComparison.OrdinalIgnoreCase) ||
               source.Contains("Summer Berry", StringComparison.OrdinalIgnoreCase);
    }

    private static EmailIntakeParseResult ApplySimplifiedDestinationGate(MailboxEmailIntakeRequest request, EmailIntakeParseResult parsed)
    {
        var source = string.Join("\n", new[]
        {
            request.Subject,
            request.BodyText,
            request.BodyHtml,
            string.Join(" ", (request.Attachments ?? []).Where(item => item.IsInline != true).Select(item => item.Name))
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var eligible = parsed.Orders
            .Where(order => IsNwfTransferOrder(order.Payload) ||
                            IsSimplifiedDestination(order.Payload) ||
                            (parsed.Orders.Count == 1 && IsSimplifiedDestination(source)))
            .ToList();

        return parsed with { Orders = eligible };
    }

    private static bool IsSimplifiedDestination(string value) =>
        value.Contains("Aldi", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Morrisons", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Weightrose", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Costco", StringComparison.OrdinalIgnoreCase);

    private static bool IsSimplifiedDestination(JsonElement payload)
    {
        // Deliberately inspect only order-level customer/destination fields. Route
        // enrichment and source-evidence metadata can mention another retailer and
        // must never cause an unrelated row in a mixed NWF report to pass the gate.
        var fields = new[]
        {
            "customerCode", "customerName", "marketName", "stallNumber",
            "depotId", "depotDescription", "deliverySiteName", "destinationName"
        };
        return fields.Any(field => TryGetProperty(payload, field, out var value) &&
                                   value.ValueKind == JsonValueKind.String &&
                                   IsSimplifiedDestination(value.GetString() ?? string.Empty));
    }

    private static bool IsNwfTransferOrder(JsonElement payload) =>
        string.Equals(ReadText(payload, "customerCode"), "NWF", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(ReadText(payload, "jobType"), "Collection transfer", StringComparison.OrdinalIgnoreCase);

    private static bool IsNwfTransferSource(MailboxEmailIntakeRequest request)
    {
        var subject = request.Subject ?? string.Empty;
        if (!subject.Contains("transfer", StringComparison.OrdinalIgnoreCase)) return false;

        var nwfSites = new[] { "Barnham", "Merston", "Runcton", "Selsey", "Drayton" };
        return nwfSites.Count(site => subject.Contains(site, StringComparison.OrdinalIgnoreCase)) >= 2;
    }

    private static JsonElement AddFastPathMarker(JsonElement payload)
    {
        var root = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
        root["emailIntakePath"] = "sender-route-fast-path";
        return JsonSerializer.SerializeToElement(root);
    }

    private async Task<IReadOnlyCollection<string>> MasterSiteNames(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking()
            .Where(site => site.Active)
            .ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        return sites
            .SelectMany(site => new[] { site.Name, site.DriverTextName, site.ExternalCode }
                .Concat((site.Aliases ?? string.Empty)
                    .Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IActionResult> StageMappingException(MailboxEmailIntakeRequest request, EmailIntakeParseResult parsed, CancellationToken ct)
    {
        var idempotencyKey = $"email:{CompactKey(request.MessageId)}:mapping-exception";
        if (idempotencyKey.Length > 200) idempotencyKey = idempotencyKey[..200];
        var already = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, ct);
        if (already is not null)
            return Accepted(new
            {
                ignored = false,
                staged = 0,
                existing = 1,
                superseded = 0,
                warnings = parsed.Warnings,
                outlookCategory = "TMS Review",
                records = new[]
                {
                    new
                    {
                        stagingId = already.Id,
                        status = already.Status.ToString(),
                        existing = true,
                        plannerReady = false,
                        intakeStatus = "MappingException",
                        warnings = parsed.Warnings.Append(parsed.IgnoredReason).Where(value => !string.IsNullOrWhiteSpace(value)).ToList(),
                        reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{already.Id}"
                    }
                }
            });

        var warnings = parsed.Warnings.Append(parsed.IgnoredReason).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var date = ExtractDate(request) ?? DateOnly.FromDateTime((request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).DateTime);
        var payload = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["poNumber"] = $"MAPPING-{CompactKey(request.MessageId)[..Math.Min(CompactKey(request.MessageId).Length, 24)]}",
            ["customerCode"] = InferMappingCustomer(request),
            ["collectionDate"] = date.ToString("yyyy-MM-dd"),
            ["deliveryDate"] = date.ToString("yyyy-MM-dd"),
            ["pallets"] = null,
            ["sellerName"] = null,
            ["marketName"] = InferMappingCustomer(request),
            ["stallNumber"] = null,
            ["jobType"] = "Mapping Exception",
            ["driverInstructions"] = "Mailbox order needs manual mapping before approval.",
            ["plannerReady"] = false,
            ["intakeStatus"] = "MappingException",
            ["intakeConfidence"] = "Low",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = "Mapping Exception",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["sourceAttachmentNames"] = (request.Attachments ?? []).Where(item => item.IsInline != true).Select(item => item.Name).Where(name => !string.IsNullOrWhiteSpace(name)).ToList()
        });
        var stagedPayload = EnrichSourceEvidence(payload, request);
        var item = stagingService.Create(new StageImportRequest(
            "order",
            idempotencyKey,
            stagedPayload,
            $"Info mailbox mapping exception / {(request.SenderAddress ?? "unknown sender").Trim()}"));
        db.StagedImports.Add(item);
        db.StagedImportEvents.Add(StagingAudit.Create(item, "MappingException", item.Status, "Plausible mailbox order could not be parsed automatically; source evidence retained for review.", "Info mailbox intake"));
        await db.SaveChangesAsync(ct);

        logger.LogWarning(
            "Info mailbox intake {MessageId}: staged mapping exception for {Sender} / {Subject}. Reason: {Reason}",
            request.MessageId,
            request.SenderAddress,
            request.Subject,
            parsed.IgnoredReason);

        return Accepted(new
        {
            ignored = false,
            staged = 1,
            existing = 0,
            superseded = 0,
            warnings,
            outlookCategory = "TMS Review",
            records = new[]
            {
                new
                {
                    stagingId = item.Id,
                    status = item.Status.ToString(),
                    existing = false,
                    plannerReady = false,
                    intakeStatus = "MappingException",
                    warnings,
                    reviewUrl = $"{Request.Scheme}://{Request.Host}/api/v1/staging/{item.Id}"
                }
            }
        });
    }

    private static bool ShouldStageMappingException(MailboxEmailIntakeRequest request, EmailIntakeParseResult parsed)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId)) return false;
        var reason = parsed.IgnoredReason ?? string.Empty;
        if (reason.Contains("Internal TMS", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("Operational request", StringComparison.OrdinalIgnoreCase)) return false;
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        var body = $"{request.BodyText} {request.BodyHtml}";
        var attachments = string.Join(" ", (request.Attachments ?? []).Select(item => item.Name));
        var value = $"{sender} {subject} {body} {attachments}";
        if (LooksOperationalNoise(value))
            return false;
        if (subject.Contains("market", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(body, @"\b\d+\s*(?:pt|pallets?|p)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(body, @"\b(?:collect\w*|deliver\w*)\b", RegexOptions.IgnoreCase))
            return true;
        var hasAttachment = (request.Attachments ?? []).Any(item => item.IsInline != true);
        var internalPlannerAttachment = sender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase) &&
                                        hasAttachment &&
                                        (value.Contains("load plan", StringComparison.OrdinalIgnoreCase) ||
                                         value.Contains("daily times", StringComparison.OrdinalIgnoreCase) ||
                                         value.Contains("aldi times", StringComparison.OrdinalIgnoreCase) ||
                                         value.Contains("lyons collections", StringComparison.OrdinalIgnoreCase));

        var recognisedSource = sender.EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||
               internalPlannerAttachment ||
               sender.EndsWith("@apsgroup.uk.com", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@pmtransport.co.uk", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@vitacress.com", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@langmeadherbs.co.uk", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@langmeadfarms.co.uk", StringComparison.OrdinalIgnoreCase) ||
               sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Natures Way", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Nature's Way", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Langmead", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Barfoots", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Market Week", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("PM Transport", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Vitacress", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Aldi", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Weightrose", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("Morrisons", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("IFCO", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("tray", StringComparison.OrdinalIgnoreCase);

        return LooksLikeOrderIntent(request, value) && (recognisedSource || !sender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksLikeOrderIntent(MailboxEmailIntakeRequest request, string value)
    {
        var hasAttachment = (request.Attachments ?? []).Any(item => item.IsInline != true);
        if (value.Contains("pallet order", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("order ref", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("purchase order", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("delivery quantities", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("booking form", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("market week", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("additional market", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("onward delivery", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("confirmed collection", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("confirmed collections", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("confirmed ALDI", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("load plan", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("daily times", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("aldi times", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("lyons collections", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("collection for", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("delivery to", StringComparison.OrdinalIgnoreCase))
            return true;

        return hasAttachment &&
               (value.Contains("order", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("booking", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("pallet", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("pallets", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("tray", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("trays", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("crate", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("crates", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("transport", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("collection", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("delivery", StringComparison.OrdinalIgnoreCase));
    }

    private static bool LooksOperationalNoise(string value) =>
        value.Contains("available loads", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("loads available", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("load work available", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("loads tipping", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("rates negotiable", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("must be own vehicle", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("let us know if you are interested", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("let us know if you can assist", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("you opted in", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("inbound eta", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("please find attached eta", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("github", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("microsoft support", StringComparison.OrdinalIgnoreCase);

    private static string InferMappingCustomer(MailboxEmailIntakeRequest request)
    {
        var value = $"{request.SenderAddress} {request.Subject}";
        if (value.Contains("IFCO", StringComparison.OrdinalIgnoreCase))
            return "IFCO";
        if (value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) || value.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Natures Way", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Nature's Way", StringComparison.OrdinalIgnoreCase) ||
            (request.SenderAddress ?? string.Empty).EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase))
            return "NWF";
        if (value.Contains("Langmead", StringComparison.OrdinalIgnoreCase) ||
            (request.SenderAddress ?? string.Empty).EndsWith("@langmeadherbs.co.uk", StringComparison.OrdinalIgnoreCase) ||
            (request.SenderAddress ?? string.Empty).EndsWith("@langmeadfarms.co.uk", StringComparison.OrdinalIgnoreCase))
            return "LANGMEADS";
        if (value.Contains("Barfoots", StringComparison.OrdinalIgnoreCase) ||
            (request.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase))
            return "BARFOOTS";
        if (value.Contains("Aldi", StringComparison.OrdinalIgnoreCase)) return "ALDI";
        if (value.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) || value.Contains("Weightrose", StringComparison.OrdinalIgnoreCase)) return "WAITROSE";
        if (value.Contains("Morrisons", StringComparison.OrdinalIgnoreCase)) return "MORRISONS";
        return "EMAIL";
    }

    private static DateOnly? ExtractDate(MailboxEmailIntakeRequest request)
    {
        var value = $"{request.Subject}\n{request.BodyText}\n{string.Join("\n", (request.Attachments ?? []).Select(item => item.Name))}";
        var match = DateRegex.Match(value);
        if (!match.Success) return null;
        var day = int.Parse(match.Groups["day"].Value);
        var month = int.Parse(match.Groups["month"].Value);
        var yearText = match.Groups["year"].Value;
        var year = string.IsNullOrWhiteSpace(yearText)
            ? (request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).Year
            : yearText.Length == 2
                ? 2000 + int.Parse(yearText)
                : int.Parse(yearText);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private async Task<int> SupersedeOlderPendingBatch(
        IReadOnlyCollection<ParsedEmailOrder> missingOrders,
        IReadOnlyCollection<ParsedEmailOrder> allOrders,
        string currentMessageId,
        CancellationToken ct)
    {
        var naturalKeys = missingOrders
            .Select(order => order.NaturalKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matchKeys = allOrders
            .SelectMany(order => ReadMatchKeys(order.Payload))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (naturalKeys.Count == 0 && matchKeys.Count == 0) return 0;

        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview)
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var count = 0;

        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var root = document.RootElement;
                if (string.Equals(ReadText(root, "sourceMessageId"), currentMessageId, StringComparison.Ordinal))
                    continue;

                var naturalKey = ReadText(root, "intakeNaturalKey");
                var naturalMatch = !string.IsNullOrWhiteSpace(naturalKey) && naturalKeys.Contains(naturalKey);
                var stableMatch = matchKeys.Count > 0 && ReadMatchKeys(root).Any(matchKeys.Contains);
                if (!naturalMatch && !stableMatch)
                    continue;

                var previous = candidate.Status;
                candidate.Status = StagingStatus.Rejected;
                candidate.ReviewedAtUtc = now;
                candidate.ReviewedBy = stableMatch ? "Mailbox snapshot supersession" : "Mailbox supersession";
                candidate.ReviewNote = stableMatch
                    ? $"Superseded by a newer NWF/Info mailbox snapshot ({currentMessageId}). Original evidence retained."
                    : $"Superseded automatically by a newer Info mailbox message ({currentMessageId}). Original evidence retained.";
                db.StagedImportEvents.Add(StagingAudit.Create(candidate, "Superseded", previous, candidate.ReviewNote, candidate.ReviewedBy));
                count++;
            }
            catch (JsonException)
            {
                // A malformed legacy staging payload remains visible for manual review
                // and must not block newer mailbox work from being staged.
            }
        }

        return count;
    }

    private async Task<int> SupersedeOlderPendingByMatchKeys(IReadOnlyCollection<string> currentKeys, string currentMessageId, CancellationToken ct)
    {
        if (currentKeys.Count == 0) return 0;
        var keySet = currentKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview)
            .ToListAsync(ct);

        var matching = new List<StagedImport>();
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var root = document.RootElement;
                if (string.Equals(ReadText(root, "sourceMessageId"), currentMessageId, StringComparison.Ordinal))
                    continue;
                if (ReadMatchKeys(root).Any(keySet.Contains))
                    matching.Add(candidate);
            }
            catch (JsonException)
            {
            }
        }

        if (matching.Count == 0) return 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var candidate in matching)
        {
            var previous = candidate.Status;
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox snapshot supersession";
            candidate.ReviewNote = $"Superseded by a newer NWF/Info mailbox snapshot ({currentMessageId}). Original evidence retained.";
            db.StagedImportEvents.Add(StagingAudit.Create(candidate, "Superseded", previous, candidate.ReviewNote, candidate.ReviewedBy));
        }
        await db.SaveChangesAsync(ct);
        return matching.Count;
    }

    private async Task<int> SupersedeOlderPending(string naturalKey, string currentMessageId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(naturalKey)) return 0;
        var marker = $"\"intakeNaturalKey\":\"{EscapeForContains(naturalKey)}\"";
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.PayloadJson.Contains(marker))
            .ToListAsync(ct);
        if (candidates.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var count = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                if (string.Equals(ReadText(document.RootElement, "sourceMessageId"), currentMessageId, StringComparison.Ordinal))
                    continue;
            }
            catch (JsonException) { }

            var previous = candidate.Status;
            candidate.Status = StagingStatus.Rejected;
            candidate.ReviewedAtUtc = now;
            candidate.ReviewedBy = "Mailbox supersession";
            candidate.ReviewNote = $"Superseded automatically by a newer Info mailbox message ({currentMessageId}). Original evidence retained.";
            db.StagedImportEvents.Add(StagingAudit.Create(candidate, "Superseded", previous, candidate.ReviewNote, candidate.ReviewedBy));
            count++;
        }
        if (count > 0) await db.SaveChangesAsync(ct);
        return count;
    }

    private static IReadOnlyList<string> ReadMatchKeys(JsonElement payload)
    {
        if (!TryGetProperty(payload, "intakeMatchKeys", out var value) || value.ValueKind != JsonValueKind.Array)
            return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => CanonicalMatchKey(item.GetString()!.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CanonicalMatchKey(string key)
    {
        var parts = key.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 &&
            string.Equals(parts[0], "NWF", StringComparison.OrdinalIgnoreCase) &&
            DateOnly.TryParse(parts[1], out _) &&
            (parts[2].StartsWith("PRODUCT:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("TRANSPORT:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("LOAD:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("CRATEREF:", StringComparison.OrdinalIgnoreCase)))
        {
            return $"NWF|{parts[2].ToUpperInvariant()}";
        }

        if (parts.Length >= 3 &&
            string.Equals(parts[0], "IFCO", StringComparison.OrdinalIgnoreCase) &&
            DateOnly.TryParse(parts[1], out _) &&
            (parts[2].StartsWith("TRANSPORT:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("CRATEPO:", StringComparison.OrdinalIgnoreCase) ||
             parts[2].StartsWith("LOAD:", StringComparison.OrdinalIgnoreCase)))
        {
            return $"IFCO|{parts[2].ToUpperInvariant()}";
        }

        return key.ToUpperInvariant();
    }

    private static string? ReadText(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }

    private static bool? ReadBool(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
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

    private static string CompactKey(string value)
    {
        var compact = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return compact.Length <= 96 ? compact : compact[^96..];
    }

    private static string EscapeForContains(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string SourceEvidenceKey(string messageId)
    {
        var key = $"email-evidence:{CompactKey(messageId)}";
        return key.Length <= 200 ? key : key[..200];
    }

    private async Task EnsureSourceEmailEvidence(MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var evidenceKey = SourceEvidenceKey(request.MessageId);
        var exists = await db.StagedImports.AsNoTracking()
            .AnyAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == evidenceKey, ct);
        if (exists) return;

        var bodyText = Clip(request.BodyText, SourceBodyTextLimit);
        var bodyHtml = Clip(request.BodyHtml, SourceBodyHtmlLimit);
        var payload = JsonSerializer.Serialize(new
        {
            messageId = request.MessageId,
            internetMessageId = request.InternetMessageId,
            conversationId = request.ConversationId,
            mailbox = request.Mailbox,
            senderAddress = request.SenderAddress,
            senderName = request.SenderName,
            subject = request.Subject,
            receivedAtUtc = request.ReceivedAtUtc,
            bodyText,
            bodyHtml,
            bodyFormat = request.BodyFormat,
            importance = request.Importance,
            webLink = request.WebLink,
            toRecipients = request.ToRecipients,
            ccRecipients = request.CcRecipients,
            correlationId = request.CorrelationId,
            attachments = (request.Attachments ?? []).Select(attachment => new
            {
                attachment.Name,
                attachment.ContentType,
                attachment.ContentId,
                attachment.Size,
                attachment.IsInline,
                contentBase64 = attachment.EffectiveContentBase64
            }).ToList(),
            bodyTruncated = (request.BodyText?.Length ?? 0) > SourceBodyTextLimit || (request.BodyHtml?.Length ?? 0) > SourceBodyHtmlLimit,
            evidenceAvailable = true
        });

        var now = DateTimeOffset.UtcNow;
        var evidence = new StagedImport
        {
            EntityType = "email-evidence",
            IdempotencyKey = evidenceKey,
            PayloadJson = payload,
            Status = StagingStatus.Archived,
            Source = $"Info mailbox evidence / {(request.SenderAddress ?? "unknown sender").Trim()}",
            ReceivedAtUtc = request.ReceivedAtUtc ?? now,
            ReviewedAtUtc = now,
            ReviewedBy = "Info mailbox intake",
            ReviewNote = "Immutable source email evidence retained for Order Review, including attachment copies supplied by Power Automate."
        };
        db.StagedImports.Add(evidence);
        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = evidence.Id,
            EventType = "EvidenceRetained",
            NewStatus = StagingStatus.Archived,
            PayloadJson = JsonSerializer.Serialize(new
            {
                evidenceKey,
                request.MessageId,
                request.InternetMessageId,
                request.Subject,
                request.ReceivedAtUtc,
                attachmentCount = (request.Attachments ?? []).Count,
                attachmentCopyCount = (request.Attachments ?? []).Count(item => item.IsInline != true && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64))
            }),
            Note = evidence.ReviewNote,
            Actor = evidence.ReviewedBy,
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<int> LinkOperationalUpdateToPendingOrders(MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var text = $"{request.Subject}\n{request.BodyText}";
        var customer = text.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ? "WAITROSE" : null;
        var siteMatch = Regex.Match(text, @"\b(?:collected|collect(?:ion)?)\s+from\s+(?<site>[A-Za-z][A-Za-z0-9 &'()/-]{1,80})", RegexOptions.IgnoreCase);
        var site = siteMatch.Success ? siteMatch.Groups["site"].Value.Trim().TrimEnd('.', ',', ';', ':') : null;
        if (customer is null || string.IsNullOrWhiteSpace(site)) return 0;

        var receivedDate = DateOnly.FromDateTime((request.ReceivedAtUtc ?? DateTimeOffset.UtcNow).Date).ToString("yyyy-MM-dd");
        var candidates = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview)
            .ToListAsync(ct);
        var actor = $"Mailbox status {CompactKey(request.MessageId)}";
        var linked = 0;
        foreach (var candidate in candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(candidate.PayloadJson);
                var payload = document.RootElement;
                if (!string.Equals(ReadText(payload, "customerCode"), customer, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadText(payload, "sellerName"), site, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(ReadText(payload, "collectionDate"), receivedDate, StringComparison.Ordinal))
                    continue;
                if (await db.StagedImportEvents.AnyAsync(item => item.StagedImportId == candidate.Id && item.Actor == actor, ct))
                    continue;

                db.StagedImportEvents.Add(new StagedImportEvent
                {
                    StagedImportId = candidate.Id,
                    EventType = "OperationalUpdateLinked",
                    NewStatus = candidate.Status,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        request.MessageId,
                        request.Subject,
                        request.ReceivedAtUtc,
                        sourceEvidenceKey = SourceEvidenceKey(request.MessageId),
                        update = request.BodyText
                    }),
                    Note = $"Operational mailbox update linked: {request.Subject}",
                    Actor = actor
                });
                linked++;
            }
            catch (JsonException) { }
        }
        if (linked > 0) await db.SaveChangesAsync(ct);
        return linked;
    }

    private static string? Clip(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return value.Length <= limit ? value : value[..limit];
    }

    private static (string? Text, bool Truncated) SourceBodyPreview(MailboxEmailIntakeRequest request)
    {
        var raw = request.BodyText;
        if (string.IsNullOrWhiteSpace(raw) && !string.IsNullOrWhiteSpace(request.BodyHtml))
        {
            raw = WebUtility.HtmlDecode(Regex.Replace(request.BodyHtml, "<[^>]+>", " "));
            raw = Regex.Replace(raw, @"\s+", " ").Trim();
        }
        if (string.IsNullOrWhiteSpace(raw)) return (null, false);
        return raw.Length <= SourceBodyPreviewLimit
            ? (raw, false)
            : (raw[..SourceBodyPreviewLimit], true);
    }

    internal static JsonElement EnrichSourceEvidence(JsonElement payload, MailboxEmailIntakeRequest request)
    {
        var root = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
        root["sourceMailbox"] = request.Mailbox;
        root["sourceSender"] = request.SenderAddress;
        root["sourceSenderName"] = request.SenderName;
        root["sourceEmailSubject"] = request.Subject;
        root["sourceEmailReceivedAt"] = request.ReceivedAtUtc;
        root["sourceEmailMessageId"] = request.MessageId;
        root["sourceMessageId"] = request.MessageId;
        root["sourceInternetMessageId"] = request.InternetMessageId;
        root["sourceConversationId"] = request.ConversationId;
        root["sourceEvidenceKey"] = SourceEvidenceKey(request.MessageId);
        root["sourceEmailWebLink"] = request.WebLink;
        root["sourceWebLink"] = request.WebLink;
        root["sourceSubject"] = request.Subject;
        root["sourceReceivedAtUtc"] = request.ReceivedAtUtc;
        root["sourceBodyFormat"] = request.BodyFormat;
        root["sourceImportance"] = request.Importance;
        root["importCorrelationId"] = request.CorrelationId;
        root["sourceToRecipients"] = request.ToRecipients is { } to ? JsonNode.Parse(to.GetRawText()) : null;
        root["sourceCcRecipients"] = request.CcRecipients is { } cc ? JsonNode.Parse(cc.GetRawText()) : null;
        var preview = SourceBodyPreview(request);
        root["sourceBodyText"] = preview.Text;
        root["sourceBodyPreviewTruncated"] = preview.Truncated;

        var attachments = new JsonArray();
        foreach (var attachment in request.Attachments ?? [])
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
        root["sourceAttachments"] = attachments;
        root["importSource"] = "PowerAutomate/InfoMailbox";
        root["reviewStatus"] = "Pending Review";
        return JsonSerializer.SerializeToElement(root);
    }
}
