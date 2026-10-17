using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/night-outs"), Authorize]
public sealed class NightOutController(TmsDbContext db, TachoMasterClient tachoMaster, ILogger<NightOutController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet("report")]
    public async Task<IActionResult> Report([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (to < from) return BadRequest(new { message = "The to date must be on or after the from date." });
        if (to.DayNumber - from.DayNumber > 62) return BadRequest(new { message = "Night-out reporting is limited to 63 days at a time." });

        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate >= from && x.PlanningDate <= to && x.DriverId != null && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).ToListAsync(ct);

        var driverIds = loads.Select(x => x.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var drivers = await db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers.Values.ToList(), ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles.Values.ToList(), ct);

        var allIdentifiers = aliasesByVehicle.Values.SelectMany(aliases => aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var startUtc = StartOfUkDay(from);
        var endUtc = StartOfUkDay(to.AddDays(1));
        var trackingEvents = allIdentifiers.Count == 0 ? new List<VehicleTrackingEvent>() : await db.VehicleTrackingEvents.AsNoTracking()
            .Where(x => x.EventTimeUtc >= startUtc && x.EventTimeUtc < endUtc.AddHours(12) && allIdentifiers.Contains(x.VehicleIdentifier))
            .OrderByDescending(x => x.EventTimeUtc).Take(50000).ToListAsync(ct);

        var tachoByDate = new Dictionary<DateOnly, IReadOnlyList<TachoDriverDutyStatus>>();
        foreach (var date in loads.Select(x => x.PlanningDate).Distinct())
        {
            try { tachoByDate[date] = await tachoMaster.GetDriverDutyStatusesAsync(date, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "TachoMaster night-out validation unavailable for {Date}", date);
                tachoByDate[date] = [];
            }
        }

        var rows = loads.Select(load =>
        {
            drivers.TryGetValue(load.DriverId!.Value, out var driver);
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var foundVehicle) ? foundVehicle : null;
            var aliases = vehicle is not null && aliasesByVehicle.TryGetValue(vehicle.Id, out var knownAliases)
                ? knownAliases
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dayStart = StartOfUkDay(load.PlanningDate);
            var dayEnd = StartOfUkDay(load.PlanningDate.AddDays(1));
            var overnightEnd = dayEnd.AddHours(9);
            var lastTrack = trackingEvents.FirstOrDefault(x =>
                x.EventTimeUtc >= dayStart && x.EventTimeUtc < overnightEnd &&
                ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, x.VehicleIdentifier));

            var duties = tachoByDate.TryGetValue(load.PlanningDate, out var dayDuties) ? dayDuties : [];
            var duty = duties
                .Where(candidate => driver is null || DriverMatches(driver, candidate))
                .Where(candidate => vehicle is null || ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, candidate.VehicleCode))
                .OrderByDescending(candidate => candidate.DutyStartUtc)
                .FirstOrDefault();
            var requested = ReadNightOut(load.PlannerNotes) == true;
            var cardOpenAcrossMidnight = duty is not null && LiveDriverEvidenceRules.SpansUkMidnight(load.PlanningDate, duty.DutyStartUtc, duty.DutyEndUtc);
            var final = load.Stops.OrderByDescending(x => x.Sequence).FirstOrDefault();
            var evidenceStatus = cardOpenAcrossMidnight && requested && lastTrack is not null ? "Confirmed - card remained inserted + DOT evidence"
                : cardOpenAcrossMidnight && requested ? "Confirmed - card/open duty spans midnight"
                : cardOpenAcrossMidnight && !requested ? "Detected - card remained inserted; planner tick missing"
                : requested && lastTrack is not null && duty is not null ? "DOT + Tacho evidence captured"
                : requested && lastTrack is not null ? "DOT evidence captured"
                : requested && duty is not null ? "Tacho evidence captured"
                : requested ? "Planner confirmation only"
                : "No night out evidence";
            return new
            {
                load.Id,
                load.Reference,
                load.PlanningDate,
                driverId = load.DriverId,
                driverName = driver?.DisplayName,
                vehicle = vehicle?.Registration,
                requested,
                cardOpenAcrossMidnight,
                finalStop = final?.Name,
                trackerLastEventUtc = lastTrack?.EventTimeUtc,
                trackerLatitude = lastTrack?.Latitude,
                trackerLongitude = lastTrack?.Longitude,
                trackerMoving = lastTrack?.IsMoving,
                tachoDriver = duty?.DriverName,
                tachoDutyStartUtc = duty?.DutyStartUtc,
                tachoDutyEndUtc = duty?.DutyEndUtc,
                evidenceStatus
            };
        }).Where(row => row.requested || row.cardOpenAcrossMidnight).ToList();

        return Ok(new
        {
            from,
            to,
            generatedAtUtc = DateTimeOffset.UtcNow,
            rows,
            counts = rows.GroupBy(x => x.driverName ?? "Unknown")
                .Select(g => new
                {
                    driver = g.Key,
                    nights = g.Count(),
                    planned = g.Count(x => x.requested),
                    detectedFromOpenCard = g.Count(x => x.cardOpenAcrossMidnight),
                    plannerTickMissing = g.Count(x => x.cardOpenAcrossMidnight && !x.requested)
                })
                .OrderByDescending(x => x.nights)
        });
    }

    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus duty)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode > 0 && memberCode == duty.MemberCode) return true;
        if (CardsMatch(driver.TachoCardNumber, duty.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(duty.EmployeeNumber) &&
            Normalise(driver.EmployeeNumber) == Normalise(duty.EmployeeNumber)) return true;
        var names = new[] { driver.DisplayName, driver.TachoName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalise(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains(Normalise(duty.DriverName));
    }

    private static bool CardsMatch(string? left, string? right)
    {
        var a = Normalise(left);
        var b = Normalise(right);
        return a.Length >= 8 && b.Length >= 8 &&
               (a == b || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTimeOffset StartOfUkDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, London.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static bool? ReadNightOut(string? notes)
    {
        var value = (notes ?? string.Empty).Split('·').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("Night out:", StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        return value.EndsWith("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.EndsWith("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    }
}
