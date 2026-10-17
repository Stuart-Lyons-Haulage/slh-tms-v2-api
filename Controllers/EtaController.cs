using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/eta")]
[Authorize]
public sealed class EtaController(
    TmsDbContext db,
    LiveEtaCalculator liveEta,
    EtaAccuracyProcessor accuracy,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet("live")]
    public async Task<ActionResult<IReadOnlyList<LiveEtaSnapshot>>> Live([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var requestedDate = date ?? UkDate(timeProvider.GetUtcNow());
        return Ok(await liveEta.CalculateAsync(requestedDate, ct));
    }

    [HttpGet("accuracy-report")]
    public async Task<ActionResult<EtaAccuracyReport>> AccuracyReport(CancellationToken ct) =>
        Ok(await accuracy.ReportAsync(ct));

    [HttpGet("run/{runId:guid}/history")]
    public async Task<IActionResult> RunHistory(Guid runId, CancellationToken ct)
    {
        await ManagementReportingStore.EnsureSchemaAsync(db, ct);
        var snapshots = await db.EtaSnapshots.AsNoTracking()
            .Where(snapshot => snapshot.LoadId == runId)
            .OrderBy(snapshot => snapshot.CapturedAtUtc)
            .ToListAsync(ct);
        return Ok(snapshots);
    }

    private static DateOnly UkDate(DateTimeOffset utc)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
    }
}

[ApiController]
[Route("api/notifications")]
[Authorize]
public sealed class CustomerNotificationsController(CustomerNotificationService notifications) : ControllerBase
{
    [HttpPost("morning-briefing/preview")]
    public async Task<ActionResult<MorningBriefingPreview>> MorningBriefingPreview([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await notifications.PreviewMorningBriefingAsync(date, ct));

    [HttpGet("log")]
    public async Task<IActionResult> NotificationLog(
        [FromQuery] Guid customerId,
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        CancellationToken ct)
    {
        if (to < from) return BadRequest(new { error = "The 'to' date must be on or after the 'from' date." });
        return Ok(await notifications.ReadLogAsync(customerId, from, to, ct));
    }
}
