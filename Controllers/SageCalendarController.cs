using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>Sage HR "Sync with my calendar" link: save it, read it now, and see what it gave.</summary>
[ApiController]
[Route("api/v1/integrations/sage-calendar")]
[Authorize]
public sealed class SageCalendarController(TmsDbContext db, SageCalendarFeed feed) : ControllerBase
{
    public sealed record SaveRequest(string? Url);

    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var setting = await SageCalendarFeed.ReadSettingAsync(db, ct);
        var snapshot = await SageCalendarFeed.ReadSnapshotAsync(db, ct);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return Ok(new
        {
            linked = setting is not null,
            link = setting?.Masked,
            linkSavedBy = setting?.UpdatedBy,
            linkSavedAtUtc = setting?.UpdatedAtUtc,
            lastReadUtc = snapshot?.FetchedAtUtc,
            problem = snapshot?.Problem,
            events = snapshot?.Events ?? 0,
            matched = snapshot?.Matched ?? 0,
            unmatched = snapshot?.Unmatched ?? [],
            archivedDrivers = snapshot?.ArchivedDrivers ?? [],
            ignored = snapshot?.Ignored ?? 0,
            types = snapshot?.Types ?? new Dictionary<string, int>(),
            driversOffToday = snapshot?.Days.Where(day => day.Date == today).Select(day => new { day.DriverName, day.Kind, day.Type }) ?? [],
            leaveDays = snapshot?.Days.Count(day => day.Kind == "Leave") ?? 0,
            nonWorkingDays = snapshot?.Days.Count(day => day.Kind == "NonWorking") ?? 0
        });
    }

    [HttpPut, Authorize(Policy = "TmsOwner")]
    public async Task<IActionResult> Save(SaveRequest request, CancellationToken ct)
    {
        try { await feed.SaveUrlAsync(db, request.Url ?? string.Empty, User.Identity?.Name, ct); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
        var snapshot = await feed.SyncAsync(db, ct);
        return Ok(new { saved = true, snapshot.Events, snapshot.Matched, snapshot.Problem });
    }

    [HttpPost("refresh"), Authorize(Policy = "TmsOwner")]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        var snapshot = await feed.SyncAsync(db, ct);
        return Ok(new { snapshot.Events, snapshot.Matched, snapshot.Problem });
    }
}
