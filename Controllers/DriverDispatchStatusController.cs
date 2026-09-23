using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-dispatch-status"), Authorize]
public sealed class DriverDispatchStatusController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    ILogger<DriverDispatchStatusController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly LoadStatus[] ExecutedStatuses = [LoadStatus.Dispatched, LoadStatus.InProgress, LoadStatus.Completed];

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var drivers = await db.Drivers.Where(item => item.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(item => item.Status != LoadStatus.Cancelled)
            .ToList();
        var loadIds = loads.Select(item => item.Id).ToList();
        IReadOnlyList<DriverStatusLog> logs = loadIds.Count == 0
            ? Array.Empty<DriverStatusLog>()
            : await db.DriverStatusLogs
                .AsNoTracking()
                .Where(item => item.LoadId != Guid.Empty && loadIds.Contains(item.LoadId))
                .OrderByDescending(item => item.CapturedAtUtc)
                .ToListAsync(ct);

        // Live movement is deliberately optional enrichment. Persisted run state remains usable if
        // the tracking table or vehicle aliases are temporarily unavailable.
        var vehicleAliases = new Dictionary<Guid, HashSet<string>>();
        var liveStatuses = new List<VehicleLiveStatus>();
        if (planningDate == today)
        {
            try
            {
                var assignedVehicleIds = loads
                    .Where(item => item.VehicleId is not null)
                    .Select(item => item.VehicleId!.Value)
                    .Distinct()
                    .ToList();
                if (assignedVehicleIds.Count > 0)
                {
                    var assignedVehicles = await db.Vehicles.AsNoTracking()
                        .Where(item => assignedVehicleIds.Contains(item.Id))
                        .ToListAsync(ct);
                    vehicleAliases = await ExecutionIdentityResolver.VehicleAliasesAsync(db, assignedVehicles, ct);
                    liveStatuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning(exception, "Driver Dispatch live movement evidence was unavailable for {PlanningDate}; persisted run status will be used.", planningDate);
            }
        }

        var historyStart = planningDate.AddDays(-7);
        var recentActivity = await db.Loads.AsNoTracking()
            .Where(item => item.DriverId != null && item.PlanningDate >= historyStart && item.PlanningDate < planningDate && ExecutedStatuses.Contains(item.Status))
            .Select(item => new { DriverId = item.DriverId!.Value, item.PlanningDate })
            .ToListAsync(ct);

        var dutyBuffer = new List<TachoDriverDutyStatus>();
        if (tachoMaster.IsConfigured)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                var throughDate = planningDate < today ? planningDate : today;
                var fromDate = throughDate.AddDays(-8);
                for (var dutyDate = fromDate; dutyDate <= throughDate; dutyDate = dutyDate.AddDays(1))
                    dutyBuffer.AddRange(await tachoMaster.GetDriverDutyStatusesAsync(dutyDate, timeout.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Keep any duty days already returned before the timeout. Discarding the partial
                // history could incorrectly reset a Monday/Tuesday driver to Day 1 on Wednesday.
                logger.LogWarning("TachoMaster exceeded the Driver Dispatch status budget for {PlanningDate}; retaining {DutyCount} duty rows already returned.", planningDate, dutyBuffer.Count);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "TachoMaster weekly-rest history was partially unavailable for Driver Dispatch status on {PlanningDate}; retaining {DutyCount} duty rows.", planningDate, dutyBuffer.Count);
            }
        }
        IReadOnlyList<TachoDriverDutyStatus> duties = dutyBuffer;

        var result = drivers.Select(driver =>
        {
            var load = loads
                .Where(item => item.DriverId == driver.Id)
                .OrderByDescending(item => item.CreatedAtUtc)
                .FirstOrDefault();
            IReadOnlyList<DriverStatusLog> loadLogs = load is null
                ? Array.Empty<DriverStatusLog>()
                : logs.Where(item => item.LoadId == load.Id).OrderByDescending(item => item.CapturedAtUtc).ToList();

            // Driver confirmation is messaging evidence, not the run's operational state. Keep it
            // separate so the future inbound-SMS automation can set confirmation without changing
            // Dispatched/Working/Completed.
            var latestDispatch = loadLogs.FirstOrDefault(item => item.Status == "Driver dispatched");
            var latestInbound = latestDispatch is null
                ? null
                : loadLogs.FirstOrDefault(item => item.Status == "Driver response received" && item.CapturedAtUtc > latestDispatch.CapturedAtUtc);
            var dispatchWasSent = latestDispatch is not null || load?.Status is LoadStatus.Dispatched or LoadStatus.InProgress or LoadStatus.Completed;
            var dispatchStatus = load is null
                ? "No Run"
                : latestInbound is not null
                    ? "Confirmed"
                    : dispatchWasSent
                        ? "Sent Awaiting Response"
                        : "Awaiting Dispatch";

            var matchedDuties = duties
                .Where(item => DriverDayCycleCalculator.MatchesDriver(driver, item))
                .OrderByDescending(item => item.MetricsValidAtUtc ?? item.DutyStartUtc)
                .ThenByDescending(item => item.DutyStartUtc)
                .ToList();
            var latestDuty = matchedDuties.FirstOrDefault();
            var tachoProjectedDay = matchedDuties.Count == 0
                ? (int?)null
                : DriverDayCycleCalculator.Calculate(planningDate, matchedDuties);

            var referenceUtc = planningDate <= today
                ? DateTimeOffset.UtcNow
                : ProjectedPlanningReferenceUtc(planningDate, matchedDuties);
            var weekly = DriverWeeklyRestComplianceService.Evaluate(driver, referenceUtc, duties);

            var executedDates = recentActivity
                .Where(item => item.DriverId == driver.Id)
                .Select(item => item.PlanningDate);
            var projectedDayNumber = ReconcileProjectedDay(
                planningDate,
                tachoProjectedDay,
                matchedDuties.Count,
                executedDates,
                weekly.LastWeeklyRestEndUtc);

            var driveAvailablePlanningDayMinutes = planningDate == today
                ? latestDuty?.DriveAvailableTodayMinutes ?? driver.TachoDriveAvailableTodayMinutes
                : planningDate == today.AddDays(1)
                    ? latestDuty?.DriveAvailableTomorrowMinutes
                    : null;
            var workAvailableWeekMinutes = latestDuty?.WorkAvailableWeekMinutes ?? driver.TachoWorkAvailableWeekMinutes;
            var earliest = EarliestPlanningStart(planningDate, today, latestDuty);
            var availability = Availability(weekly, driveAvailablePlanningDayMinutes, workAvailableWeekMinutes, planningDate, today, tachoMaster.IsConfigured, projectedDayNumber);

            var liveWorking = load is not null && HasLiveWorkingEvidence(
                load,
                driver,
                planningDate,
                today,
                vehicleAliases,
                liveStatuses,
                matchedDuties,
                latestDispatch?.CapturedAtUtc);
            var operationalStatus = load is null
                ? "No Run"
                : load.Status == LoadStatus.Completed
                    ? "Completed"
                    : load.Status == LoadStatus.InProgress || liveWorking
                        ? "Working"
                        : load.Status == LoadStatus.Dispatched || latestDispatch is not null
                            ? "Dispatched"
                            : "Awaiting Dispatch";

            return new DriverDispatchStatusRow(
                driver.Id,
                dispatchStatus,
                operationalStatus,
                latestInbound is not null,
                latestInbound?.CapturedAtUtc,
                latestInbound?.Notes,
                latestInbound?.CapturedAtUtc,
                latestDispatch?.CapturedAtUtc,
                weekly.Status,
                weekly.Message,
                weekly.WeeklyRestDueUtc,
                weekly.LastWeeklyRestEndUtc,
                availability.Status,
                availability.Message,
                driveAvailablePlanningDayMinutes,
                workAvailableWeekMinutes,
                projectedDayNumber,
                earliest.StartUtc,
                earliest.Source,
                earliest.IsAssumption);
        }).ToList();

        return Ok(new { planningDate, drivers = result });
    }

    internal static int ReconcileProjectedDay(
        DateOnly planningDate,
        int? tachoProjectedDay,
        int matchedDutyCount,
        IEnumerable<DateOnly> executedTmsDates,
        DateTimeOffset? lastWeeklyRestEndUtc)
    {
        var cycleResetDate = lastWeeklyRestEndUtc is DateTimeOffset restEnd
            ? LondonDate(restEnd)
            : (DateOnly?)null;
        var tmsDates = executedTmsDates
            .Where(day => cycleResetDate is null || day >= cycleResetDate.Value)
            .ToHashSet();
        var tmsConsecutiveDays = 0;
        for (var day = planningDate.AddDays(-1); tmsConsecutiveDays < 7 && tmsDates.Contains(day); day = day.AddDays(-1))
            tmsConsecutiveDays++;
        var tmsProjectedDay = Math.Clamp(tmsConsecutiveDays + 1, 1, 7);

        // Tacho is authoritative when it has enough duty periods to establish the cycle. When the
        // provider only returns a current/profile row, executed TMS history remains a conservative
        // cross-check — but never across a qualifying weekly rest that Tacho has already proven.
        return matchedDutyCount >= 2
            ? tachoProjectedDay ?? tmsProjectedDay
            : Math.Max(tachoProjectedDay ?? 1, tmsProjectedDay);
    }

    private static bool HasLiveWorkingEvidence(
        Load load,
        Driver driver,
        DateOnly planningDate,
        DateOnly today,
        IReadOnlyDictionary<Guid, HashSet<string>> vehicleAliases,
        IReadOnlyList<VehicleLiveStatus> liveStatuses,
        IReadOnlyList<TachoDriverDutyStatus> matchedDuties,
        DateTimeOffset? dispatchSentAtUtc)
    {
        if (planningDate != today || load.VehicleId is not Guid vehicleId) return false;
        if (!vehicleAliases.TryGetValue(vehicleId, out var aliases) || aliases.Count == 0) return false;

        var live = ExecutionIdentityResolver.MatchLive(aliases, liveStatuses);
        if (live is null) return false;
        var now = DateTimeOffset.UtcNow;
        if (live.LastEventTimeUtc > now.AddMinutes(5) || now - live.LastEventTimeUtc > TimeSpan.FromMinutes(30)) return false;
        if (live.IsMoving != true && live.SpeedKph.GetValueOrDefault() <= 3) return false;
        if (dispatchSentAtUtc is DateTimeOffset sentAt && live.LastEventTimeUtc < sentAt) return false;

        // "Working" needs both sides of the evidence requested by Operations: movement from DOT
        // and a live card/duty identity for the allocated driver in the allocated vehicle.
        if (CardsMatch(driver.TachoCardNumber, live.CurrentDriverCardNumber)) return true;
        var recentDutyFloor = now.AddHours(-24);
        return matchedDuties.Any(duty =>
            duty.DutyEndUtc is null &&
            duty.DutyStartUtc >= recentDutyFloor &&
            ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, duty.VehicleCode));
    }

    private static bool CardsMatch(string? left, string? right)
    {
        var a = ExecutionIdentityResolver.NormaliseVehicle(left);
        var b = ExecutionIdentityResolver.NormaliseVehicle(right);
        if (a.Length < 8 || b.Length < 8) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
               a.EndsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.EndsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    private static DriverAvailability Availability(
        WeeklyRestComplianceResult weekly,
        int? driveAvailablePlanningDayMinutes,
        int? workAvailableWeekMinutes,
        DateOnly planningDate,
        DateOnly today,
        bool tachoConfigured,
        int? projectedDayNumber)
    {
        if (weekly.Status == "Overdue")
        {
            if (projectedDayNumber is >= 1 and <= 6)
                return new("Unverified", $"TachoMaster weekly-rest history contains overdue evidence, but the current projected duty cycle is Day {projectedDayNumber}. Keep the historic event for compliance review; do not hard-block the current allocation from that older event alone.");
            return new("Unavailable", weekly.Message);
        }

        // A prospective Day 7 must remain a hard planning exception. The driver needs a qualifying
        // weekly rest before another duty can be treated as part of a fresh cycle.
        if (projectedDayNumber is >= 7)
            return new("Unavailable", "Projected Day 7. A qualifying weekly rest is required before another duty can be allocated.");

        if (!tachoConfigured)
            return new("Unverified", "TachoMaster is unavailable, so availability cannot be verified until final dispatch.");

        if (weekly.Status is "Unverified" or "Unknown")
            return new("Unverified", weekly.Message);

        if (planningDate <= today)
        {
            if (driveAvailablePlanningDayMinutes is <= 0)
                return new("Unavailable", "TachoMaster shows no driving time available for this planning day.");
            if (workAvailableWeekMinutes is <= 0)
                return new("Unavailable", "TachoMaster shows no working time available for the current week.");
        }
        else
        {
            // Tomorrow is a planning decision, not a live dispatch decision. TachoMaster's current
            // profile can report 0/null tomorrow allowance while today's duty is still open. Keep
            // that visible as a warning, but do not prevent the planner allocating the run. The
            // actual dispatch continues to validate live hours after the full 11-hour SLH rest.
            if (driveAvailablePlanningDayMinutes is <= 0)
                return new("Unverified", "Tomorrow driving allowance is not yet confirmed by the live Tacho profile. Allocation is allowed for planning; final dispatch will re-check live hours after the full 11h regular rest.");
            if (workAvailableWeekMinutes is <= 0)
                return new("Unverified", "Current Tacho profile does not confirm remaining weekly work for the future planning day. Allocation is allowed for planning; final dispatch will re-check before release.");
        }

        if (planningDate <= today.AddDays(1) && driveAvailablePlanningDayMinutes is null)
            return new("Unverified", "TachoMaster did not return planning-day driving availability. Allocation can be planned, but final dispatch will re-check live hours.");

        return new("Available", weekly.Status == "DueSoon"
            ? $"Available for planning, but weekly rest is due soon. {weekly.Message}"
            : "TachoMaster shows the driver as available for planning. Final dispatch still validates the selected route against live remaining hours.");
    }

    private static DateTimeOffset ProjectedPlanningReferenceUtc(DateOnly planningDate, IReadOnlyList<TachoDriverDutyStatus> matchedDuties)
    {
        var previous = matchedDuties
            .Where(item => LondonDate(item.DutyStartUtc) < planningDate)
            .OrderByDescending(item => item.DutyStartUtc)
            .FirstOrDefault();
        var localTime = previous is null
            ? TimeOnly.MinValue
            : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(previous.DutyStartUtc, London).DateTime);
        return ToUtc(planningDate.ToDateTime(localTime));
    }

    private static EarliestStartEvidence EarliestPlanningStart(DateOnly planningDate, DateOnly today, TachoDriverDutyStatus? latestDuty)
    {
        if (planningDate <= today || latestDuty is null)
            return EarliestStartEvidence.Empty;

        var planningFloor = ToUtc(planningDate.ToDateTime(TimeOnly.MinValue));
        if (latestDuty.DutyEndUtc is DateTimeOffset dutyEnd)
        {
            var start = dutyEnd.AddHours(11);
            if (start < planningFloor) start = planningFloor;
            return new(start, $"Tacho duty ended {LocalTime(dutyEnd):dd/MM HH:mm}; planning start uses 11h regular daily rest. Reduced daily rest is not used for planning.", false);
        }

        var now = DateTimeOffset.UtcNow;
        var assumedDutyEnd = latestDuty.DutyStartUtc.AddHours(13);
        if (assumedDutyEnd < now) assumedDutyEnd = now;
        var assumedStart = assumedDutyEnd.AddHours(11);
        if (assumedStart < planningFloor) assumedStart = planningFloor;
        return new(assumedStart, $"ASSUMPTION · today's Tacho duty is still open. Using assumed duty end {LocalTime(assumedDutyEnd):dd/MM HH:mm} then 11h regular daily rest. Recalculate when the duty closes.", true);
    }

    private static DateOnly LondonDate(DateTimeOffset value)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, London).DateTime);

    private static DateTimeOffset LocalTime(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, London);

    private static DateTimeOffset ToUtc(DateTime local)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, London), TimeSpan.Zero);
    }

    private sealed record DriverAvailability(string Status, string Message);
    private sealed record EarliestStartEvidence(DateTimeOffset? StartUtc, string? Source, bool IsAssumption)
    {
        public static EarliestStartEvidence Empty { get; } = new(null, null, false);
    }
}

public sealed record DriverDispatchStatusRow(
    Guid DriverId,
    string DispatchStatus,
    string OperationalStatus,
    bool DriverConfirmed,
    DateTimeOffset? DriverConfirmationAtUtc,
    string? LastDriverReply,
    DateTimeOffset? LastDriverReplyAtUtc,
    DateTimeOffset? LastDispatchSentAtUtc,
    string WeeklyRestStatus,
    string WeeklyRestMessage,
    DateTimeOffset? WeeklyRestDueUtc,
    DateTimeOffset? LastWeeklyRestEndUtc,
    string AvailabilityStatus,
    string AvailabilityMessage,
    int? DriveAvailablePlanningDayMinutes,
    int? WorkAvailableWeekMinutes,
    int? ProjectedDayNumber,
    DateTimeOffset? EarliestStartUtc,
    string? EarliestStartSource,
    bool EarliestStartIsAssumption);
