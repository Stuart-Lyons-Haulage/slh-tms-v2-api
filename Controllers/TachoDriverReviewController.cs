using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Exposes the queue of new TachoMaster workers that have no matching Driver Master record.
/// These are created by TachoMemberCodeDriverMasterSync when an unrecognised member code
/// arrives from TachoMaster. A reviewer must explicitly promote or reject each entry —
/// no driver is created automatically from an unrecognised Tacho identity.
/// </summary>
[ApiController]
[Route("api/v1/driver-master/tacho-review")]
[Authorize]
public sealed class TachoDriverReviewController(
    TmsDbContext db,
    ILogger<TachoDriverReviewController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Returns all pending TachoMaster driver review items.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == "driverreview" && row.Status == StagingStatus.PendingReview)
            .OrderBy(row => row.ReceivedAtUtc)
            .ToListAsync(ct);

        var items = rows.Select(row =>
        {
            TachoDriverReviewPayload? payload = null;
            try { payload = JsonSerializer.Deserialize<TachoDriverReviewPayload>(row.PayloadJson, JsonOptions); }
            catch (JsonException) { }

            return new
            {
                id = row.Id,
                idempotencyKey = row.IdempotencyKey,
                receivedAtUtc = row.ReceivedAtUtc,
                reviewNote = row.ReviewNote,
                tachoMemberCode = payload?.TachoMemberCode,
                displayName = payload?.DisplayName,
                cardNumber = payload?.CardNumber,
                employeeNumber = payload?.EmployeeNumber,
                workerType = payload?.WorkerType,
                agencyName = payload?.AgencyName,
                cardLastRead = payload?.CardLastRead,
                driverCardExpiry = payload?.DriverCardExpiry,
                drivingLicenceExpiry = payload?.DrivingLicenceExpiry,
                cpcExpiry = payload?.CpcExpiry
            };
        }).ToList();

        return Ok(new
        {
            count = items.Count,
            items
        });
    }

    /// <summary>
    /// Promotes a pending review item to a new active Driver Master record.
    /// The reviewer may supply additional CRM fields (mobile, driver type, group)
    /// that are not available from TachoMaster.
    /// </summary>
    [HttpPost("{id:guid}/promote")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Promote(Guid id, [FromBody] TachoDriverPromoteRequest request, CancellationToken ct)
    {
        var row = await db.StagedImports
            .SingleOrDefaultAsync(r => r.Id == id && r.EntityType == "driverreview" && r.Status == StagingStatus.PendingReview, ct);
        if (row is null) return NotFound(new { error = "Review item not found or already actioned." });

        TachoDriverReviewPayload? payload;
        try { payload = JsonSerializer.Deserialize<TachoDriverReviewPayload>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return UnprocessableEntity(new { error = "Review item payload could not be parsed." }); }
        if (payload is null) return UnprocessableEntity(new { error = "Review item payload was empty." });

        // Guard: do not promote if a driver with this member code already exists
        var existingByMember = await db.Drivers
            .AnyAsync(d => d.TachoMasterDriverId == payload.TachoMemberCode, ct);
        if (existingByMember)
        {
            row.Status = StagingStatus.Archived;
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            row.ReviewedBy = Actor();
            row.ReviewNote = $"Auto-archived: a Driver Master record with Member Code {payload.TachoMemberCode} now exists. No duplicate was created.";
            await db.SaveChangesAsync(ct);
            return Conflict(new { error = $"A driver with TachoMaster Member Code {payload.TachoMemberCode} already exists. The review item has been archived." });
        }

        var now = DateTimeOffset.UtcNow;
        var actor = Actor();
        var employeeNumber = await UniqueEmployeeNumberAsync(payload, ct);

        var driver = new Driver
        {
            EmployeeNumber = employeeNumber,
            DisplayName = Clean(request.DisplayName) ?? payload.DisplayName,
            TachoName = payload.DisplayName,
            TachoMasterDriverId = payload.TachoMemberCode,
            TachoCardNumber = Clean(payload.CardNumber),
            DriverType = Clean(request.DriverType) ?? Clean(payload.WorkerType),
            DriverGroup = Clean(request.DriverGroup),
            AgencyName = Clean(payload.AgencyName),
            MobileNumber = Clean(request.MobileNumber),
            Active = true,
            LastTachoSyncUtc = now
        };

        db.Drivers.Add(driver);

        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = $"Promoted to Driver Master as {driver.DisplayName} ({employeeNumber}) by {actor}.";

        db.MasterDataAudits.Add(new MasterDataAudit
        {
            EntityType = "Driver",
            EntityId = driver.Id,
            Action = "PromotedFromTachoReview",
            ChangedBy = actor,
            ChangesJson = JsonSerializer.Serialize(new
            {
                reviewItemId = id,
                tachoMemberCode = payload.TachoMemberCode,
                displayName = driver.DisplayName,
                employeeNumber,
                source = "TachoMaster driver review queue — promoted by authorised reviewer"
            }, JsonOptions)
        });

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "TachoMaster driver review item {ReviewId} promoted: {DisplayName} (Member {MemberCode}) → Driver {DriverId} by {Actor}.",
            id, driver.DisplayName, payload.TachoMemberCode, driver.Id, actor);

        return Ok(new
        {
            driverId = driver.Id,
            employeeNumber = driver.EmployeeNumber,
            displayName = driver.DisplayName,
            tachoMemberCode = driver.TachoMasterDriverId,
            message = $"Driver {driver.DisplayName} ({employeeNumber}) created and activated in Driver Master."
        });
    }

    /// <summary>
    /// Rejects a pending review item. The TachoMaster member will be ignored on future syncs
    /// until the review item is re-opened or the member's data changes enough to generate a new one.
    /// </summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] TachoDriverRejectRequest request, CancellationToken ct)
    {
        var row = await db.StagedImports
            .SingleOrDefaultAsync(r => r.Id == id && r.EntityType == "driverreview" && r.Status == StagingStatus.PendingReview, ct);
        if (row is null) return NotFound(new { error = "Review item not found or already actioned." });

        var actor = Actor();
        row.Status = StagingStatus.Rejected;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = actor;
        row.ReviewNote = string.IsNullOrWhiteSpace(request.Reason)
            ? $"Rejected by {actor}."
            : $"Rejected by {actor}: {request.Reason.Trim()}";

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "TachoMaster driver review item {ReviewId} rejected by {Actor}: {Reason}.",
            id, actor, request.Reason);

        return Ok(new { rejected = true, reason = row.ReviewNote });
    }

    /// <summary>
    /// Returns a count of pending review items — lightweight endpoint for the dashboard badge.
    /// </summary>
    [HttpGet("count")]
    public async Task<IActionResult> Count(CancellationToken ct)
    {
        var count = await db.StagedImports.AsNoTracking()
            .CountAsync(row => row.EntityType == "driverreview" && row.Status == StagingStatus.PendingReview, ct);
        return Ok(new { pendingCount = count });
    }

    private async Task<string> UniqueEmployeeNumberAsync(TachoDriverReviewPayload payload, CancellationToken ct)
    {
        var used = await db.Drivers.Select(d => d.EmployeeNumber).ToHashSetAsync(StringComparer.OrdinalIgnoreCase, ct);
        var preferred = Clean(payload.EmployeeNumber);
        if (!string.IsNullOrWhiteSpace(preferred) && !used.Contains(preferred))
            return preferred[..Math.Min(preferred.Length, 40)];
        var root = $"TM-{payload.TachoMemberCode}";
        if (!used.Contains(root)) return root;
        for (var i = 2; i < 1000; i++)
        {
            var candidate = $"{root}-{i}";
            if (!used.Contains(candidate)) return candidate;
        }
        return $"TM-{Guid.NewGuid():N}"[..40];
    }

    private string Actor() =>
        User.FindFirst("preferred_username")?.Value
        ?? User.Identity?.Name
        ?? "TMS reviewer";

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record TachoDriverReviewPayload(
    string TachoMemberCode,
    string DisplayName,
    string? CardNumber,
    string? EmployeeNumber,
    string? WorkerType,
    string? AgencyName,
    string? CardLastRead,
    string? DriverCardExpiry,
    string? DrivingLicenceExpiry,
    string? CpcExpiry);

public sealed record TachoDriverPromoteRequest(
    string? DisplayName,
    string? MobileNumber,
    string? DriverType,
    string? DriverGroup);

public sealed record TachoDriverRejectRequest(string? Reason);
