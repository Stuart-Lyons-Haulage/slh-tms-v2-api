using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Read-only parity probe between the durable imported plan and the resilient operational
/// run source used by both Operations wallboards. This lets production verification prove
/// that the board contains the whole imported plan rather than merely proving one row exists.
/// </summary>
[ApiController, Route("api/v1/operations/wallboard-source-health")]
[Authorize]
public sealed class WallboardSourceHealthController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        if (!pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var imported = (await PlannerPlanAuditProjection.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();
        var wallboard = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();

        var wallboardReferences = wallboard.Select(load => load.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = imported
            .Where(load => !wallboardReferences.Contains(load.Reference))
            .Select(load => load.Reference)
            .OrderBy(reference => reference)
            .ToList();

        return Ok(new
        {
            planningDate,
            checkedAtUtc = DateTimeOffset.UtcNow,
            importedPlanRuns = imported.Count,
            wallboardRuns = wallboard.Count,
            allImportedRunsVisible = missing.Count == 0 && wallboard.Count >= imported.Count,
            missingImportedRuns = missing
        });
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
