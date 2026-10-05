using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-availability"), Authorize]
public sealed class DriverAvailabilityController(TmsDbContext db, SageHrClient sageHr, ILogger<DriverAvailabilityController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<DriverAvailabilitySnapshot>> Get([FromQuery] DateOnly date, CancellationToken ct) =>
        Ok(await DriverAvailabilityService.ReadAsync(db, date, ct, sageHr, logger));

    [HttpGet("classification-review")]
    public async Task<IActionResult> ClassificationReview([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        var snapshot = await DriverAvailabilityService.ReadAsync(db, planningDate, ct, sageHr, logger);
        return Ok(new
        {
            planningDate,
            count = snapshot.ClassificationMismatchCount,
            sourceOfTruth = "Driver Master employment type",
            validation = "3-digit employee number plus latest successful SageHR driver-roster match",
            mismatches = snapshot.Drivers.Where(driver => driver.ClassificationMismatch).Select(driver => new
            {
                driver.DriverId,
                driver.EmployeeNumber,
                driver.DisplayName,
                driver.EmploymentType,
                reason = driver.ClassificationReviewReason
            })
        });
    }

    [HttpPost, Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Create(DriverAvailabilityWriteRequest request, CancellationToken ct)
    {
        var validation = await Validate(request, null, ct);
        if (validation is not null) return validation;
        var actor = User.Identity?.Name ?? "TMS planner";
        var window = new DriverAvailabilityWindow { Id = Guid.NewGuid(), DriverId = request.DriverId, CreatedBy = actor, UpdatedBy = actor };
        Apply(window, request, actor);
        db.DriverAvailabilityWindows.Add(window);
        await AddAudit(window, "DriverAvailabilityCreated", actor, ct);
        return CreatedAtAction(nameof(Get), new { date = DateOnly.FromDateTime(window.AvailableFromUtc.UtcDateTime) }, window);
    }

    [HttpPut("{id:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Update(Guid id, DriverAvailabilityWriteRequest request, CancellationToken ct)
    {
        var window = await db.DriverAvailabilityWindows.SingleOrDefaultAsync(item => item.Id == id, ct);
        if (window is null) return NotFound(new { message = "The availability record could not be found." });
        if (window.DriverId != request.DriverId) return BadRequest(new { message = "An availability record cannot be moved to a different driver." });
        var validation = await Validate(request, id, ct);
        if (validation is not null) return validation;
        var actor = User.Identity?.Name ?? "TMS planner";
        Apply(window, request, actor);
        await AddAudit(window, "DriverAvailabilityUpdated", actor, ct);
        return Ok(window);
    }

    private async Task<IActionResult?> Validate(DriverAvailabilityWriteRequest request, Guid? id, CancellationToken ct)
    {
        if (request.AvailableUntilUtc <= request.AvailableFromUtc) return BadRequest(new { message = "Available until must be after available from." });
        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.DriverId && item.Active, ct);
        if (driver is null) return NotFound(new { message = "The active Driver Master record could not be found." });
        await MasterDetailStore.EnrichDriversAsync(db, [driver], ct);
        var type = DriverAvailabilityService.CanonicalEmploymentType(driver.DriverType, driver.DriverGroup);
        if (type is not ("Agency" or "Casual")) return BadRequest(new { message = "Availability records can only be edited for Agency or Casual drivers. Employment type remains controlled by Master Data." });
        if (request.LongTermPlacement && type != "Agency") return BadRequest(new { message = "Only Agency drivers can use a long-term repeated placement." });
        if (request.LongTermPlacement && request.PlacementEndDate is null) return BadRequest(new { message = "A long-term agency placement must have a placement end date." });
        if (request.LongTermPlacement && request.PlacementEndDate < DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(request.AvailableFromUtc, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime))
            return BadRequest(new { message = "Placement end date cannot be before the availability start date." });
        return null;
    }

    private static void Apply(DriverAvailabilityWindow window, DriverAvailabilityWriteRequest request, string actor)
    {
        window.DriverId = request.DriverId;
        window.AvailableFromUtc = request.AvailableFromUtc.ToUniversalTime();
        window.AvailableUntilUtc = request.AvailableUntilUtc.ToUniversalTime();
        window.Confirmed = request.Confirmed;
        window.LongTermPlacement = request.LongTermPlacement;
        window.PlacementEndDate = request.LongTermPlacement ? request.PlacementEndDate : null;
        window.UsualDays = request.LongTermPlacement ? Clean(request.UsualDays, 80) : null;
        window.Notes = Clean(request.Notes, 500);
        window.BookingReference = Clean(request.BookingReference, 160);
        window.UpdatedBy = actor;
        window.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private async Task AddAudit(DriverAvailabilityWindow window, string eventType, string actor, CancellationToken ct)
    {
        db.OperationalHistoryEvents.Add(new OperationalHistoryEvent
        {
            EntityType = "DriverAvailability",
            EntityId = window.Id,
            EventType = eventType,
            Actor = actor,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new { window.DriverId, window.AvailableFromUtc, window.AvailableUntilUtc, window.Confirmed, window.LongTermPlacement, window.PlacementEndDate, window.UsualDays, window.BookingReference }),
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private static string? Clean(string? value, int length)
    {
        var clean = value?.Trim();
        if (string.IsNullOrWhiteSpace(clean)) return null;
        return clean.Length <= length ? clean : clean[..length];
    }
}

public sealed record DriverAvailabilityWriteRequest(
    Guid DriverId,
    DateTimeOffset AvailableFromUtc,
    DateTimeOffset AvailableUntilUtc,
    bool Confirmed,
    bool LongTermPlacement,
    DateOnly? PlacementEndDate,
    string? UsualDays,
    string? Notes,
    string? BookingReference);
