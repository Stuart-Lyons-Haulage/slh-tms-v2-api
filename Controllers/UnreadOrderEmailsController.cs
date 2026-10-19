using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Customer emails the TMS recognised as orders but couldn't read (the "Needs date" cards).
/// Lists each one with who sent it and whether its orders have since been captured, and can
/// re-read one email through the full intake for every date it covers.
/// </summary>
[ApiController]
[Route("api/v1/order-intake/unread-emails")]
[Authorize]
public sealed class UnreadOrderEmailsController(
    TmsDbContext db,
    StagingService stagingService,
    ILogger<OrderIntakeController> intakeLogger) : ControllerBase
{
    private static readonly StagingStatus[] Captured = [StagingStatus.PendingReview, StagingStatus.Approved, StagingStatus.Promoted];

    public sealed record UnreadEmail(Guid Id, string? Sender, string? Subject, DateTimeOffset? ReceivedAtUtc, string? Warning, string? Customer, int CapturedOrders, bool EvidenceKept);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var placeholders = await PlaceholdersAsync(ct);
        var result = new List<UnreadEmail>();
        foreach (var item in placeholders)
        {
            var payload = Read(item.PayloadJson);
            var messageId = Text(payload, "sourceMessageId", "sourceEmailMessageId");
            result.Add(new UnreadEmail(
                item.Id,
                Text(payload, "sourceSender") ?? item.Source?.Split(" / ").LastOrDefault(),
                Text(payload, "sourceEmailSubject", "sourceSubject"),
                DateTimeOffset.TryParse(Text(payload, "sourceEmailReceivedAt"), out var received) ? received : item.ReceivedAtUtc,
                Text(payload, "intakeWarning"),
                Text(payload, "customerCode"),
                messageId is null ? 0 : await CapturedCountAsync(messageId, ct),
                await EvidenceAsync(payload, ct) is not null));
        }
        return Ok(result.OrderByDescending(item => item.ReceivedAtUtc));
    }

    /// <summary>Runs one email back through the full intake. Orders for every date are staged; the card clears if any come out.</summary>
    [HttpPost("{id:guid}/reread"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reread(Guid id, CancellationToken ct)
    {
        var placeholder = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, ct);
        if (placeholder is null) return NotFound(new { message = "That card has already been dealt with." });
        var payload = Read(placeholder.PayloadJson);
        var evidence = await EvidenceAsync(payload, ct);
        if (evidence is null) return Ok(new { orders = 0, cleared = false, message = "The original email isn't stored, so it can't be re-read. Open it in Outlook and add the orders by hand." });

        MailboxEmailIntakeRequest request;
        try { request = RetainedOrderEvidenceReplayController.Rehydrate(evidence.PayloadJson); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Ok(new { orders = 0, cleared = false, message = $"The stored email couldn't be opened: {ex.GetBaseException().Message}" });
        }

        var intake = new OrderIntakeController(db, stagingService, intakeLogger) { ControllerContext = ControllerContext };
        await intake.Intake(request, ct);
        db.ChangeTracker.Clear();

        var captured = request.MessageId is null ? 0 : await CapturedCountAsync(request.MessageId, ct);
        var stillOpen = await db.StagedImports.AsNoTracking().AnyAsync(item => item.Id == id && item.Status == StagingStatus.PendingReview, ct);
        if (captured > 0 && stillOpen) await CloseAsync(id, "Orders from this email are captured; the unread-email card is closed.", ct);
        return Ok(new
        {
            orders = captured,
            cleared = captured > 0,
            message = captured > 0
                ? $"{captured} order{(captured == 1 ? "" : "s")} from this email {(captured == 1 ? "is" : "are")} now in Orders."
                : $"Still can't read any orders from it. {Text(Read((await db.StagedImports.AsNoTracking().SingleAsync(item => item.Id == id, ct)).PayloadJson), "intakeWarning") ?? "Add them by hand from the email."}"
        });
    }

    /// <summary>Closes a card whose email's orders are already in the TMS.</summary>
    [HttpPost("{id:guid}/close-captured"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> CloseCaptured(Guid id, CancellationToken ct)
    {
        var placeholder = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.Status == StagingStatus.PendingReview, ct);
        if (placeholder is null) return NotFound(new { message = "That card has already been dealt with." });
        var messageId = Text(Read(placeholder.PayloadJson), "sourceMessageId", "sourceEmailMessageId");
        var captured = messageId is null ? 0 : await CapturedCountAsync(messageId, ct);
        if (captured == 0) return BadRequest(new { message = "No orders from this email are in the TMS yet, so the card stays open." });
        await CloseAsync(id, $"Closed by {User.Identity?.Name}: {captured} order(s) from this email are already captured.", ct);
        return Ok(new { closed = true, captured });
    }

    private async Task<List<StagedImport>> PlaceholdersAsync(CancellationToken ct) =>
        (await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview &&
                           ((item.Source != null && item.Source.StartsWith("Info mailbox mapping exception")) ||
                            item.IdempotencyKey.EndsWith(":mapping-exception")))
            .ToListAsync(ct));

    private async Task<int> CapturedCountAsync(string messageId, CancellationToken ct)
    {
        var prefix = OrderIntakeController.BuildOrderIdempotencyKey(messageId, string.Empty);
        if (prefix.Length >= 200) return 0;
        return await db.StagedImports.AsNoTracking()
            .CountAsync(item => item.EntityType == "order" && Captured.Contains(item.Status) &&
                                item.IdempotencyKey.StartsWith(prefix) && !item.IdempotencyKey.EndsWith(":mapping-exception"), ct);
    }

    private async Task<StagedImport?> EvidenceAsync(JsonElement payload, CancellationToken ct)
    {
        var key = Text(payload, "sourceEvidenceKey");
        if (key is not null)
        {
            var byKey = await db.StagedImports.AsNoTracking().FirstOrDefaultAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == key, ct);
            if (byKey is not null) return byKey;
        }
        var messageId = Text(payload, "sourceMessageId", "sourceEmailMessageId");
        if (messageId is null) return null;
        var fallback = SourceEvidenceKeyBuilder.For(messageId, Text(payload, "sourceInternetMessageId"));
        return await db.StagedImports.AsNoTracking().FirstOrDefaultAsync(item => item.EntityType == "email-evidence" && item.IdempotencyKey == fallback, ct);
    }

    private async Task CloseAsync(Guid id, string note, CancellationToken ct)
    {
        var item = await db.StagedImports.SingleAsync(row => row.Id == id, ct);
        var previous = item.Status;
        item.Status = StagingStatus.Archived;
        item.IdempotencyKey = $"archived-mapping:{item.Id:N}";
        item.ReviewedAtUtc = DateTimeOffset.UtcNow;
        item.ReviewedBy = User.Identity?.Name ?? "Unread emails";
        item.ReviewNote = note;
        db.StagedImportEvents.Add(StagingAudit.Create(item, "MappingExceptionResolved", previous, note, item.ReviewedBy));
        await db.SaveChangesAsync(ct);
    }

    private static JsonElement Read(string json)
    {
        try { using var document = JsonDocument.Parse(json); return document.RootElement.Clone(); }
        catch (JsonException) { return default; }
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                return value.GetString()!.Trim();
        return null;
    }
}
