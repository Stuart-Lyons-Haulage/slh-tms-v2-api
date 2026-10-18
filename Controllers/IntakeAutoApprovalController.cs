using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Per-format auto-approval setting with the evidence to decide it: for each recurring
/// booking format seen recently, how many orders would have approved themselves and
/// whether the planners then approved those unchanged.
/// </summary>
[ApiController]
[Route("api/v1/order-intake/auto-approval")]
[Authorize]
public sealed class IntakeAutoApprovalController(TmsDbContext db) : ControllerBase
{
    [HttpGet, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Get([FromQuery] int days = 14, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 60);
        var since = DateTimeOffset.UtcNow.AddDays(-days);
        var modes = await IntakeAutoApproval.LoadModesAsync(db, ct);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.ReceivedAtUtc >= since && x.PayloadJson.Contains("\"autoApproval\""))
            .Select(x => new { x.Id, x.Status, x.ReviewedBy, x.PayloadJson })
            .ToListAsync(ct);
        var ids = rows.Select(x => x.Id).ToList();
        var amended = (await db.StagedImportEvents.AsNoTracking()
                .Where(x => ids.Contains(x.StagedImportId) && x.EventType == "Amended")
                .Select(x => x.StagedImportId)
                .ToListAsync(ct))
            .ToHashSet();

        var formats = new Dictionary<string, FormatStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var payload = document.RootElement;
                if (!payload.TryGetProperty("autoApproval", out var decision)) continue;
                var key = decision.TryGetProperty("formatKey", out var keyValue) && keyValue.ValueKind == JsonValueKind.String ? keyValue.GetString() : null;
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!formats.TryGetValue(key, out var stats))
                    formats[key] = stats = new FormatStats(key, Text(payload, "customerCode"), Text(payload, "sourceSender"));
                stats.Orders++;
                var eligible = decision.TryGetProperty("eligible", out var e) && e.ValueKind == JsonValueKind.True;
                if (eligible) stats.WouldApprove++;
                if (row.ReviewedBy == IntakeAutoApproval.Actor) stats.AutoApproved++;
                else if (eligible && row.Status == StagingStatus.Promoted)
                {
                    if (amended.Contains(row.Id)) stats.EligibleButEdited++;
                    else stats.EligibleApprovedUnchanged++;
                }
                else if (eligible && row.Status == StagingStatus.Rejected) stats.EligibleButRejected++;
            }
            catch (JsonException) { }
        }

        return Ok(new
        {
            days,
            formats = formats.Values
                .OrderByDescending(x => x.Orders)
                .Select(x => new
                {
                    x.FormatKey,
                    mode = IntakeAutoApproval.ModeFor(modes, x.FormatKey),
                    x.Customer,
                    x.Sender,
                    x.Orders,
                    x.WouldApprove,
                    x.EligibleApprovedUnchanged,
                    x.EligibleButEdited,
                    x.EligibleButRejected,
                    x.AutoApproved
                })
        });
    }

    [HttpPut, Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Set([FromBody] AutoApprovalModeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.FormatKey)) return BadRequest(new { message = "formatKey is required." });
        var user = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value;
        await IntakeAutoApproval.SaveModeAsync(db, request.FormatKey.Trim(), request.Mode, user, ct);
        return Ok(new { formatKey = request.FormatKey.Trim(), mode = IntakeAutoApproval.NormaliseMode(request.Mode) });
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed class FormatStats(string formatKey, string? customer, string? sender)
    {
        public string FormatKey { get; } = formatKey;
        public string? Customer { get; } = customer;
        public string? Sender { get; } = sender;
        public int Orders { get; set; }
        public int WouldApprove { get; set; }
        public int EligibleApprovedUnchanged { get; set; }
        public int EligibleButEdited { get; set; }
        public int EligibleButRejected { get; set; }
        public int AutoApproved { get; set; }
    }
}

public sealed record AutoApprovalModeRequest(string FormatKey, string Mode);
