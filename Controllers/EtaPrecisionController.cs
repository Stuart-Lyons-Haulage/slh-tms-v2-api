using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/management")]
[Authorize]
public sealed class EtaPrecisionController(TmsDbContext db) : ControllerBase
{
    [HttpPost("eta-snapshots/capture"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Capture([FromBody] List<EtaSnapshotCaptureItem> items, CancellationToken ct)
    {
        if (items.Count > 2000) return BadRequest("A maximum of 2,000 ETA samples can be captured per request.");
        var added = await ManagementReportingStore.CaptureAsync(db, items, ct);
        return Ok(new { received = items.Count, added, capturedAtUtc = DateTimeOffset.UtcNow });
    }

    [HttpGet("eta-precision")]
    public async Task<IActionResult> Precision([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        var last = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var first = from ?? last.AddDays(-6);
        if (first > last) return BadRequest("'from' must be on or before 'to'.");
        if (last.DayNumber - first.DayNumber > 366) return BadRequest("ETA precision reports are limited to 366 days per request.");

        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
        await ManagementReportingStore.EnsureSchemaAsync(db, ct);

        var loadIds = await db.Loads.AsNoTracking()
            .Where(x => x.PlanningDate >= first && x.PlanningDate <= last && x.Status != LoadStatus.Cancelled)
            .Select(x => x.Id)
            .Take(5000)
            .ToListAsync(ct);
        if (loadIds.Count == 0) return Ok(Empty(first, last, "No runs are available in the selected period."));

        var actuals = await db.GeofenceVisits.AsNoTracking()
            .Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value) && x.LoadStopId != null && x.ConfirmedAtUtc != null)
            .Select(x => new { StopId = x.LoadStopId!.Value, ActualUtc = x.ConfirmedAtUtc!.Value })
            .Take(20000)
            .ToListAsync(ct);
        if (actuals.Count == 0) return Ok(Empty(first, last, "ETA samples are being collected; confirmed geofence arrivals are needed before precision can be scored."));

        var stopIds = actuals.Select(x => x.StopId).Distinct().ToList();
        var snapshots = await db.EtaSnapshots.AsNoTracking()
            .Where(x => stopIds.Contains(x.StopId) && x.Source == "Live" && x.EtaUtc != null)
            .OrderBy(x => x.StopId).ThenBy(x => x.CapturedAtUtc)
            .Take(50000)
            .ToListAsync(ct);
        var byStop = snapshots.GroupBy(x => x.StopId).ToDictionary(x => x.Key, x => x.ToList());
        var errors = new List<double>();

        foreach (var actual in actuals)
        {
            if (!byStop.TryGetValue(actual.StopId, out var candidates)) continue;
            var snapshot = candidates.Where(x => x.CapturedAtUtc <= actual.ActualUtc).OrderByDescending(x => x.CapturedAtUtc).FirstOrDefault();
            if (snapshot?.EtaUtc is null) continue;
            errors.Add(Math.Abs((snapshot.EtaUtc.Value - actual.ActualUtc).TotalMinutes));
        }

        if (errors.Count == 0) return Ok(Empty(first, last, "Live ETA snapshots are now enabled. Precision will populate after captured ETAs have corresponding confirmed arrivals."));

        return Ok(new
        {
            from = first,
            to = last,
            dataAvailable = true,
            samples = errors.Count,
            within10MinutesPercent = Percent(errors.Count(x => x <= 10), errors.Count),
            within15MinutesPercent = Percent(errors.Count(x => x <= 15), errors.Count),
            within30MinutesPercent = Percent(errors.Count(x => x <= 30), errors.Count),
            meanAbsoluteErrorMinutes = Math.Round(errors.Average(), 1),
            message = $"Scored from {errors.Count} live ETA snapshot(s) against confirmed geofence arrival."
        });
    }

    private static object Empty(DateOnly first, DateOnly last, string message) => new
    {
        from = first,
        to = last,
        dataAvailable = false,
        samples = 0,
        within10MinutesPercent = (decimal?)null,
        within15MinutesPercent = (decimal?)null,
        within30MinutesPercent = (decimal?)null,
        meanAbsoluteErrorMinutes = (double?)null,
        message
    };

    private static decimal? Percent(int value, int total) => total <= 0 ? null : Math.Round((decimal)value / total * 100m, 1);
}