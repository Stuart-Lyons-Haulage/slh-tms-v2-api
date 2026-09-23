using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Sanitised production probe for the Outlook/Power Automate -> SQL staging bridge.
/// It deliberately exposes only aggregate counts and timestamps: no sender, subject,
/// message identifiers, attachment names or order payload data leave the protected API.
/// </summary>
[ApiController]
[Route("api/v1/health/intake")]
[AllowAnonymous]
public sealed class IntakePipelineHealthController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-24);
        var evidence = db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "email-evidence" && item.ReceivedAtUtc >= from);
        var orders = db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order"
                && item.ReceivedAtUtc >= from
                && item.Source != null
                && item.Source.Contains("Info mailbox"));

        var evidenceEmails = await evidence.CountAsync(ct);
        var orderRecords = await orders.CountAsync(ct);
        var pendingReview = await orders.CountAsync(item => item.Status == StagingStatus.PendingReview, ct);
        var failed = await orders.CountAsync(item => item.Status == StagingStatus.Failed, ct);
        var lastEmailReceivedUtc = await evidence
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Select(item => (DateTimeOffset?)item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        var lastOrderStagedUtc = await orders
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Select(item => (DateTimeOffset?)item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);

        double? evidenceToOrderGapMinutes = lastEmailReceivedUtc is not null && lastOrderStagedUtc is not null
            ? Math.Round((lastEmailReceivedUtc.Value - lastOrderStagedUtc.Value).TotalMinutes, 1)
            : null;
        var newestEvidenceHasNoRecentOrder = lastEmailReceivedUtc is not null
            && (lastOrderStagedUtc is null || lastEmailReceivedUtc.Value > lastOrderStagedUtc.Value.AddMinutes(15));
        var status = evidenceEmails == 0 || newestEvidenceHasNoRecentOrder || failed > 0 ? "attention" : "healthy";

        return Ok(new
        {
            status,
            windowHours = 24,
            evidenceEmails,
            orderRecords,
            pendingReview,
            failed,
            lastEmailReceivedUtc,
            lastOrderStagedUtc,
            evidenceToOrderGapMinutes,
            checkedAtUtc = now
        });
    }
}
