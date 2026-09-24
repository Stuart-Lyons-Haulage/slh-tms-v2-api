using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-timesheets"), Authorize]
public sealed class WeeklyDriverTimesheetsController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    DotTrackingClient dotTracking,
    SageHrClient sageHr,
    ILogger<WeeklyDriverTimesheetsController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet]
    public async Task<IActionResult> Range([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > 92)
            return BadRequest(new { message = "Choose a valid date range of no more than 93 days." });

        return Ok(await Build(from, to, ct));
    }

    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly([FromQuery] DateOnly weekStart, CancellationToken ct) =>
        Ok(await Build(weekStart, weekStart.AddDays(6), ct));

    private async Task<object> Build(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var today = LondonToday();

        var drivers = await db.Drivers.AsNoTracking()
            .Where(x => x.Active)
            .OrderBy(x => x.DisplayName)
            .ToListAsync(ct);
        try { await MasterDetailStore.EnrichDriversAsync(db, drivers, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Driver detail enrichment was unavailable for timesheets.");
        }
        drivers = drivers.Where(DriverPopulationRules.IsDriver).ToList();

        IReadOnlyList<SageHrEmployee> sageEmployees = [];
        string? sageError = null;
        if (sageHr.IsConfigured)
        {
            try { sageEmployees = await sageHr.GetActiveEmployeesAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sageError = ex.GetBaseException().Message;
                logger.LogWarning(ex, "Sage HR roster could not be loaded for timesheet employment classification.");
            }
        }
        else
        {
            sageError = "Sage HR is not configured.";
        }

        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        var vehicleById = vehicles.ToDictionary(x => x.Id);
        var vehicleAliases = vehicles.ToDictionary(
            x => x.Id,
            x => new[] { x.Registration, x.Abbreviation, x.FleetNumber }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        var vehicleByAlias = new Dictionary<string, Vehicle>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in vehicles)
            foreach (var alias in vehicleAliases[vehicle.Id])
                vehicleByAlias.TryAdd(Normalise(alias), vehicle);

        var loads = new List<Load>();
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var dayLoads = await PlanningResilience.ReadLoadsAsync(db, day, ct);
            try { await RunOperationalStore.EnrichAsync(db, dayLoads, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Operational allocation enrichment unavailable for timesheets on {Date}.", day);
            }
            loads.AddRange(dayLoads.Where(x => x.Status != LoadStatus.Cancelled));
        }
        loads = PlanningResilience.CollapseLogicalDuplicates(loads)
            .Where(x => x.PlanningDate >= from && x.PlanningDate <= to)
            .ToList();

        var tachoByDate = new Dictionary<DateOnly, IReadOnlyList<TachoDriverDutyStatus>>();
        string? tachoError = null;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            if (day > today)
            {
                tachoByDate[day] = [];
                continue;
            }

            try { tachoByDate[day] = await tachoMaster.GetDriverDutyStatusesAsync(day, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tachoError ??= ex.GetBaseException().Message;
                tachoByDate[day] = [];
                logger.LogWarning(ex, "TachoMaster timesheet duty read failed for {Date}.", day);
            }
        }

        var trackingByDate = new Dictionary<DateOnly, IReadOnlyList<DotTelemetryRecord>>();
        string? trackerError = null;
        var providerHistoryDays = 0;
        var legacyFallbackDays = 0;

        // RoadTech deliberately remains the owner of breadcrumb history. The operational SQL
        // database stores current state/geofence visits rather than every GPS point, so timesheets
        // must read the provider's historical endpoint instead of expecting VehicleTrackingEvents
        // to contain a complete journey trail.
        for (var day = from; day <= to.AddDays(1); day = day.AddDays(1))
        {
            if (day > today)
            {
                trackingByDate[day] = [];
                continue;
            }

            try
            {
                var providerRows = await dotTracking.GetHistoricalVehicleEventsAsync(day, ct);
                var records = providerRows
                    .Select(DotTelemetryRecord.FromProvider)
                    .Where(record => !string.IsNullOrWhiteSpace(record.VehicleIdentifier))
                    .OrderBy(record => record.EventTimeUtc)
                    .ToList();
                trackingByDate[day] = records;
                providerHistoryDays++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                trackerError ??= ex.GetBaseException().Message;
                logger.LogWarning(ex, "RoadTech historical movement read failed for timesheets on {Date}; checking retained legacy rows.", day);

                try
                {
                    var dayStart = StartOfUkDay(day);
                    var dayEnd = StartOfUkDay(day.AddDays(1));
                    var legacy = await db.VehicleTrackingEvents.AsNoTracking()
                        .Where(x => x.EventTimeUtc >= dayStart && x.EventTimeUtc < dayEnd)
                        .OrderBy(x => x.EventTimeUtc)
                        .Take(100000)
                        .ToListAsync(ct);
                    trackingByDate[day] = legacy.Select(x => new DotTelemetryRecord(
                        x.ProviderEventId,
                        x.VehicleIdentifier,
                        x.EventTimeUtc,
                        x.Latitude,
                        x.Longitude,
                        x.SpeedKph,
                        x.IgnitionOn,
                        x.IsMoving,
                        x.MatchStatus,
                        x.RawPayload)).ToList();
                    if (legacy.Count > 0) legacyFallbackDays++;
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    db.ChangeTracker.Clear();
                    logger.LogWarning(fallbackEx, "Legacy RoadTech movement fallback also failed for {Date}.", day);
                    trackingByDate[day] = [];
                }
            }
        }

        var driverRows = new List<object>();
        var unmatchedTachoDutyCount = 0;

        foreach (var day in tachoByDate.Keys)
        {
            foreach (var duty in tachoByDate[day])
                if (!drivers.Any(driver => DriverMatches(driver, duty)))
                    unmatchedTachoDutyCount++;
        }

        foreach (var driver in drivers)
        {
            var sageMatch = sageEmployees.FirstOrDefault(employee => SageMatches(driver, employee));
            // Sage HR is the authority for employed status. Anyone who does not match the
            // active Sage roster must not appear in the employed payroll section.
            var employmentType = sageMatch is not null ? "Employed" : "Agency";
            var agencyName = employmentType == "Agency"
                ? FirstMeaningful(driver.AgencyName, driver.DriverGroup, "Not in Sage HR")
                : null;
            var days = new List<object>();
            var daysWorked = 0;
            var reviewDays = 0;
            var dutySpanTotal = 0;
            var activityTotal = 0;
            var driveTotal = 0;
            var workTotal = 0;
            var availableTotal = 0;
            var restTotal = 0;

            for (var day = from; day <= to; day = day.AddDays(1))
            {
                var dayLoads = loads.Where(x => x.DriverId == driver.Id && x.PlanningDate == day)
                    .OrderBy(x => x.Reference)
                    .ToList();
                var duties = tachoByDate.TryGetValue(day, out var source)
                    ? source.Where(x => DriverMatches(driver, x)).OrderBy(x => x.DutyStartUtc).ToList()
                    : [];

                var tachoStart = duties.Count > 0 ? duties.Min(x => x.DutyStartUtc) : (DateTimeOffset?)null;
                var tachoEnd = duties.Count > 0 && duties.All(x => x.DutyEndUtc is not null)
                    ? duties.Max(x => x.DutyEndUtc) : (DateTimeOffset?)null;
                var dutySpanMinutes = tachoStart is not null && tachoEnd is not null && tachoEnd >= tachoStart
                    ? Minutes(tachoEnd.Value - tachoStart.Value) : (int?)null;
                var driveMinutes = duties.Sum(x => x.DriveMinutes);
                var workMinutes = duties.Sum(x => x.WorkMinutes);
                var availableMinutes = duties.Sum(x => x.AvailableMinutes);
                var restMinutes = duties.Sum(x => x.RestMinutes);
                var activityMinutes = duties.Count == 0 ? (int?)null : driveMinutes + workMinutes + availableMinutes;

                var trackingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var displayVehicles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var duty in duties)
                {
                    if (string.IsNullOrWhiteSpace(duty.VehicleCode)) continue;
                    trackingKeys.Add(Normalise(duty.VehicleCode));
                    displayVehicles.Add(duty.VehicleCode);
                    if (vehicleByAlias.TryGetValue(Normalise(duty.VehicleCode), out var vehicle))
                    {
                        displayVehicles.Add(vehicle.Registration);
                        foreach (var alias in vehicleAliases[vehicle.Id]) trackingKeys.Add(Normalise(alias));
                    }
                }

                foreach (var load in dayLoads)
                {
                    if (load.VehicleId is not Guid vehicleId || !vehicleById.TryGetValue(vehicleId, out var vehicle)) continue;
                    displayVehicles.Add(vehicle.Registration);
                    foreach (var alias in vehicleAliases[vehicle.Id]) trackingKeys.Add(Normalise(alias));
                }

                var plannedTimes = dayLoads.SelectMany(x => x.Stops)
                    .Where(x => x.PlannedArrivalUtc is not null)
                    .Select(x => x.PlannedArrivalUtc!.Value)
                    .OrderBy(x => x)
                    .ToList();
                var plannedStart = plannedTimes.Count > 0 ? plannedTimes.First() : (DateTimeOffset?)null;
                var plannedEnd = plannedTimes.Count > 0 ? plannedTimes.Last() : (DateTimeOffset?)null;

                var trackerWindowStart = tachoStart ?? StartOfUkDay(day);
                var trackerWindowEnd = tachoEnd ?? (tachoStart?.AddHours(20) ?? StartOfUkDay(day.AddDays(1)).AddHours(6));
                if (trackerWindowEnd <= trackerWindowStart) trackerWindowEnd = trackerWindowStart.AddHours(20);

                var trackingForDuty = (trackingByDate.TryGetValue(day, out var dayTracking) ? dayTracking : [])
                    .Concat(trackingByDate.TryGetValue(day.AddDays(1), out var nextDayTracking) ? nextDayTracking : []);
                var movement = trackingForDuty
                    .Where(x => x.EventTimeUtc >= trackerWindowStart && x.EventTimeUtc <= trackerWindowEnd)
                    .Where(x => trackingKeys.Contains(Normalise(x.VehicleIdentifier)))
                    .Where(IsMovement)
                    .OrderBy(x => x.EventTimeUtc)
                    .ToList();

                var firstMovement = movement.Count > 0 ? movement.Min(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var lastMovement = movement.Count > 0 ? movement.Max(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var movementSpanMinutes = firstMovement is not null && lastMovement is not null
                    ? Minutes(lastMovement.Value - firstMovement.Value) : (int?)null;
                var startVarianceMinutes = tachoStart is not null && firstMovement is not null
                    ? Minutes(firstMovement.Value - tachoStart.Value) : (int?)null;
                var finishVarianceMinutes = tachoEnd is not null && lastMovement is not null
                    ? Minutes(lastMovement.Value - tachoEnd.Value) : (int?)null;

                var notes = new List<string>();
                if (tachoStart is not null && firstMovement is not null && Math.Abs(startVarianceMinutes ?? 0) > 45)
                    notes.Add($"Tacho start and first vehicle movement differ by {Math.Abs(startVarianceMinutes!.Value)} minutes.");
                if (tachoEnd is not null && lastMovement is not null && Math.Abs(finishVarianceMinutes ?? 0) > 60)
                    notes.Add($"Tacho finish and last vehicle movement differ by {Math.Abs(finishVarianceMinutes!.Value)} minutes.");
                if (dayLoads.Count == 0 && duties.Count > 0)
                    notes.Add("No TMS route was allocated; timesheet is based on TachoMaster and vehicle movement evidence.");
                if (duties.Count == 0 && dayLoads.Count > 0 && movement.Count == 0 && day <= today)
                    notes.Add("A TMS route exists but no TachoMaster or vehicle-movement evidence was found.");

                var hasTacho = duties.Count > 0;
                var hasTracker = movement.Count > 0;
                var status = tachoStart is not null && tachoEnd is null
                    ? "Open duty"
                    : notes.Any(x => x.Contains("differ by", StringComparison.OrdinalIgnoreCase))
                        ? "Review"
                        : hasTacho && hasTracker
                            ? "Confirmed"
                            : hasTacho
                                ? "Tacho only"
                                : hasTracker
                                    ? "Tracker only"
                                    : dayLoads.Count > 0
                                        ? "Missing evidence"
                                        : "No work";

                var worked = hasTacho || hasTracker || dayLoads.Count > 0;
                if (!worked) continue;

                daysWorked++;
                if (status is "Review" or "Missing evidence" or "Open duty") reviewDays++;
                dutySpanTotal += dutySpanMinutes ?? 0;
                activityTotal += activityMinutes ?? 0;
                driveTotal += driveMinutes;
                workTotal += workMinutes;
                availableTotal += availableMinutes;
                restTotal += restMinutes;

                days.Add(new
                {
                    date = day,
                    startUtc = tachoStart ?? firstMovement ?? plannedStart,
                    finishUtc = tachoEnd ?? lastMovement ?? plannedEnd,
                    dutySpanMinutes,
                    activityMinutes,
                    driveMinutes,
                    workMinutes,
                    availableMinutes,
                    restMinutes,
                    breakCount = duties.Sum(x => x.BreakCount),
                    breakMinutes = duties.Where(x => x.BreakMinutes is not null).Sum(x => x.BreakMinutes ?? 0),
                    tachoStartUtc = tachoStart,
                    tachoEndUtc = tachoEnd,
                    firstMovementUtc = firstMovement,
                    lastMovementUtc = lastMovement,
                    movementSpanMinutes,
                    startVarianceMinutes,
                    finishVarianceMinutes,
                    vehicles = displayVehicles.OrderBy(x => x).ToArray(),
                    runs = dayLoads.Select(x => RunDisplayLabel.For(x)).Distinct().ToArray(),
                    routeAllocated = dayLoads.Count > 0,
                    status,
                    notes
                });
            }

            if (daysWorked == 0) continue;

            driverRows.Add(new
            {
                driverId = driver.Id,
                driverName = driver.DisplayName,
                driver.EmployeeNumber,
                sageMatched = sageMatch is not null,
                employmentType,
                agencyName,
                daysWorked,
                reviewDays,
                dutySpanMinutes = dutySpanTotal,
                activityMinutes = activityTotal,
                driveMinutes = driveTotal,
                workMinutes = workTotal,
                availableMinutes = availableTotal,
                restMinutes = restTotal,
                status = reviewDays == 0 ? "Confirmed" : "Review",
                days
            });
        }

        var employed = driverRows.Where(RowEmploymentIs("Employed")).Count();
        var agency = driverRows.Where(RowEmploymentIs("Agency")).Count();

        return new
        {
            from,
            to,
            weekStarts = "Wednesday",
            weekEnds = "Tuesday",
            generatedAtUtc = DateTimeOffset.UtcNow,
            sourceStatus = new
            {
                tachoMaster = tachoError is null ? "Available" : $"Partial: {tachoError}",
                roadTech = trackerError is null
                    ? $"Available - historical movement loaded directly from RoadTech for {providerHistoryDays} day(s)"
                    : legacyFallbackDays > 0
                        ? $"Partial - RoadTech history failed for at least one day; {legacyFallbackDays} day(s) used retained legacy movement rows. {trackerError}"
                        : $"Partial - RoadTech historical movement unavailable for at least one day. {trackerError}",
                sageHr = sageError is null
                    ? $"Available - {sageEmployees.Count} active employee record(s); employed timesheets require a Sage HR match"
                    : $"Unavailable - {sageError}. No driver is treated as employed without Sage HR confirmation."
            },
            summary = new
            {
                drivers = driverRows.Count,
                employedDrivers = employed,
                agencyDrivers = agency,
                reviewDrivers = driverRows.Count(RowNeedsReview),
                unmatchedTachoDuties = unmatchedTachoDutyCount
            },
            drivers = driverRows
        };
    }

    private static Func<object, bool> RowEmploymentIs(string type) => row =>
    {
        var property = row.GetType().GetProperty("employmentType");
        return string.Equals(property?.GetValue(row)?.ToString(), type, StringComparison.OrdinalIgnoreCase);
    };

    private static bool RowNeedsReview(object row)
    {
        var property = row.GetType().GetProperty("status");
        return string.Equals(property?.GetValue(row)?.ToString(), "Review", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstMeaningful(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();

    private static bool SageMatches(Driver driver, SageHrEmployee employee)
    {
        if (!string.IsNullOrWhiteSpace(employee.EmployeeNumber) &&
            !string.IsNullOrWhiteSpace(driver.EmployeeNumber) &&
            Normalise(employee.EmployeeNumber) == Normalise(driver.EmployeeNumber))
            return true;

        return Normalise($"{employee.FirstName} {employee.LastName}") == Normalise(driver.DisplayName);
    }

    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus status)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var linked) && linked > 0 && linked == status.MemberCode) return true;
        if (SameCard(driver.TachoCardNumber, status.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(status.EmployeeNumber) &&
            Normalise(driver.EmployeeNumber) == Normalise(status.EmployeeNumber)) return true;

        var names = new[] { driver.TachoName, driver.DisplayName }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => Normalise(x!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return names.Contains(Normalise(status.DriverName));
    }

    private static bool SameCard(string? left, string? right)
    {
        var a = Normalise(left ?? string.Empty);
        var b = Normalise(right ?? string.Empty);
        return a.Length >= 8 && b.Length >= 8 &&
               (a == b || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsMovement(DotTelemetryRecord item) =>
        item.IsMoving == true || (item.SpeedKph ?? 0m) > 0m;

    private static int Minutes(TimeSpan value) => (int)Math.Round(value.TotalMinutes);

    private static DateOnly LondonToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);

    private static DateTimeOffset StartOfUkDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, London.GetUtcOffset(local)).ToUniversalTime();
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
