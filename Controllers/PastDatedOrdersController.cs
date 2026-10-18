using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Orders still waiting for review whose collection and delivery dates have all gone. They
/// can't be planned any more, so they only clutter the queue and the counts. Planners see
/// the list and close them in one go (recorded as rejected, with a note, so nothing is lost).
/// </summary>
[ApiController]
[Route("api/v1/staging/queue/past-dated")]
[Authorize]
public sealed class PastDatedOrdersController(TmsDbContext db, StagingService service) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    public const string CloseNote = "Closed: date passed while waiting for review";

    public sealed record PastDatedOrder(Guid Id, string? Customer, string? Reference, DateOnly LatestDate, DateTimeOffset ReceivedAtUtc, string? Subject);
    public sealed record CloseRequest(List<Guid> Ids);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var items = await FindAsync(Today(), ct);
        return Ok(new { today = Today(), count = items.Count, oldest = items.Count == 0 ? (DateOnly?)null : items.Min(item => item.LatestDate), items });
    }

    [HttpPost("close"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Close(CloseRequest request, CancellationToken ct)
    {
        if (request.Ids is not { Count: > 0 }) return BadRequest(new { message = "Choose at least one order to close." });
        // Only close what is still waiting and still past-dated now, whatever the screen sent.
        var allowed = (await FindAsync(Today(), ct)).Select(item => item.Id).ToHashSet();
        var closed = 0;
        var skipped = 0;
        foreach (var id in request.Ids.Distinct())
        {
            if (!allowed.Contains(id)) { skipped++; continue; }
            try { await service.ReviewAndPromote(id, false, CloseNote, User, ct); closed++; }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or DbUpdateConcurrencyException) { skipped++; db.ChangeTracker.Clear(); }
        }
        return Ok(new { closed, skipped });
    }

    internal async Task<List<PastDatedOrder>> FindAsync(DateOnly today, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(item => item.Status == StagingStatus.PendingReview && item.EntityType == "order")
            .Select(item => new { item.Id, item.PayloadJson, item.ReceivedAtUtc })
            .ToListAsync(ct);
        var result = new List<PastDatedOrder>();
        foreach (var row in rows)
        {
            var dates = StagingQueueProjection.PlanningDates(row.PayloadJson);
            if (dates.Count == 0) continue;
            var latest = dates.Max();
            if (latest >= today) continue;
            result.Add(new PastDatedOrder(row.Id, Field(row.PayloadJson, "customerCode", "sellerName"), Field(row.PayloadJson, "poNumber", "customerPo", "customerRef", "transportPo"), latest, row.ReceivedAtUtc, Field(row.PayloadJson, "sourceSubject", "sourceEmailSubject")));
        }
        return result.OrderBy(item => item.LatestDate).ThenBy(item => item.Customer).ToList();
    }

    private static DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);

    private static string? Field(string payloadJson, params string[] names)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var property in document.RootElement.EnumerateObject())
                if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                {
                    var text = property.Value.ToString().Trim();
                    if (text.Length > 0) return text.Length > 120 ? text[..120] : text;
                }
        }
        catch (JsonException) { }
        return null;
    }
}
