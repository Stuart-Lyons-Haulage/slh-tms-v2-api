using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-forecast"), Authorize]
public sealed class DriverForecastController(
    TmsDbContext db,
    SageHrClient sageHr,
    ILogger<DriverForecastController> logger) : ControllerBase
{
    private const string EntityType = "driverdemandforecast";

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 31)
            return BadRequest(new { message = "Choose a forecast range of no more than 32 days." });

        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted)
            .OrderBy(row => row.ReceivedAtUtc)
            .ToListAsync(ct);
        var byDate = rows.Select(Parse).Where(item => item is not null).Cast<DriverForecastInput>()
            .Where(item => item.Date >= from && item.Date <= to)
            .ToDictionary(item => item.Date);
        var result = new List<DriverForecastDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var staffing = await DriverAvailabilityService.ReadAsync(db, date, ct, sageHr, logger);
            byDate.TryGetValue(date, out var input);
            var required = (input?.DayRequired ?? 0) + (input?.NightRequired ?? 0);
            result.Add(new DriverForecastDay(date, input?.DayRequired ?? 0, input?.NightRequired ?? 0,
                input?.Notes, input?.OpsNotes, input?.AgencyRequested ?? 0, input?.AgencyConfirmed ?? 0,
                staffing.Summary.AvailableDrivers, staffing.Summary.EmployedAvailable,
                staffing.Summary.AgencyConfirmed, staffing.Summary.CasualConfirmed,
                required - staffing.Summary.AvailableDrivers));
        }
        return Ok(result);
    }

    [HttpPut("{date}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Put(DateOnly date, DriverForecastWriteRequest request, CancellationToken ct)
    {
        if (request.DayRequired < 0 || request.NightRequired < 0 || request.AgencyRequested < 0 || request.AgencyConfirmed < 0)
            return BadRequest(new { message = "Forecast counts cannot be negative." });
        var actor = User.Identity?.Name ?? "TMS planner";
        var key = $"{EntityType}:{date:yyyy-MM-dd}";
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.EntityType == EntityType && item.IdempotencyKey == key, ct);
        var payload = JsonSerializer.Serialize(new DriverForecastInput(date, request.DayRequired, request.NightRequired,
            Clean(request.Notes, 1000), Clean(request.OpsNotes, 1000), request.AgencyRequested, request.AgencyConfirmed));
        var now = DateTimeOffset.UtcNow;
        if (row is null)
        {
            row = new StagedImport { EntityType = EntityType, IdempotencyKey = key, PayloadJson = payload, Source = "Staffing forecast", Status = StagingStatus.Promoted, ReceivedAtUtc = now, ReviewedAtUtc = now, ReviewedBy = actor, ReviewNote = "Management forecast entered in TMS." };
            db.StagedImports.Add(row);
        }
        else
        {
            row.PayloadJson = payload; row.Status = StagingStatus.Promoted; row.ReviewedAtUtc = now; row.ReviewedBy = actor; row.ReviewNote = "Management forecast updated in TMS.";
        }
        await db.SaveChangesAsync(ct);
        db.OperationalHistoryEvents.Add(new OperationalHistoryEvent { EntityType = EntityType, EntityId = row.Id, EventType = "ForecastSaved", Actor = actor, PayloadJson = payload, OccurredAtUtc = now });
        await db.SaveChangesAsync(ct);
        return Ok(Parse(row));
    }

    private static DriverForecastInput? Parse(StagedImport row)
    {
        try { return JsonSerializer.Deserialize<DriverForecastInput>(row.PayloadJson); }
        catch (JsonException) { return null; }
    }

    private static string? Clean(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}

public sealed record DriverForecastWriteRequest(int DayRequired, int NightRequired, int AgencyRequested, int AgencyConfirmed, string? Notes, string? OpsNotes);
public sealed record DriverForecastDay(DateOnly Date, int DayRequired, int NightRequired, string? Notes, string? OpsNotes, int AgencyRequested, int AgencyConfirmed, int AvailableDrivers, int EmployedAvailable, int AgencyAvailable, int CasualAvailable, int Shortfall);
internal sealed record DriverForecastInput(DateOnly Date, int DayRequired, int NightRequired, string? Notes, string? OpsNotes, int AgencyRequested, int AgencyConfirmed);
