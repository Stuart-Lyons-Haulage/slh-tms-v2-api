using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-hours-compliance"), Authorize]
public sealed class DriverHoursComplianceController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<DriverHoursComplianceController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpGet("weekly")]
    public async Task<IActionResult> Weekly([FromQuery] DateOnly date, CancellationToken ct) => Ok(await Build(date, ct));

    [HttpGet("non-employed.csv")]
    public async Task<IActionResult> NonEmployedCsv([FromQuery] DateOnly date, CancellationToken ct)
    {
        var report = await Build(date, ct);
        var csv = new StringBuilder();
        csv.AppendLine("Week Start,Week End,Date,Driver,Employee Number,Employment Type,Agency,Tacho Duty Start,Tacho Duty End,Tacho Duty Span Minutes,Tacho Activity Minutes,Tracker First Movement,Card To First Movement Minutes,Tracker Last Movement,Tracker Movement Span Minutes,Tracker Vehicle(s),Run(s),Span Variance Minutes,Invoice Evidence Status");
        foreach (var row in report.NonEmployedHours)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                report.WeekStart.ToString("yyyy-MM-dd"), report.WeekEnd.ToString("yyyy-MM-dd"), row.Date.ToString("yyyy-MM-dd"),
                row.DriverName, row.EmployeeNumber, row.EmploymentType, row.AgencyName ?? string.Empty,
                row.TachoDutyStartUtc?.ToString("O") ?? string.Empty, row.TachoDutyEndUtc?.ToString("O") ?? string.Empty,
                row.TachoDutySpanMinutes?.ToString() ?? string.Empty, row.TachoActivityMinutes?.ToString() ?? string.Empty,
                row.TrackerFirstMovementUtc?.ToString("O") ?? string.Empty, row.CardToFirstMovementMinutes?.ToString() ?? string.Empty,
                row.TrackerLastMovementUtc?.ToString("O") ?? string.Empty, row.TrackerMovementSpanMinutes?.ToString() ?? string.Empty,
                string.Join("; ", row.TrackerVehicles), string.Join("; ", row.Runs), row.VarianceMinutes?.ToString() ?? string.Empty, row.EvidenceStatus
            }.Select(Csv)));
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"non-employed-driver-hours-{report.WeekStart:yyyy-MM-dd}-to-{report.WeekEnd:yyyy-MM-dd}.csv");
    }

    private async Task<DriverHoursComplianceReport> Build(DateOnly selectedDate, CancellationToken ct)
    {
        var weekStart = WednesdayWeekStart(selectedDate);
        var weekEnd = weekStart.AddDays(6);
        var evidenceEnd = weekEnd.AddDays(2);

        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct);
        try { await MasterDetailStore.EnrichDriversAsync(db, drivers, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Driver detail enrichment unavailable for driver-hours compliance.");
        }

        List<Vehicle> vehicles;
        try { vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Vehicle master unavailable for driver-hours compliance.");
            vehicles = [];
        }

        var loads = new List<Load>();
        for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
            loads.AddRange(await PlanningResilience.ReadLoadsAsync(db, day, ct));
        loads = loads.Where(x => x.Status != LoadStatus.Cancelled).GroupBy(x => x.Id).Select(x => x.Last()).ToList();

        var tachoByDate = new Dictionary<DateOnly, IReadOnlyList<TachoDriverDutyStatus>>();
        string? tachoError = null;
        for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
        {
            if (day > DateOnly.FromDateTime(DateTime.UtcNow)) { tachoByDate[day] = []; continue; }
            try { tachoByDate[day] = await tachoMaster.GetDriverDutyStatusesAsync(day, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tachoError ??= ex.GetBaseException().Message;
                tachoByDate[day] = [];
                logger.LogWarning(ex, "TachoMaster driver-hours read failed for {Date}.", day);
            }
        }

        var trackingStartUtc = StartOfDayUtc(weekStart);
        var trackingEndUtc = StartOfDayUtc(evidenceEnd);
        List<VehicleTrackingEvent> tracking;
        string? trackerError = null;
        try
        {
            tracking = await db.VehicleTrackingEvents.AsNoTracking()
                .Where(x => x.EventTimeUtc >= trackingStartUtc && x.EventTimeUtc < trackingEndUtc)
                .OrderBy(x => x.EventTimeUtc).Take(150000).ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            trackerError = ex.GetBaseException().Message;
            db.ChangeTracker.Clear();
            tracking = [];
            logger.LogWarning(ex, "Tracker evidence unavailable for driver-hours compliance.");
        }

        var baseGeofence = await TryBaseGeofence(ct);
        List<GeofenceVisit> baseVisits = [];
        string? geofenceError = null;
        if (baseGeofence is not null)
        {
            try
            {
                baseVisits = await db.GeofenceVisits.AsNoTracking()
                    .Where(x => x.GeofenceId == baseGeofence.Id && x.EnteredAtUtc < trackingEndUtc && (x.ExitedAtUtc == null || x.ExitedAtUtc >= trackingStartUtc))
                    .OrderBy(x => x.EnteredAtUtc).Take(10000).ToListAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                geofenceError = ex.GetBaseException().Message;
                db.ChangeTracker.Clear();
                logger.LogWarning(ex, "Base geofence visits unavailable for night-out confirmation.");
            }
        }

        var nonEmployedRows = new List<NonEmployedHourRow>();
        var nightRows = new List<NightOutEvidenceRow>();

        foreach (var driver in drivers)
        {
            var employment = EmploymentType(driver);
            for (var day = weekStart; day <= weekEnd; day = day.AddDays(1))
            {
                var dayLoads = loads.Where(x => x.DriverId == driver.Id && x.PlanningDate == day).OrderBy(x => x.Reference).ToList();
                var duties = tachoByDate.TryGetValue(day, out var available)
                    ? available.Where(x => DriverMatches(driver, x)).OrderBy(x => x.DutyStartUtc).ToList()
                    : [];
                if (dayLoads.Count == 0 && duties.Count == 0) continue;

                var vehicleKeys = VehicleKeys(dayLoads, duties, vehicles);
                var dutyStart = duties.Count > 0 ? duties.Min(x => x.DutyStartUtc) : FirstPlannedTime(dayLoads);
                var dutyEnd = duties.Count > 0 && duties.All(x => x.DutyEndUtc is not null)
                    ? duties.Max(x => x.DutyEndUtc) : (DateTimeOffset?)null;
                var trackerWindowStart = dutyStart ?? StartOfDayUtc(day);
                var trackerWindowEnd = dutyEnd ?? StartOfDayUtc(day.AddDays(1)).AddHours(12);
                if (trackerWindowEnd <= trackerWindowStart) trackerWindowEnd = trackerWindowStart.AddHours(18);
                var dayTracking = tracking.Where(x => x.EventTimeUtc >= trackerWindowStart && x.EventTimeUtc <= trackerWindowEnd && vehicleKeys.Contains(Normalise(x.VehicleIdentifier))).ToList();
                var movement = dayTracking.Where(IsMovement).ToList();
                var trackerFirst = movement.Count > 0 ? movement.Min(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var trackerLast = movement.Count > 0 ? movement.Max(x => x.EventTimeUtc) : (DateTimeOffset?)null;
                var trackerSpan = trackerFirst is not null && trackerLast is not null ? Minutes(trackerLast.Value - trackerFirst.Value) : (int?)null;
                var dutySpan = dutyStart is not null && dutyEnd is not null && dutyEnd >= dutyStart ? Minutes(dutyEnd.Value - dutyStart.Value) : (int?)null;
                var tachoActivity = duties.Count == 0 ? (int?)null : duties.Sum(x => x.WorkMinutes + x.DriveMinutes + x.AvailableMinutes);
                var variance = dutySpan is not null && trackerSpan is not null ? trackerSpan - dutySpan : null;
                var cardToFirstMovement = dutyStart is not null && trackerFirst is not null ? Minutes(trackerFirst.Value - dutyStart.Value) : (int?)null;

                if (!string.Equals(employment, "Employed", StringComparison.OrdinalIgnoreCase))
                {
                    var evidence = cardToFirstMovement is int startGap && Math.Abs(startGap) > 45
                        ? $"Review start discrepancy: card/duty to first movement differs by {Math.Abs(startGap)} minutes"
                        : dutySpan is not null && trackerSpan is not null
                            ? Math.Abs(variance ?? 0) <= 90 ? "Confirmed by Tacho + tracker" : "Review variance: Tacho + tracker disagree"
                            : dutySpan is not null ? "Tacho only - tracker confirmation missing"
                            : trackerSpan is not null ? "Tracker only - Tacho hours missing"
                            : "No independent hours evidence";
                    nonEmployedRows.Add(new NonEmployedHourRow(day, driver.Id, driver.DisplayName, driver.EmployeeNumber, employment,
                        driver.AgencyName, dutyStart, dutyEnd, dutySpan, tachoActivity, trackerFirst, cardToFirstMovement, trackerLast, trackerSpan,
                        vehicleKeys.OrderBy(x => x).ToArray(), dayLoads.Select(x => x.Reference).Distinct().ToArray(), variance, evidence));
                }

                var plannerTick = dayLoads.Any(x => ReadNightOut(x.PlannerNotes) == true);
                var cardOpenAcrossMidnight = duties.Any(x => LiveDriverEvidenceRules.SpansUkMidnight(day, x.DutyStartUtc, x.DutyEndUtc));
                var tachoRest = duties.Any(x => x.RestMinutes >= 60 && LiveDriverEvidenceRules.SpansUkMidnight(day, x.DutyStartUtc, x.DutyEndUtc));
                var overnightStart = StartOfDayUtc(day.AddDays(1));
                var overnightEnd = overnightStart.AddHours(9);
                var overnightTracking = dayTracking.Where(x => x.EventTimeUtc >= overnightStart && x.EventTimeUtc <= overnightEnd).OrderBy(x => x.EventTimeUtc).ToList();
                var trackerObservedOvernight = overnightTracking.Count > 0;
                var baseVisit = baseVisits
                    .Where(x => vehicleKeys.Contains(Normalise(x.VehicleIdentifier)) && x.EnteredAtUtc < overnightEnd && (x.ExitedAtUtc == null || x.ExitedAtUtc >= overnightStart))
                    .OrderBy(x => x.EnteredAtUtc)
                    .FirstOrDefault();

                bool? awayFromBase = baseGeofence is null || geofenceError is not null || !trackerObservedOvernight
                    ? null
                    : baseVisit is null;
                var trackerEvidenceUtc = overnightTracking.LastOrDefault()?.EventTimeUtc ?? baseVisit?.EnteredAtUtc;

                if (plannerTick || tachoRest || cardOpenAcrossMidnight || awayFromBase == true)
                {
                    var status = plannerTick && cardOpenAcrossMidnight && awayFromBase == true ? "Confirmed - card remained inserted overnight"
                        : !plannerTick && cardOpenAcrossMidnight && awayFromBase == true ? "Detected - card remained inserted; planner tick missing"
                        : cardOpenAcrossMidnight && awayFromBase is null ? "Review - card remained inserted across midnight; location evidence incomplete"
                        : plannerTick && tachoRest && awayFromBase == true ? "Confirmed"
                        : !plannerTick && tachoRest && awayFromBase == true ? "Detected - planner tick missing"
                        : awayFromBase == false && plannerTick ? "Review - vehicle returned to base"
                        : "Review - evidence incomplete";
                    var sageExpense = string.Equals(employment, "Employed", StringComparison.OrdinalIgnoreCase) && status.StartsWith("Confirmed", StringComparison.OrdinalIgnoreCase)
                        ? "Expected - Sage HR expense reconciliation not yet connected"
                        : "Not yet reconciled";
                    nightRows.Add(new NightOutEvidenceRow(day, driver.Id, driver.DisplayName, employment,
                        dayLoads.Select(x => x.Reference).Distinct().ToArray(), plannerTick, cardOpenAcrossMidnight, tachoRest,
                        duties.Sum(x => x.RestMinutes), trackerEvidenceUtc, awayFromBase, baseVisit?.EnteredAtUtc,
                        baseGeofence?.Name, status, sageExpense));
                }
            }
        }

        return new DriverHoursComplianceReport(
            weekStart, weekEnd, DateTimeOffset.UtcNow,
            new DriverHoursPolicy("Wednesday", "Tuesday",
                "The operating week is Wednesday through Tuesday. A PM run remains attached to its commencement day even when delivery continues after midnight.",
                "A card/open Tacho duty spanning UK midnight is immediate overnight evidence. Overnight rest and tracker/geofence evidence then confirm whether the vehicle remained away from Base. Planner Night out = Yes records intent but is not the sole authority.",
                "Fleetio pre-use evidence remains valid across midnight while the same driver retains control; a driver/vehicle/trailer handover creates a new check requirement."),
            new DriverHoursSourceStatus(
                tachoError is null ? "Available" : $"Partial: {tachoError}",
                trackerError is null ? "Available" : $"Partial: {trackerError}",
                baseGeofence is null ? "Base geofence not resolved" : geofenceError is null ? $"Base geofence: {baseGeofence.Name}" : $"Partial: {geofenceError}",
                "Sage HR employee roster is connected; expense-claim API reconciliation is not yet implemented."),
            nightRows.OrderBy(x => x.Date).ThenBy(x => x.DriverName).ToArray(),
            nonEmployedRows.OrderBy(x => x.Date).ThenBy(x => x.DriverName).ToArray());
    }

    private async Task<SiteGeofence?> TryBaseGeofence(CancellationToken ct)
    {
        try
        {
            var geofences = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
            return geofences.FirstOrDefault(x => Normalise(x.SiteNumber) is "SLH" or "BASE" or "YARD")
                ?? geofences.FirstOrDefault(x => Normalise(x.Name).Contains("STUARTLYONS") || Normalise(x.Name).Contains("LYONSHAULAGE") || Normalise(x.Name) == "BASE");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Base geofence lookup unavailable for night-out confirmation.");
            db.ChangeTracker.Clear();
            return null;
        }
    }

    internal static DateOnly WednesdayWeekStart(DateOnly date)
    {
        var delta = ((int)date.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        return date.AddDays(-delta);
    }

    private static HashSet<string> VehicleKeys(IEnumerable<Load> loads, IEnumerable<TachoDriverDutyStatus> duties, IEnumerable<Vehicle> vehicles)
    {
        var result = duties.Select(x => Normalise(x.VehicleCode)).Where(x => x.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var vehicleById = vehicles.ToDictionary(x => x.Id);
        foreach (var load in loads)
        {
            if (load.VehicleId is not Guid id || !vehicleById.TryGetValue(id, out var vehicle)) continue;
            foreach (var value in new[] { vehicle.Registration, vehicle.Abbreviation, vehicle.FleetNumber })
                if (!string.IsNullOrWhiteSpace(value)) result.Add(Normalise(value));
        }
        return result;
    }

    private static DateTimeOffset? FirstPlannedTime(IEnumerable<Load> loads) => loads.SelectMany(x => x.Stops)
        .Where(x => x.PlannedArrivalUtc is not null).Select(x => x.PlannedArrivalUtc).OrderBy(x => x).FirstOrDefault();

    private static bool DriverMatches(Driver driver, TachoDriverDutyStatus status)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var linked) && linked > 0 && linked == status.MemberCode) return true;
        if (SameCard(driver.TachoCardNumber, status.CardNumber)) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(status.EmployeeNumber)
            && Normalise(driver.EmployeeNumber) == Normalise(status.EmployeeNumber)) return true;
        var names = new[] { driver.TachoName, driver.DisplayName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Normalise(x!)).ToHashSet();
        return names.Contains(Normalise(status.DriverName));
    }

    private static string EmploymentType(Driver driver)
    {
        var value = (driver.DriverType ?? string.Empty).Trim();
        if (value.Contains("agency", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(driver.AgencyName)) return "Agency";
        if (value.Contains("sub", StringComparison.OrdinalIgnoreCase)) return "Subcontractor";
        if (value.Contains("employ", StringComparison.OrdinalIgnoreCase) || value.Contains("permanent", StringComparison.OrdinalIgnoreCase)) return "Employed";
        return string.IsNullOrWhiteSpace(value) ? "Unknown" : value;
    }

    private static bool? ReadNightOut(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var parts = notes.Split(new[] { '|', '·', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var value = parts.FirstOrDefault(x => x.StartsWith("Night out:", StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        return value.EndsWith("Yes", StringComparison.OrdinalIgnoreCase) ? true : value.EndsWith("No", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private static bool IsMovement(VehicleTrackingEvent x) => x.IsMoving == true || x.IgnitionOn == true || (x.SpeedKph ?? 0m) > 0m;
    private static int Minutes(TimeSpan value) => (int)Math.Round(value.TotalMinutes);
    private static DateTimeOffset StartOfDayUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, London.GetUtcOffset(local)).ToUniversalTime();
    }
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool SameCard(string? left, string? right)
    {
        var a = Normalise(left); var b = Normalise(right);
        return a.Length >= 8 && b.Length >= 8 && (a == b || a.EndsWith(b, StringComparison.OrdinalIgnoreCase) || b.EndsWith(a, StringComparison.OrdinalIgnoreCase));
    }
    private static string Csv(string value) => $"\"{(value ?? string.Empty).Replace("\"", "\"\"")}\"";

    public sealed record DriverHoursComplianceReport(DateOnly WeekStart, DateOnly WeekEnd, DateTimeOffset GeneratedAtUtc,
        DriverHoursPolicy Policy, DriverHoursSourceStatus SourceStatus, IReadOnlyList<NightOutEvidenceRow> NightOuts,
        IReadOnlyList<NonEmployedHourRow> NonEmployedHours);
    public sealed record DriverHoursPolicy(string WeekStarts, string WeekEnds, string OperatingDayRule, string NightOutRule, string FleetCheckRule);
    public sealed record DriverHoursSourceStatus(string TachoMaster, string Tracker, string BaseSite, string SageHrExpenses);
    public sealed record NightOutEvidenceRow(DateOnly Date, Guid DriverId, string DriverName, string EmploymentType, IReadOnlyList<string> Runs,
        bool PlannerTicked, bool CardOpenAcrossMidnight, bool TachoRestEvidence, int TachoRestMinutes, DateTimeOffset? TrackerEvidenceUtc, bool? TrackerAwayFromBase,
        DateTimeOffset? BaseReturnAtUtc, string? BaseGeofenceName, string Status, string SageExpenseStatus);
    public sealed record NonEmployedHourRow(DateOnly Date, Guid DriverId, string DriverName, string EmployeeNumber, string EmploymentType,
        string? AgencyName, DateTimeOffset? TachoDutyStartUtc, DateTimeOffset? TachoDutyEndUtc, int? TachoDutySpanMinutes,
        int? TachoActivityMinutes, DateTimeOffset? TrackerFirstMovementUtc, int? CardToFirstMovementMinutes, DateTimeOffset? TrackerLastMovementUtc,
        int? TrackerMovementSpanMinutes, IReadOnlyList<string> TrackerVehicles, IReadOnlyList<string> Runs, int? VarianceMinutes, string EvidenceStatus);
}
