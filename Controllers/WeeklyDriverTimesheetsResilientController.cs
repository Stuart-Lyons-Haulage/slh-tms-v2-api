using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-timesheets"), Authorize]
public sealed class WeeklyDriverTimesheetsResilientController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    SageHrClient sageHr,
    ILogger<WeeklyDriverTimesheetsResilientController> logger) : ControllerBase
{
    [HttpGet("weekly-resilient")]
    public async Task<IActionResult> Weekly([FromQuery] DateOnly weekStart, CancellationToken ct)
    {
        var weekEnd = weekStart.AddDays(6);
        var fromUtc = new DateTimeOffset(weekStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
        var toUtc = new DateTimeOffset(weekEnd.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

        List<Driver> drivers;
        try { drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Weekly driver rota could not load drivers for {WeekStart}.", weekStart);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "Driver master data is unavailable for the weekly Night Outs view.", detail = ex.GetBaseException().Message });
        }

        var (loads, tmsSource) = await ReadLoads(weekStart, weekEnd, ct);
        var vehicleIds = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        Dictionary<Guid, Vehicle> vehicles;
        try
        {
            vehicles = vehicleIds.Count == 0
                ? []
                : await db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Vehicle enrichment unavailable for weekly Night Outs.");
            db.ChangeTracker.Clear();
            vehicles = [];
        }

        var rawVehicleIdentifiers = vehicles.Values
            .SelectMany(v => new[] { v.Registration, v.Abbreviation, v.FleetNumber })
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var vehicleKeys = rawVehicleIdentifiers.Select(Normalise).ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<Models.Tracking.VehicleTrackingEvent> trackingEvents = [];
        string? dotError = null;
        if (rawVehicleIdentifiers.Count > 0)
        {
            try
            {
                trackingEvents = await db.VehicleTrackingEvents.AsNoTracking()
                    .Where(x => x.EventTimeUtc >= fromUtc && x.EventTimeUtc < toUtc && rawVehicleIdentifiers.Contains(x.VehicleIdentifier))
                    .OrderBy(x => x.EventTimeUtc).Take(100000).ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                dotError = ex.GetBaseException().Message;
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "DOT/Falcon weekly reconciliation unavailable for {WeekStart}.", weekStart);
            }
        }

        IReadOnlyList<SageHrEmployee> sageEmployees = [];
        string? sageError = null;
        if (sageHr.IsConfigured)
        {
            try { sageEmployees = await sageHr.GetActiveEmployeesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sageError = ex.GetBaseException().Message;
                logger.LogWarning(ex, "Sage HR roster unavailable for weekly Night Outs.");
            }
        }
        else sageError = "Sage HR is not configured.";

        var tachoByDate = new Dictionary<DateOnly, IReadOnlyCollection<TachoDriverDutyStatus>>();
        string? tachoError = null;
        for (var date = weekStart; date <= weekEnd; date = date.AddDays(1))
        {
            if (date > DateOnly.FromDateTime(DateTime.UtcNow)) { tachoByDate[date] = []; continue; }
            try { tachoByDate[date] = await tachoMaster.GetDriverDutyStatusesAsync(date, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tachoError ??= ex.GetBaseException().Message;
                tachoByDate[date] = [];
                logger.LogWarning(ex, "TachoMaster duty reconciliation failed for {Date}.", date);
            }
        }

        var driverRows = drivers.Select(driver =>
        {
            var sage = sageEmployees.FirstOrDefault(x => SageMatches(driver, x));
            var days = new List<object>();
            var workedDays = 0;
            var nights = 0;
            var discrepancyCount = 0;
            var tachoMinutesWeek = 0;
            var plannedMinutesWeek = 0;

            for (var date = weekStart; date <= weekEnd; date = date.AddDays(1))
            {
                var dayLoads = loads.Where(x => x.DriverId == driver.Id && x.PlanningDate == date).OrderBy(x => x.Reference).ToList();
                var plannedTimes = dayLoads.SelectMany(x => x.Stops).Where(x => x.PlannedArrivalUtc != null).Select(x => x.PlannedArrivalUtc!.Value).OrderBy(x => x).ToList();
                var plannedStart = plannedTimes.Count > 0 ? plannedTimes.First() : (DateTimeOffset?)null;
                var plannedEnd = plannedTimes.Count > 0 ? plannedTimes.Last() : (DateTimeOffset?)null;
                var plannedMinutes = plannedStart != null && plannedEnd != null && plannedEnd >= plannedStart ? (int)Math.Round((plannedEnd.Value - plannedStart.Value).TotalMinutes) : (int?)null;
                if (plannedMinutes is int pm) plannedMinutesWeek += pm;

                var tachoMatches = tachoByDate.TryGetValue(date, out var duties)
                    ? duties.Where(x => DriverMatches(driver, x)).OrderBy(x => x.DutyStartUtc).ToList()
                    : [];
                var tachoStart = tachoMatches.Count > 0 ? tachoMatches.Min(x => x.DutyStartUtc) : (DateTimeOffset?)null;
                var tachoEnds = tachoMatches.Where(x => x.DutyEndUtc != null).Select(x => x.DutyEndUtc!.Value).ToList();
                var tachoEnd = tachoMatches.Count > 0 && tachoEnds.Count == tachoMatches.Count ? tachoEnds.Max() : (DateTimeOffset?)null;
                var tachoMinutes = tachoMatches.Count > 0 ? tachoMatches.Sum(x => x.WorkMinutes + x.DriveMinutes + x.AvailableMinutes) : (int?)null;
                if (tachoMinutes is int tm) tachoMinutesWeek += tm;

                var assignedVehicleKeys = dayLoads.Where(x => x.VehicleId != null && vehicles.ContainsKey(x.VehicleId.Value))
                    .SelectMany(x => VehicleKeys(vehicles[x.VehicleId!.Value].Registration, vehicles[x.VehicleId!.Value].Abbreviation, vehicles[x.VehicleId!.Value].FleetNumber))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var dayStartUtc = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
                var dayEndUtc = dayStartUtc.AddDays(1);
                var dot = trackingEvents.Where(x => x.EventTimeUtc >= dayStartUtc && x.EventTimeUtc < dayEndUtc && assignedVehicleKeys.Contains(Normalise(x.VehicleIdentifier)) && (x.IsMoving == true || x.IgnitionOn == true || x.SpeedKph > 0)).ToList();
                var dotStart = dot.Count > 0 ? dot.Min(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var dotEnd = dot.Count > 0 ? dot.Max(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var dotMinutes = dotStart != null && dotEnd != null ? (int)Math.Round((dotEnd.Value - dotStart.Value).TotalMinutes) : (int?)null;

                var nightOut = dayLoads.Any(x => ReadNightOut(x.PlannerNotes) == true);
                if (nightOut) nights++;
                var discrepancies = new List<string>();
                if (dayLoads.Count > 0 && tachoMatches.Count == 0 && date <= DateOnly.FromDateTime(DateTime.UtcNow)) discrepancies.Add("Planned work exists but no TachoMaster duty matched.");
                if (dayLoads.Count == 0 && tachoMatches.Count > 0) discrepancies.Add("TachoMaster duty exists but no TMS run is allocated.");
                if (dot.Count > 0 && tachoMatches.Count == 0) discrepancies.Add("DOT shows vehicle movement but no TachoMaster duty matched.");
                if (dotError is null && dayLoads.Count > 0 && assignedVehicleKeys.Count > 0 && dot.Count == 0 && date <= DateOnly.FromDateTime(DateTime.UtcNow)) discrepancies.Add("Allocated vehicle has no DOT movement evidence for the day.");
                if (plannedStart != null && tachoStart != null && Math.Abs((tachoStart.Value - plannedStart.Value).TotalMinutes) > 60) discrepancies.Add($"TMS planned start and TachoMaster duty start differ by {Math.Abs((int)(tachoStart.Value - plannedStart.Value).TotalMinutes)} minutes.");
                if (tachoStart != null && dotStart != null && Math.Abs((dotStart.Value - tachoStart.Value).TotalMinutes) > 45) discrepancies.Add($"First DOT movement and TachoMaster duty start differ by {Math.Abs((int)(dotStart.Value - tachoStart.Value).TotalMinutes)} minutes.");
                if (tachoEnd != null && dotEnd != null && Math.Abs((dotEnd.Value - tachoEnd.Value).TotalMinutes) > 60) discrepancies.Add($"Last DOT movement and TachoMaster duty end differ by {Math.Abs((int)(dotEnd.Value - tachoEnd.Value).TotalMinutes)} minutes.");
                if (sageEmployees.Count > 0 && sage is null && (dayLoads.Count > 0 || tachoMatches.Count > 0)) discrepancies.Add("Working evidence exists but the driver is not matched to the active Sage HR roster.");

                var worked = dayLoads.Count > 0 || tachoMatches.Count > 0 || dot.Count > 0;
                if (worked) workedDays++;
                discrepancyCount += discrepancies.Count;
                days.Add(new
                {
                    date, worked, sageMatched = sage is not null,
                    tms = new { runCount = dayLoads.Count, runs = dayLoads.Select(x => x.Reference).ToList(), plannedStartUtc = plannedStart, plannedEndUtc = plannedEnd, plannedMinutes },
                    tacho = new { matched = tachoMatches.Count > 0, dutyStartUtc = tachoStart, dutyEndUtc = tachoEnd, totalMinutes = tachoMinutes, vehicles = tachoMatches.Select(x => x.VehicleCode).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList(), driveMinutes = tachoMatches.Sum(x => x.DriveMinutes), restMinutes = tachoMatches.Sum(x => x.RestMinutes) },
                    dot = new { movementEvents = dot.Count, firstMovementUtc = dotStart, lastMovementUtc = dotEnd, movementSpanMinutes = dotMinutes, vehicles = assignedVehicleKeys.ToList() },
                    nightOut, discrepancies,
                    status = discrepancies.Count == 0 ? worked ? "Confirmed" : "No work" : discrepancies.Count >= 2 ? "Review" : "Check"
                });
            }

            return new
            {
                driverId = driver.Id, driverName = driver.DisplayName, driver.EmployeeNumber, driver.TachoName,
                sageMatched = sage is not null, sageEmployeeId = sage?.Id,
                daysWorked = workedDays, nightsOut = nights, plannedMinutes = plannedMinutesWeek, tachoMinutes = tachoMinutesWeek,
                discrepancyCount, weeklyStatus = discrepancyCount == 0 ? "Confirmed" : discrepancyCount >= 3 ? "Review" : "Check", days
            };
        }).ToList();

        var workingDrivers = driverRows.Where(x => x.daysWorked > 0).OrderBy(x => x.driverName).ToList();
        return Ok(new
        {
            weekStart, weekEnd, generatedAtUtc = DateTimeOffset.UtcNow,
            sourceStatus = new
            {
                tms = tmsSource,
                dot = dotError is null ? $"Available from stored RoadTech Falcon tracking events ({vehicleKeys.Count} allocated vehicle identifiers)" : $"Unavailable for this refresh: {dotError}",
                tachoMaster = tachoError is null ? "Available - completed/current duty history through today" : $"Partial: {tachoError}",
                sageHr = sageError is null ? "Available - active employee roster" : $"Unavailable: {sageError}"
            },
            summary = new { liveDrivers = workingDrivers.Count, totalDaysWorked = workingDrivers.Sum(x => x.daysWorked), totalNightsOut = workingDrivers.Sum(x => x.nightsOut), driversWithDiscrepancies = workingDrivers.Count(x => x.discrepancyCount > 0), discrepancyCount = workingDrivers.Sum(x => x.discrepancyCount) },
            drivers = workingDrivers
        });
    }

    private async Task<(List<Load> Loads, string Source)> ReadLoads(DateOnly weekStart, DateOnly weekEnd, CancellationToken ct)
    {
        try
        {
            var loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
                .Where(x => x.PlanningDate >= weekStart && x.PlanningDate <= weekEnd && x.DriverId != null && x.Status != LoadStatus.Cancelled)
                .ToListAsync(ct);
            return (loads, "Available from TMS planning tables");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Dedicated Loads table unavailable for weekly Night Outs; using the audited planning register.");
            db.ChangeTracker.Clear();
            var loads = new List<Load>();
            for (var date = weekStart; date <= weekEnd; date = date.AddDays(1))
                loads.AddRange((await PlanningRegisterStore.ReadLoadsAsync(db, date, ct)).Where(x => x.DriverId != null && x.Status != LoadStatus.Cancelled));
            return (loads.GroupBy(x => x.Id).Select(x => x.Last()).ToList(), "Available from TMS planning register fallback");
        }
    }

    private static IEnumerable<string> VehicleKeys(params string?[] values) => values.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalise(x!)).Where(x => x.Length > 0);
    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus status)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var linkedMember) && linkedMember > 0 && linkedMember == status.MemberCode) return true;
        if (SameCard(driver.TachoCardNumber, status.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(status.EmployeeNumber) && string.Equals(Normalise(driver.EmployeeNumber), Normalise(status.EmployeeNumber), StringComparison.OrdinalIgnoreCase)) return true;
        var names = new[] { driver.TachoName, driver.DisplayName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalise(x!)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains(Normalise(status.DriverName));
    }
    private static bool SameCard(string? left, string? right)
    {
        var a = Normalise(left ?? string.Empty); var b = Normalise(right ?? string.Empty);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }
    private static bool SageMatches(Driver driver, SageHrEmployee employee)
    {
        if (!string.IsNullOrWhiteSpace(employee.EmployeeNumber) && string.Equals(Normalise(employee.EmployeeNumber), Normalise(driver.EmployeeNumber), StringComparison.OrdinalIgnoreCase)) return true;
        return string.Equals(Normalise($"{employee.FirstName} {employee.LastName}"), Normalise(driver.DisplayName), StringComparison.OrdinalIgnoreCase);
    }
    private static bool? ReadNightOut(string? notes)
    {
        var value = (notes ?? string.Empty).Split('·').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("Night out:", StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        return value.EndsWith("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.EndsWith("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    }
}
