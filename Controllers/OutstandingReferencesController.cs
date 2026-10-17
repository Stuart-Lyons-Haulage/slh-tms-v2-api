using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Authorize]
public sealed class OutstandingReferencesController(TmsDbContext db, OrderCompletenessService completeness) : ControllerBase
{
    [HttpGet("api/v1/operations/completeness")]
    public async Task<IActionResult> Completeness(CancellationToken ct) => Ok(await completeness.ReadCompletenessAsync(ct));

    [HttpGet("api/v1/outstanding-references")]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        await completeness.RefreshReferenceIssuesAsync(ct);
        var issues = await db.OrderReferenceIssues.AsNoTracking().Where(x => x.Status == ReferenceIssueStatus.Open).OrderBy(x => x.DetectedAtUtc).ToListAsync(ct);
        var movementIds = issues.Select(x => x.MovementId).Distinct().ToList();
        var movements = await db.OrderMovements.AsNoTracking().Where(x => movementIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var issueIds = issues.Select(i => i.Id).ToList();
        var events = await db.ReferenceChaseEvents.AsNoTracking().Where(x => issueIds.Contains(x.ReferenceIssueId)).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return Ok(new { count = issues.Count, references = issues.Select(issue => new
        {
            issue.Id, issue.MovementId, issue.TransportOrderId, issue.ReferenceType, customer = movements[issue.MovementId].CustomerCode,
            ageDays = Math.Max(0, (int)(now - issue.DetectedAtUtc).TotalDays), issue.Owner, issue.Notes, issue.DetectedAtUtc,
            lastChasedAtUtc = events.Where(x => x.ReferenceIssueId == issue.Id && x.EventType == "Sent").MaxBy(x => x.OccurredAtUtc)?.OccurredAtUtc
        }) });
    }

    [HttpPost("api/v1/outstanding-references/{id:guid}/draft-chase"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Draft(Guid id, CancellationToken ct)
    {
        var issue = await db.OrderReferenceIssues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Status == ReferenceIssueStatus.Open, ct);
        if (issue is null) return NotFound();
        var movement = await db.OrderMovements.AsNoTracking().SingleAsync(x => x.Id == issue.MovementId, ct);
        var revision = movement.CurrentRevisionId is null ? null : await db.OrderRevisions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == movement.CurrentRevisionId, ct);
        var (sender, subject, messageId, conversationId) = Source(revision?.PayloadJson);
        var contact = string.IsNullOrWhiteSpace(sender) ? await db.CustomerContacts.AsNoTracking().Where(x => x.Active && x.CustomerCode == movement.CustomerCode && x.Email != null).OrderBy(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var recipient = sender ?? contact?.Email;
        if (string.IsNullOrWhiteSpace(recipient)) return Conflict(new { code = "reference_chase_recipient_missing", message = "No original sender or mapped customer email is available." });
        return Ok(new
        {
            issueId = issue.Id, to = recipient, replyToMessageId = messageId, conversationId,
            subject = string.IsNullOrWhiteSpace(subject) ? $"Reference required for {movement.CustomerCode} movement" : $"RE: {subject}",
            body = $"Please could you provide the missing {Display(issue.ReferenceType)} for the {movement.CustomerCode} movement. The transport work will continue as agreed; this request is to complete our order records.",
            requiresReview = true, sendsAutomatically = false
        });
    }

    [HttpPost("api/v1/outstanding-references/{id:guid}/record-sent"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RecordSent(Guid id, ReferenceSentRequest request, CancellationToken ct)
    {
        var issue = await db.OrderReferenceIssues.SingleOrDefaultAsync(x => x.Id == id && x.Status == ReferenceIssueStatus.Open, ct);
        if (issue is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.Recipient)) return BadRequest(new { code = "recipient_required" });
        db.ReferenceChaseEvents.Add(new ReferenceChaseEvent { ReferenceIssueId = id, EventType = "Sent", Recipient = request.Recipient.Trim(), ProviderMessageId = request.ProviderMessageId, ProviderThreadId = request.ProviderThreadId, Note = request.Note, Actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value });
        await db.SaveChangesAsync(ct);
        return Ok(new { issueId = id, sentAtUtc = DateTimeOffset.UtcNow });
    }

    [HttpPost("api/v1/outstanding-references/{id:guid}/resolve"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Resolve(Guid id, ReferenceResolveRequest request, CancellationToken ct)
    {
        var issue = await db.OrderReferenceIssues.SingleOrDefaultAsync(x => x.Id == id && x.Status == ReferenceIssueStatus.Open, ct);
        if (issue is null) return NotFound();
        if (string.IsNullOrWhiteSpace(request.ReferenceValue)) return BadRequest(new { code = "reference_value_required" });
        var movement = await db.OrderMovements.SingleAsync(x => x.Id == issue.MovementId, ct);
        if (movement.CurrentRevisionId is Guid revisionId)
        {
            if (issue.ReferenceType == "LoadReference")
            {
                var lines = await db.OrderSourceLines.Where(x => x.RevisionId == revisionId).ToListAsync(ct);
                foreach (var line in lines.Where(x => string.IsNullOrWhiteSpace(x.LoadReference))) line.LoadReference = request.ReferenceValue.Trim();
            }
            else if (issue.ReferenceType == "PO")
            {
                var revision = await db.OrderRevisions.SingleAsync(x => x.Id == revisionId, ct);
                var root = JsonNode.Parse(revision.PayloadJson)?.AsObject() ?? new JsonObject();
                root["poNumber"] = request.ReferenceValue.Trim();
                revision.PayloadJson = root.ToJsonString();
            }
        }
        issue.Status = ReferenceIssueStatus.Resolved; issue.ResolvedAtUtc = DateTimeOffset.UtcNow; issue.ResolvedBy = User.Identity?.Name ?? User.FindFirst("oid")?.Value; issue.Notes = string.Join(" | ", new[] { issue.Notes, $"Supplied: {request.ReferenceValue.Trim()}", request.Note }.Where(x => !string.IsNullOrWhiteSpace(x)));
        db.ReferenceChaseEvents.Add(new ReferenceChaseEvent { ReferenceIssueId = id, EventType = "Resolved", Note = issue.Notes, Actor = issue.ResolvedBy });
        await db.SaveChangesAsync(ct);
        return Ok(new { issueId = id, status = issue.Status.ToString(), issue.ResolvedAtUtc });
    }

    private static (string? Sender, string? Subject, string? MessageId, string? ConversationId) Source(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { using var document = JsonDocument.Parse(json); var root = document.RootElement; return (Text(root, "sourceSender"), Text(root, "sourceEmailSubject", "sourceSubject"), Text(root, "sourceEmailMessageId", "sourceMessageId"), Text(root, "sourceConversationId")); }
        catch (JsonException) { return default; }
    }
    private static string? Text(JsonElement root, params string[] names) { foreach (var name in names) if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static string Display(string value) => value == "LoadReference" ? "load reference" : "purchase order number";
}

public sealed record ReferenceSentRequest(string Recipient, string? ProviderMessageId, string? ProviderThreadId, string? Note);
public sealed record ReferenceResolveRequest(string ReferenceValue, string? Note);
