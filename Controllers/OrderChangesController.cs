using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>Amendments and cancellations still to be actioned on a run or a live
/// Samsara route, and the record of who actioned them.</summary>
[ApiController]
[Route("api/v1/order-changes")]
[Authorize]
public sealed class OrderChangesController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool open = true, [FromQuery] int days = 7, CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 60));
        var query = db.StagedImports.AsNoTracking().Where(x => x.EntityType == OrderChangeImpact.EntityType && x.ReceivedAtUtc >= since);
        if (open) query = query.Where(x => x.Status == StagingStatus.PendingReview);
        var rows = await query.OrderByDescending(x => x.ReceivedAtUtc).Take(200).ToListAsync(ct);
        return Ok(rows.Select(row => new
        {
            id = row.Id,
            open = row.Status == StagingStatus.PendingReview,
            raisedAtUtc = row.ReceivedAtUtc,
            actionedBy = row.ReviewedBy,
            actionedAtUtc = row.ReviewedAtUtc,
            actionNote = row.ReviewNote,
            change = JsonDocument.Parse(row.PayloadJson).RootElement
        }));
    }

    [HttpPost("{id:guid}/done"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Done(Guid id, [FromBody] OrderChangeDoneRequest? request, CancellationToken ct)
    {
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.Id == id && x.EntityType == OrderChangeImpact.EntityType, ct);
        if (row is null) return NotFound();
        if (row.Status != StagingStatus.PendingReview) return Ok(new { id, status = row.Status.ToString() });
        var user = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "planner";
        var previous = row.Status;
        row.Status = StagingStatus.Approved;
        row.ReviewedBy = user;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewNote = string.IsNullOrWhiteSpace(request?.Note) ? "Actioned" : request!.Note!.Trim();
        db.StagedImportEvents.Add(StagingAudit.Create(row, "ChangeActioned", previous, row.ReviewNote, user));
        await db.SaveChangesAsync(ct);
        return Ok(new { id, status = row.Status.ToString(), actionedBy = user });
    }
}

public sealed record OrderChangeDoneRequest(string? Note);
