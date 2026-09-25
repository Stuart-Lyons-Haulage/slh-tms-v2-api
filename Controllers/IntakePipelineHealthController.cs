using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Sanitised production probe for the Outlook/Power Automate -> SQL staging bridge.
/// It deliberately exposes only aggregate counts and timestamps: no sender, subject,
/// message identifiers, attachment names or order payload data leave the protected API.
/// </summary>
[ApiController]
[Route("api/v1/health/intake")]
public sealed class IntakePipelineHealthController(
    TmsDbContext db,
    InfoMailboxGraphOptions graphOptions,
    InfoMailboxGraphHealthState graphHealth,
    InfoMailboxGraphPollingService graphPoller) : ControllerBase
{
    [HttpGet, AllowAnonymous]
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
        // Not every retained email should create an order. The new verified-format
        // lane deliberately keeps unrelated Info-mailbox traffic as evidence only, so
        // an evidence-to-order gap is informational rather than a pipeline failure.
        var newestEvidenceHasNoRecentOrder = lastEmailReceivedUtc is not null
            && (lastOrderStagedUtc is null || lastEmailReceivedUtc.Value > lastOrderStagedUtc.Value.AddMinutes(15));

        var graphPollStaleAfter = TimeSpan.FromSeconds(Math.Max(300, Math.Clamp(graphOptions.PollIntervalSeconds, 30, 3600) * 3));
        var graphPollStale = graphOptions.Enabled &&
            (graphHealth.LastSuccessUtc is null || now - graphHealth.LastSuccessUtc.Value > graphPollStaleAfter);
        var graphMisconfigured = graphOptions.Enabled && !graphOptions.IsConfigured;

        var status = graphOptions.Enabled
            ? graphMisconfigured || graphPollStale || graphHealth.LastError is not null || failed > 0 ? "attention" : "healthy"
            : evidenceEmails == 0 || failed > 0 ? "attention" : "healthy";

        return Ok(new
        {
            status,
            source = graphOptions.Enabled ? "Microsoft Graph local poller" : "External mailbox bridge",
            windowHours = 24,
            evidenceEmails,
            orderRecords,
            pendingReview,
            failed,
            lastEmailReceivedUtc,
            lastOrderStagedUtc,
            evidenceToOrderGapMinutes,
            newestEvidenceHasNoRecentOrder,
            graph = new
            {
                enabled = graphOptions.Enabled,
                configured = graphOptions.IsConfigured,
                mailbox = graphOptions.Mailbox,
                graphHealth.LastAttemptUtc,
                graphHealth.LastSuccessUtc,
                graphHealth.LastError,
                graphHealth.LastMessagesSeen,
                graphHealth.LastMessagesIngested,
                stale = graphPollStale
            },
            checkedAtUtc = now
        });
    }

    [HttpPost("poll"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> PollNow(CancellationToken ct)
    {
        if (!graphOptions.Enabled || !graphOptions.IsConfigured)
            return BadRequest(new { message = "Microsoft Graph mailbox polling is not fully configured." });

        await graphPoller.PollOnceAsync(ct);
        return Ok(new
        {
            message = "Graph mailbox poll completed; Order Review is being refreshed from the canonical staging queue.",
            graphHealth.LastAttemptUtc,
            graphHealth.LastSuccessUtc,
            graphHealth.LastMessagesSeen,
            graphHealth.LastMessagesIngested
        });
    }
}
