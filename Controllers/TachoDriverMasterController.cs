using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/driver-master")]
[Authorize]
public sealed class TachoDriverMasterController(
    TmsDbContext db,
    TachoDriverMasterSyncJobService jobs,
    TachoDriverMasterSyncService sync,
    TachoDriverHoursRefreshService hoursRefresh) : ControllerBase
{
    [HttpPost("tachomaster/sync")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Sync(CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "TMS user";
        var recovered = await jobs.RecoverInterruptedAsync($"manual:{actor}", ct);
        var job = await jobs.EnqueueAsync(actor, ct);
        var response = new
        {
            job.JobId,
            job.Status,
            job.RequestedAtUtc,
            job.StartedAtUtc,
            job.CompletedAtUtc,
            Message = recovered > 0
                ? $"Recovered {recovered} stale TachoMaster Driver Master sync job(s) and queued a fresh reconcile."
                : job.Message,
            job.Result,
            recoveredStaleJobs = recovered
        };
        return AcceptedAtAction(nameof(SyncStatus), new { jobId = job.JobId }, response);
    }

    [HttpGet("tachomaster/sync/{jobId:guid}")]
    public async Task<IActionResult> SyncStatus(Guid jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct);
        return job is null ? NotFound() : Ok(job);
    }

    [HttpGet("tachomaster/quality")]
    public async Task<IActionResult> Quality(CancellationToken ct)
    {
        var quality = await sync.QualityAsync(ct);
        var latestCompletedCanonicalJob = await db.StagedImports.AsNoTracking()
            .Where(item =>
                item.Status == StagingStatus.Promoted &&
                (item.EntityType == "tachodrivermastersync" ||
                 item.EntityType == "tachodrivermasterorchestration" ||
                 item.EntityType == TachoDriverMasterSyncJobService.EntityType))
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Select(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);

        var latestCanonicalSyncUtc = Latest(quality.LatestCanonicalSyncUtc, latestCompletedCanonicalJob);
        return Ok(new TachoDriverMasterQuality(
            quality.ActiveDrivers,
            quality.ActiveWithMember,
            quality.ActiveWithCard,
            quality.DuplicateMemberGroups,
            quality.DuplicateCardGroups,
            quality.ActiveWithoutMember,
            quality.ActiveWithoutCard,
            latestCanonicalSyncUtc)
        {
            DuplicateMembers = quality.DuplicateMembers,
            DuplicateCards = quality.DuplicateCards
        });
    }

    [HttpGet("{driverId:guid}/tachomaster-profile")]
    public async Task<IActionResult> Profile(Guid driverId, CancellationToken ct)
    {
        var profile = await sync.ProfileAsync(driverId, ct);
        return profile is null ? NotFound() : Ok(profile);
    }

    [HttpPost("{driverId:guid}/refresh-tacho")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> RefreshTacho(Guid driverId, CancellationToken ct)
    {
        var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "TMS user";
        var result = await hoursRefresh.RefreshDriverHoursOnlyAsync(driverId, actor, ct);

        return result.Status switch
        {
            "updated" => Ok(result),
            "not_found" => NotFound(result),
            "inactive" or "missing_tacho_identity" or "not_configured" => BadRequest(result),
            "lease_busy" or "lease_lost" => StatusCode(StatusCodes.Status409Conflict, result),
            "profile_not_found" => StatusCode(StatusCodes.Status502BadGateway, result),
            _ => StatusCode(StatusCodes.Status500InternalServerError, result)
        };
    }

    private static DateTimeOffset? Latest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left >= right ? left : right;
    }
}
