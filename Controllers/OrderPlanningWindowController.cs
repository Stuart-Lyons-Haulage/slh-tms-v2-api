using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Authorize]
public sealed class OrderPlanningWindowController(TmsDbContext db) : ControllerBase
{
    [HttpPost("api/v1/staging/orders/refresh-planning-windows")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RefreshPending([FromQuery] int take = 500, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 2000);
        var items = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview)
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Take(take)
            .ToListAsync(ct);

        var actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value ?? "system";
        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var failures = new List<object>();

        foreach (var item in items)
        {
            try
            {
                using var before = JsonDocument.Parse(item.PayloadJson);
                var enriched = OrderPlanningWindowClassifier.Enrich(before.RootElement);
                var nextPayload = enriched.GetRawText();
                if (string.Equals(item.PayloadJson, nextPayload, StringComparison.Ordinal))
                {
                    skipped++;
                    continue;
                }

                item.PayloadJson = nextPayload;
                item.ReviewNote = MergeNote(item.ReviewNote, "Planning window refreshed from PM/overnight rules.");
                db.StagedImportEvents.Add(StagingAudit.Create(item, "PlanningWindowClassified", item.Status, item.ReviewNote, actor));
                updated++;
            }
            catch (JsonException ex)
            {
                failed++;
                failures.Add(new { item.Id, reason = ex.Message });
            }
        }

        await db.SaveChangesAsync(ct);
        return Ok(new { scanned = items.Count, updated, skipped, failed, failures });
    }

    [HttpPost("api/v1/staging/orders/classify-planning-window")]
    [Authorize(Policy = "TmsWrite")]
    public IActionResult Classify([FromBody] JsonElement payload)
    {
        var classification = OrderPlanningWindowClassifier.Classify(payload);
        var enriched = OrderPlanningWindowClassifier.Enrich(payload);
        return Ok(new
        {
            classification.PlanningWindow,
            classification.RunsOvernight,
            classification.SuggestedRouteType,
            classification.Confidence,
            classification.Reason,
            classification.RequiresPlannerReview,
            payload = enriched
        });
    }

    private static string MergeNote(string? current, string note)
    {
        if (string.IsNullOrWhiteSpace(current)) return note;
        if (current.Contains(note, StringComparison.OrdinalIgnoreCase)) return current;
        return current.Length + note.Length + 3 > 1000 ? current : $"{current} | {note}";
    }
}
