using System.Net;
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
/// Compatibility wrapper for the production Info mailbox endpoint.
///
/// The original OrderIntakeController remains the canonical parser/stager. This route has a
/// lower route order so production calls pass through here first; after the canonical intake
/// completes we are able to upgrade an already-retained source-evidence snapshot when a replay
/// contains better body text or attachment bytes. Orders themselves remain idempotent.
/// </summary>
[ApiController]
[Route("api/v1/order-intake", Order = -100)]
[Authorize]
public sealed class InfoMailboxIntakeUpgradeController(
    TmsDbContext db,
    StagingService stagingService,
    ILogger<OrderIntakeController> intakeLogger) : ControllerBase
{
    private const int SourceBodyTextLimit = 200000;
    private const int SourceBodyHtmlLimit = 400000;

    [HttpPost("email"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Intake([FromBody] MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        // Preserve all existing parsing, dedupe, staging and mapping behaviour.
        var canonical = new OrderIntakeController(db, stagingService, intakeLogger)
        {
            ControllerContext = new ControllerContext { HttpContext = HttpContext }
        };

        var result = await canonical.Intake(request, ct);
        if (!string.IsNullOrWhiteSpace(request.MessageId))
            await UpgradeStoredEvidenceAsync(request, ct);

        return result;
    }

    private async Task UpgradeStoredEvidenceAsync(MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        var evidenceKey = SourceEvidenceKey(request.MessageId);
        var evidence = await db.StagedImports
            .SingleOrDefaultAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == evidenceKey, ct);
        if (evidence is null) return;

        var upgrade = UpgradeEvidencePayload(evidence.PayloadJson, request);
        if (!upgrade.Changed) return;

        var now = DateTimeOffset.UtcNow;
        evidence.PayloadJson = upgrade.PayloadJson;
        evidence.ReviewedAtUtc = now;
        evidence.ReviewedBy = "Info mailbox evidence upgrade";
        evidence.ReviewNote = "Source email evidence upgraded from a replay with cleaner body text and/or retained attachment bytes. Existing good evidence was preserved.";

        db.StagedImportEvents.Add(new StagedImportEvent
        {
            StagedImportId = evidence.Id,
            EventType = "EvidenceUpgraded",
            NewStatus = evidence.Status,
            PayloadJson = JsonSerializer.Serialize(new
            {
                evidenceKey,
                request.MessageId,
                request.InternetMessageId,
                request.Subject,
                request.ReceivedAtUtc,
                attachmentCount = (request.Attachments ?? []).Count,
                attachmentCopyCount = (request.Attachments ?? []).Count(item => item.IsInline != true && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64)),
                upgrade.CleanBodyApplied,
                upgrade.AttachmentCopiesAdded
            }),
            Note = evidence.ReviewNote,
            Actor = evidence.ReviewedBy,
            OccurredAtUtc = now
        });

        await db.SaveChangesAsync(ct);
    }

    internal static EvidenceUpgradeResult UpgradeEvidencePayload(string existingPayload, MailboxEmailIntakeRequest request)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(existingPayload)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        var before = root.ToJsonString();
        var cleanBodyApplied = false;
        var attachmentCopiesAdded = 0;

        // Metadata is safe to fill from the same immutable Outlook message. Prefer the new
        // sender display name when the old flow stored the address in both fields.
        FillIfMissing(root, "messageId", request.MessageId);
        FillIfMissing(root, "internetMessageId", request.InternetMessageId);
        FillIfMissing(root, "conversationId", request.ConversationId);
        FillIfMissing(root, "mailbox", request.Mailbox);
        FillIfMissing(root, "senderAddress", request.SenderAddress);
        FillIfMissing(root, "subject", request.Subject);
        FillIfMissing(root, "receivedAtUtc", request.ReceivedAtUtc?.ToString("O"));
        FillIfMissing(root, "importance", request.Importance);
        FillIfMissing(root, "webLink", request.WebLink);
        FillIfMissing(root, "correlationId", request.CorrelationId);

        var existingSender = Text(root["senderAddress"]);
        var existingSenderName = Text(root["senderName"]);
        if (!string.IsNullOrWhiteSpace(request.SenderName) &&
            (string.IsNullOrWhiteSpace(existingSenderName) || string.Equals(existingSenderName, existingSender, StringComparison.OrdinalIgnoreCase)))
            root["senderName"] = request.SenderName;

        if (request.ToRecipients is { } to && IsEmptyNode(root["toRecipients"]))
            root["toRecipients"] = JsonNode.Parse(to.GetRawText());
        if (request.CcRecipients is { } cc && IsEmptyNode(root["ccRecipients"]))
            root["ccRecipients"] = JsonNode.Parse(cc.GetRawText());

        var incomingBodyText = CleanBodyText(request.BodyText, request.BodyHtml);
        var existingBodyText = Text(root["bodyText"]);
        if (!string.IsNullOrWhiteSpace(incomingBodyText) &&
            (string.IsNullOrWhiteSpace(existingBodyText) || LooksLikeHtml(existingBodyText)))
        {
            root["bodyText"] = Clip(incomingBodyText, SourceBodyTextLimit);
            cleanBodyApplied = true;
        }

        var incomingHtml = Clip(request.BodyHtml, SourceBodyHtmlLimit);
        var existingHtml = Text(root["bodyHtml"]);
        if (!string.IsNullOrWhiteSpace(incomingHtml) &&
            (string.IsNullOrWhiteSpace(existingHtml) || incomingHtml!.Length > existingHtml.Length))
            root["bodyHtml"] = incomingHtml;

        var incomingFormat = NormaliseBodyFormat(request.BodyFormat);
        var existingFormat = NormaliseBodyFormat(Text(root["bodyFormat"]));
        if (!string.IsNullOrWhiteSpace(incomingFormat) && string.IsNullOrWhiteSpace(existingFormat))
            root["bodyFormat"] = incomingFormat;

        var incomingTruncated = (request.BodyText?.Length ?? 0) > SourceBodyTextLimit || (request.BodyHtml?.Length ?? 0) > SourceBodyHtmlLimit;
        if (root["bodyTruncated"] is null || root["bodyTruncated"]?.GetValueKind() is JsonValueKind.Null)
            root["bodyTruncated"] = incomingTruncated;
        else if (!incomingTruncated && cleanBodyApplied)
            root["bodyTruncated"] = false;
        root["evidenceAvailable"] = true;

        var attachments = root["attachments"] as JsonArray ?? new JsonArray();
        if (root["attachments"] is not JsonArray) root["attachments"] = attachments;

        foreach (var incoming in request.Attachments ?? [])
        {
            var incomingName = incoming.Name?.Trim();
            if (string.IsNullOrWhiteSpace(incomingName)) continue;

            var match = attachments
                .OfType<JsonObject>()
                .FirstOrDefault(item => AttachmentMatches(item, incomingName, incoming.ContentId));

            if (match is null)
            {
                match = BuildAttachmentNode(incoming);
                attachments.Add(match);
                if (!string.IsNullOrWhiteSpace(incoming.EffectiveContentBase64) && incoming.IsInline != true)
                    attachmentCopiesAdded++;
                continue;
            }

            FillIfMissing(match, "name", incoming.Name);
            FillIfMissing(match, "contentType", incoming.ContentType);
            FillIfMissing(match, "contentId", incoming.ContentId);
            if (ReadLong(match["size"]) <= 0 && incoming.Size is > 0) match["size"] = incoming.Size;
            if (match["isInline"] is null && incoming.IsInline is not null) match["isInline"] = incoming.IsInline;

            var existingCopy = Text(match["contentBase64"]);
            if (string.IsNullOrWhiteSpace(existingCopy) && !string.IsNullOrWhiteSpace(incoming.EffectiveContentBase64))
            {
                match["contentBase64"] = incoming.EffectiveContentBase64;
                if (incoming.IsInline != true) attachmentCopiesAdded++;
            }
        }

        var after = root.ToJsonString();
        return new EvidenceUpgradeResult(after, !string.Equals(before, after, StringComparison.Ordinal), cleanBodyApplied, attachmentCopiesAdded);
    }

    private static JsonObject BuildAttachmentNode(MailboxAttachmentRequest attachment) => new()
    {
        ["name"] = attachment.Name,
        ["contentType"] = attachment.ContentType,
        ["contentId"] = attachment.ContentId,
        ["size"] = attachment.Size,
        ["isInline"] = attachment.IsInline,
        ["contentBase64"] = attachment.EffectiveContentBase64
    };

    private static bool AttachmentMatches(JsonObject existing, string incomingName, string? incomingContentId)
    {
        var nameMatches = string.Equals(Text(existing["name"]), incomingName, StringComparison.OrdinalIgnoreCase);
        var existingContentId = Text(existing["contentId"]);
        var contentIdMatches = !string.IsNullOrWhiteSpace(incomingContentId) &&
                               !string.IsNullOrWhiteSpace(existingContentId) &&
                               string.Equals(existingContentId, incomingContentId, StringComparison.OrdinalIgnoreCase);
        return contentIdMatches || nameMatches;
    }

    private static void FillIfMissing(JsonObject target, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !IsEmptyNode(target[name])) return;
        target[name] = value;
    }

    private static bool IsEmptyNode(JsonNode? node)
    {
        if (node is null || node.GetValueKind() is JsonValueKind.Null) return true;
        return node is JsonValue value && string.IsNullOrWhiteSpace(value.ToString());
    }

    private static string? CleanBodyText(string? bodyText, string? bodyHtml)
    {
        var candidate = bodyText;
        if (string.IsNullOrWhiteSpace(candidate)) candidate = bodyHtml;
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        if (!LooksLikeHtml(candidate)) return WebUtility.HtmlDecode(candidate).Trim();

        var withoutTags = Regex.Replace(candidate, "<[^>]+>", " ");
        return Regex.Replace(WebUtility.HtmlDecode(withoutTags), @"\s+", " ").Trim();
    }

    private static bool LooksLikeHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Regex.IsMatch(value, @"<\s*(?:!doctype|html|body|div|p|table|tr|td|span|br|strong|a)(?:\s|>|/)", RegexOptions.IgnoreCase);
    }

    private static string? NormaliseBodyFormat(string? value)
    {
        if (string.Equals(value, "html", StringComparison.OrdinalIgnoreCase)) return "html";
        if (string.Equals(value, "text", StringComparison.OrdinalIgnoreCase)) return "text";
        return null;
    }

    private static string? Clip(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return value.Length <= limit ? value : value[..limit];
    }

    private static string Text(JsonNode? node)
    {
        if (node is null || node.GetValueKind() is JsonValueKind.Null) return string.Empty;
        try { return node.GetValue<string>()?.Trim() ?? string.Empty; }
        catch (InvalidOperationException) { return node.ToString().Trim(); }
    }

    private static long ReadLong(JsonNode? node)
    {
        if (node is null) return 0;
        try { return node.GetValue<long>(); }
        catch { return long.TryParse(node.ToString(), out var value) ? value : 0; }
    }

    private static string SourceEvidenceKey(string messageId)
    {
        var compact = new string(messageId.Where(char.IsLetterOrDigit).ToArray());
        if (compact.Length > 96) compact = compact[^96..];
        var key = $"email-evidence:{compact}";
        return key.Length <= 200 ? key : key[..200];
    }

    internal sealed record EvidenceUpgradeResult(string PayloadJson, bool Changed, bool CleanBodyApplied, int AttachmentCopiesAdded);
}
