using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/driver-master")]
[AllowAnonymous]
public sealed class DriverMasterHealthController(TmsDbContext db, TachoDriverMasterSyncService sync) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var quality = await sync.QualityAsync(ct);
        var latestPayload = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "tachodrivermastersync" || item.EntityType == "tachodrivermasterorchestration")
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Select(item => item.PayloadJson)
            .FirstOrDefaultAsync(ct);

        int? sourceWorkers = null;
        if (!string.IsNullOrWhiteSpace(latestPayload))
        {
            try
            {
                using var document = JsonDocument.Parse(latestPayload);
                if (document.RootElement.TryGetProperty("sourceWorkers", out var sourceElement) && sourceElement.TryGetInt32(out var sourceCount))
                    sourceWorkers = sourceCount;
            }
            catch (JsonException)
            {
                // Historic/provider population is informational only. Driver Master is authoritative.
            }
        }

        var legacyPendingReviewCount = await db.StagedImports.AsNoTracking()
            .CountAsync(row => row.EntityType == "driverreview" && row.Status == StagingStatus.PendingReview, ct);

        var duplicateIdentityProblem = quality.DuplicateMemberGroups > 0 || quality.DuplicateCardGroups > 0;
        var reviewRequired = quality.ActiveWithoutMember;
        var cardWarnings = Math.Max(0, quality.ActiveWithMember - quality.ActiveWithCard);
        var status = duplicateIdentityProblem
            ? "attention"
            : reviewRequired > 0 || cardWarnings > 0 || legacyPendingReviewCount > 0
                ? "review"
                : "healthy";

        return Ok(new
        {
            status,
            operationalAuthority = "Driver Master",
            employmentAuthority = "Sage HR",
            tachoRole = "Identity, card, duty and hours enrichment",
            quality.ActiveDrivers,
            quality.ActiveWithMember,
            quality.ActiveWithCard,
            reviewRequiredDrivers = reviewRequired,
            cardWarningDrivers = cardWarnings,
            quality.DuplicateMemberGroups,
            quality.DuplicateCardGroups,
            quality.LatestCanonicalSyncUtc,
            sourceWorkers,
            populationAligned = sourceWorkers is > 0 ? quality.ActiveDrivers == sourceWorkers.Value : (bool?)null,
            populationAlignmentIsInformational = true,
            legacyPendingTachoReviewDrivers = legacyPendingReviewCount,
            message = duplicateIdentityProblem
                ? "Duplicate Tacho identity evidence needs attention. Driver Master rows remain live until deliberately amended."
                : reviewRequired > 0
                    ? $"{reviewRequired} active Driver Master driver(s) need a Tacho member/DB number match. They remain live and visible for review."
                    : cardWarnings > 0
                        ? $"{cardWarnings} Tacho-linked driver(s) do not currently have card evidence."
                        : "Driver Master identity health is clear."
        });
    }
}
