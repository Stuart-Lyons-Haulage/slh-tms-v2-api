using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/intake-health")]
[Authorize]
public sealed class IntakeHealthController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateTimeOffset? fromUtc, CancellationToken ct)
    {
        var from = fromUtc ?? DateTimeOffset.UtcNow.AddDays(-7);
        if (from > DateTimeOffset.UtcNow.AddMinutes(5))
            return BadRequest(new { error = "fromUtc_must_not_be_in_the_future" });

        var emailEvidence = db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "email-evidence" && item.ReceivedAtUtc >= from);
        var orderIntake = db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "order" && item.ReceivedAtUtc >= from && item.Source != null && item.Source.Contains("Info mailbox"));

        var evidenceEmails = await emailEvidence.CountAsync(ct);
        var orderRecords = await orderIntake.CountAsync(ct);
        var pendingReview = await orderIntake.CountAsync(item => item.Status == StagingStatus.PendingReview, ct);
        var promoted = await orderIntake.CountAsync(item => item.Status == StagingStatus.Promoted || item.Status == StagingStatus.Approved, ct);
        var rejected = await orderIntake.CountAsync(item => item.Status == StagingStatus.Rejected || item.Status == StagingStatus.Archived, ct);
        var failed = await orderIntake.CountAsync(item => item.Status == StagingStatus.Failed, ct);
        var mappingExceptions = await orderIntake.CountAsync(item => item.PayloadJson.Contains("MappingException"), ct);
        var fastPathOrders = await orderIntake.CountAsync(item => item.PayloadJson.Contains("sender-route-fast-path"), ct);

        var lastEmailReceivedUtc = await emailEvidence
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Select(item => (DateTimeOffset?)item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        var lastOrderStagedUtc = await orderIntake
            .OrderByDescending(item => item.ReceivedAtUtc)
            .Select(item => (DateTimeOffset?)item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);
        var lastPromotedUtc = await orderIntake
            .Where(item => item.Status == StagingStatus.Promoted || item.Status == StagingStatus.Approved)
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Select(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);

        var warnings = new List<string>();
        if (evidenceEmails > 0 && orderRecords == 0)
            warnings.Add("Mailbox evidence has been received but no transport orders have been staged in this seven day window.");
        if (failed > 0)
            warnings.Add($"{failed} mailbox order record(s) are in Failed status.");
        if (mappingExceptions > 0)
            warnings.Add($"{mappingExceptions} mailbox order record(s) need sender/site mapping review.");
        if (lastEmailReceivedUtc is not null && lastOrderStagedUtc is not null && lastEmailReceivedUtc > lastOrderStagedUtc.Value.AddMinutes(15))
            warnings.Add("The newest retained mailbox email is more than 15 minutes newer than the newest staged order; check the intake flow and mapping exceptions.");

        return Ok(new
        {
            fromUtc = from,
            generatedAtUtc = DateTimeOffset.UtcNow,
            evidenceEmails,
            orderRecords,
            pendingReview,
            promoted,
            rejected,
            failed,
            mappingExceptions,
            fastPathOrders,
            lastEmailReceivedUtc,
            lastOrderStagedUtc,
            lastPromotedUtc,
            healthy = warnings.Count == 0,
            warnings,
            note = "Evidence emails count messages retained by the TMS intake path over the last seven days by default. Unrelated mail deliberately ignored before evidence retention is not counted here."
        });
    }
}
