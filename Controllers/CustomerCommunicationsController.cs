using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/customer-communications"), Authorize]
public sealed class CustomerCommunicationsController(TmsDbContext db, StagingService staging, CustomerCommunicationExtractionService extractor) : ControllerBase
{
    private const string SentMarker = "[CUSTOMER-ACK:SENT:";
    private static readonly JsonSerializerOptions StoredPayloadJson = new(JsonSerializerDefaults.Web);

    [HttpGet("pending"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Pending([FromQuery] int take = 50, CancellationToken ct = default)
    {
        var staged = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == "order" && x.Source!.StartsWith("Info mailbox") && (x.Status == StagingStatus.PendingReview || x.Status == StagingStatus.Promoted)).OrderByDescending(x => x.ReceivedAtUtc).Take(2000).ToListAsync(ct);
        var communications = new List<object>();
        foreach (var group in staged.Select(TryParse).Where(x => x is not null).Cast<CommunicationSource>().Where(x => !string.IsNullOrWhiteSpace(x.SourceMessageId) && !x.SourceSender!.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase)).GroupBy(x => x.SourceMessageId!, StringComparer.Ordinal))
        {
            if (group.Any(x => x.ReviewNote?.Contains(SentMarker, StringComparison.Ordinal) == true)) continue;
            var accepted = group.Where(x => x.Status == StagingStatus.Promoted && x.PlannerReady != false && !string.Equals(x.IntakeStatus, "PreOrder", StringComparison.OrdinalIgnoreCase)).ToList();
            if (accepted.Count == 0) continue;
            var first = accepted[0]; var key = CommunicationKey(group.Key); var summary = accepted.Count > 1 || accepted.Any(x => x.CustomerCode == "NWF");
            var awaitingInstruction = group.Count(x => x.Status == StagingStatus.PendingReview && (x.PlannerReady == false || string.Equals(x.IntakeStatus, "PreOrder", StringComparison.OrdinalIgnoreCase)));
            communications.Add(new { communicationKey = key, kind = summary ? "OrderSummaryAccepted" : "OrderAccepted", sourceMessageId = first.SourceMessageId, sourceInternetMessageId = first.SourceInternetMessageId, sourceSubject = first.SourceSubject, replyTo = first.SourceSender, acceptedCount = accepted.Count, awaitingInstructionCount = awaitingInstruction, references = accepted.Select(x => x.Reference).Where(x => x is not null).Distinct().ToList(), bodyHtml = BuildAcceptedHtml(accepted, awaitingInstruction, summary), idempotencyKey = $"customer-ack:{key}" });
            if (communications.Count >= Math.Clamp(take, 1, 200)) break;
        }
        return Ok(new { count = communications.Count, communications });
    }

    [HttpPost("{communicationKey}/sent"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> MarkSent(string communicationKey, CommunicationSentRequest request, CancellationToken ct)
    {
        var staged = await db.StagedImports.Where(x => x.EntityType == "order" && x.Source!.StartsWith("Info mailbox")).OrderByDescending(x => x.ReceivedAtUtc).Take(2000).ToListAsync(ct);
        var matching = staged.Select(x => new { Item = x, Parsed = TryParse(x) }).Where(x => x.Parsed is not null && CommunicationKey(x.Parsed.SourceMessageId!) == communicationKey).ToList();
        if (matching.Count == 0) return NotFound();
        if (matching.Any(x => x.Item.ReviewNote?.Contains(SentMarker, StringComparison.Ordinal) == true)) return Ok(new { communicationKey, alreadySent = true });
        var marker = $"{SentMarker}{communicationKey}:{DateTimeOffset.UtcNow:O}]";
        foreach (var item in matching) item.Item.ReviewNote = string.Join(" | ", new[] { item.Item.ReviewNote, marker, request.ProviderMessageId }.Where(x => !string.IsNullOrWhiteSpace(x)));
        await db.SaveChangesAsync(ct); return Ok(new { communicationKey, marked = matching.Count });
    }

    [HttpGet]
    public async Task<IActionResult> Ledger([FromQuery] StagingStatus? status, [FromQuery] string? purpose, [FromQuery] int take = 100, CancellationToken ct = default)
    {
        var query = db.StagedImports.AsNoTracking().Where(x => x.EntityType == "communication");
        if (status is not null) query = query.Where(x => x.Status == status);
        var rows = await query.OrderByDescending(x => x.ReceivedAtUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
        var result = rows.Select(x => new { x.Id, x.Status, x.IdempotencyKey, x.Source, x.ReceivedAtUtc, x.ReviewedAtUtc, x.ReviewedBy, x.ReviewNote, payload = Parse(x.PayloadJson) });
        if (!string.IsNullOrWhiteSpace(purpose)) result = result.Where(x => x.payload.GetProperty("extraction").GetProperty("purpose").GetString()!.Equals(purpose, StringComparison.OrdinalIgnoreCase));
        return Ok(result);
    }

    [HttpPost("ingest"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Ingest(MailboxEmailIntakeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.MessageId)) return BadRequest(new { error = "message_id_required" });
        var key = $"communication:{request.MessageId}"; var existing = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (existing is not null) return Ok(new { existing = true, existing.Id, existing.Status });
        var extraction = extractor.Extract(request);
        var payload = JsonSerializer.Serialize(new { source = new { request.MessageId, request.InternetMessageId, request.Mailbox, request.SenderAddress, request.SenderName, request.Subject, request.ReceivedAtUtc, request.BodyText, request.BodyHtml, request.WebLink, request.ConversationId, request.ToRecipients, request.CcRecipients, request.BodyFormat, request.Importance, request.CorrelationId, attachments = (request.Attachments ?? []).Select(x => new { x.Name, x.ContentType, x.IsInline, x.ContentId, x.Size }) }, extraction }, StoredPayloadJson);
        var item = new StagedImport { EntityType = "communication", IdempotencyKey = key, PayloadJson = payload, Source = request.Mailbox ?? "Mailbox communication" };
        db.StagedImports.Add(item); db.StagedImportEvents.Add(StagingAudit.Create(item, "Received")); await db.SaveChangesAsync(ct);
        return Accepted(new { id = item.Id, item.Status, item.ReceivedAtUtc, purpose = extraction.Purpose });
    }

    [HttpPost("{id:guid}/approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Approve(Guid id, ReviewNote request, CancellationToken ct) => await Review(id, true, request.Note, ct);
    [HttpPost("{id:guid}/reject"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, ReviewNote request, CancellationToken ct) => await Review(id, false, request.Note, ct);
    private async Task<IActionResult> Review(Guid id, bool approve, string? note, CancellationToken ct)
    {
        if (!await db.StagedImports.AsNoTracking().AnyAsync(x => x.Id == id && x.EntityType == "communication", ct)) return NotFound();
        try { return Ok(await staging.ReviewAndPromote(id, approve, note, User, ct)); } catch (InvalidOperationException ex) { return Conflict(new { error = "communication_review_conflict", message = ex.Message }); }
    }

    private static JsonElement Parse(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }
    private static CommunicationSource? TryParse(StagedImport item) { try { using var document = JsonDocument.Parse(item.PayloadJson); var p = document.RootElement; return new(item.Status, item.ReviewNote, Text(p, "sourceMessageId"), Text(p, "sourceInternetMessageId"), Text(p, "sourceSender"), Text(p, "sourceSubject"), Text(p, "poNumber") ?? Text(p, "customerPo") ?? Text(p, "productPo"), Text(p, "customerCode"), Text(p, "collectionDate"), Text(p, "deliveryDate"), Text(p, "sellerName"), Text(p, "stallNumber"), Int(p, "pallets"), Bool(p, "plannerReady"), Text(p, "intakeStatus")); } catch (JsonException) { return null; } }
    private static string BuildAcceptedHtml(IReadOnlyList<CommunicationSource> orders, int awaitingInstruction, bool summary) { var e = HtmlEncoder.Default; var html = new StringBuilder($"<p>{(summary ? "We have reviewed the latest instruction and accepted the following movements." : "Your order has been received, reviewed and passed to Planning.")}</p><table><tr><th>Reference</th><th>Collection</th><th>Delivery</th><th>Pallets</th></tr>"); foreach (var x in orders) html.Append($"<tr><td>{e.Encode(x.Reference ?? "—")}</td><td>{e.Encode(x.CollectionSite ?? "—")}</td><td>{e.Encode(x.DeliverySite ?? "—")}</td><td>{x.Pallets}</td></tr>"); html.Append("</table>"); if (awaitingInstruction > 0) html.Append($"<p><strong>{awaitingInstruction}</strong> pre-order item(s) remain awaiting instruction and have not been committed to Planning.</p>"); return html.Append("<p>Amendments and cancellations will be reviewed against the existing order.</p>").ToString(); }
    private static string CommunicationKey(string sourceMessageId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceMessageId)))[..24].ToLowerInvariant();
    private static string? Text(JsonElement p, string n) { if (!p.TryGetProperty(n, out var v)) { foreach (var property in p.EnumerateObject()) if (property.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) { v = property.Value; break; } } return v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null; }
    private static int? Int(JsonElement p, string n) => int.TryParse(Text(p, n), out var x) ? x : null;
    private static bool? Bool(JsonElement p, string n) { if (!p.TryGetProperty(n, out var v)) { foreach (var property in p.EnumerateObject()) if (property.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) { v = property.Value; break; } } return v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : null; }
    private sealed record CommunicationSource(StagingStatus Status, string? ReviewNote, string? SourceMessageId, string? SourceInternetMessageId, string? SourceSender, string? SourceSubject, string? Reference, string? CustomerCode, string? CollectionDate, string? DeliveryDate, string? CollectionSite, string? DeliverySite, int? Pallets, bool? PlannerReady, string? IntakeStatus);
}

public sealed record CommunicationSentRequest(string? ProviderMessageId);
public sealed record ReviewNote(string? Note);
