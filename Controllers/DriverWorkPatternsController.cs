using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>Drivers' working patterns (from Sage HR) and where each driver is in theirs on a date.</summary>
[ApiController]
[Route("api/v1/driver-work-patterns")]
[Authorize]
public sealed class DriverWorkPatternsController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var patterns = await DriverWorkPatterns.LatestAsync(db, ct);
        return Ok(new
        {
            date = day,
            patterns = patterns.Values.Select(pattern =>
            {
                var plan = DriverWorkPatterns.Evaluate(pattern, day);
                return new
                {
                    pattern.DriverId,
                    pattern.Name,
                    pattern.Blocks,
                    pattern.CycleStart,
                    pattern.Source,
                    pattern.UpdatedBy,
                    pattern.UpdatedAtUtc,
                    description = DriverWorkPatterns.Describe(pattern.Blocks),
                    working = plan?.Working,
                    day = plan?.Day,
                    blockLength = plan?.BlockLength
                };
            }),
            // Distinct patterns already in use, so the same Sage pattern is picked rather than retyped.
            library = patterns.Values
                .GroupBy(pattern => (pattern.Name, pattern.Blocks, pattern.CycleStart))
                .Select(group => new { group.Key.Name, group.Key.Blocks, group.Key.CycleStart, drivers = group.Count() })
                .OrderByDescending(item => item.drivers)
        });
    }

    [HttpPut("{driverId:guid}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Put(Guid driverId, [FromBody] DriverWorkPatterns.SaveRequest request, CancellationToken ct)
    {
        if (!await db.Drivers.AnyAsync(driver => driver.Id == driverId, ct)) return NotFound(new { message = "Driver not found." });
        try
        {
            var user = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value;
            return Ok(await DriverWorkPatterns.SaveAsync(db, driverId, request, user, ct));
        }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
