using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/loads"), Authorize]
public sealed class RunDriverMessageController(
    TmsDbContext db,
    DriverSmsDispatchService sms,
    TachoMasterClient tachoMaster,
    DriverWeeklyRestComplianceService weeklyRestCompliance) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeSpan SameDayLiveSignOnWindow = TimeSpan.FromMinutes(30);

    [HttpPost("{id:guid}/dispatch-readiness")]
    public async Task<IActionResult> DispatchReadiness(Guid id, RunDispatchReadinessRequest request, CancellationToken ct)
    {
        var load = await FindLoad(id, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });
        if (load.DriverId is null || load.VehicleId is null) return BadRequest(new { message = "Allocate both a driver and vehicle before dispatch." });

        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        if (driver is null || vehicle is null) return BadRequest(new { message = "The allocated driver or vehicle could not be found." });

        var structural = await new PreDispatchSafetyService(db).EvaluateAsync(id, ct);
        if (structural.Classification == "Blocked")
            return Ok(Blocked(request.RouteDrivingMinutes, 0, StructuralExplanation(structural), structural: structural));
        if (structural.Classification == "Unverified" && !request.AcknowledgeUnverified)
            return Ok(Blocked(request.RouteDrivingMinutes, 0, "Pre-dispatch evidence is incomplete. Review the warnings and explicitly acknowledge them before dispatch.", structural: structural));

        var readiness = await AssessReadiness(load, driver, vehicle, request.RouteDrivingMinutes, actualDispatch: false, ct);
        if (!readiness.CanDispatch && readiness.Status == "Unverified")
        {
            var warningStructural = WithUnverifiedWarning(structural, readiness.Explanation);
            if (!request.AcknowledgeUnverified)
                return Ok(readiness with { StructuralReadiness = warningStructural });

            readiness = Acknowledged(readiness);
            return Ok(readiness with { StructuralReadiness = warningStructural });
        }

        return Ok(readiness with { StructuralReadiness = structural });
    }

    [HttpPost("{id:guid}/driver-message/sms"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Send(Guid id, RunDriverMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message)) return BadRequest(new { message = "The driver message is empty." });
        if (request.Message.Length > 5000) return BadRequest(new { message = "The driver message is too long." });

        // Allocation writes deliberately target the Planning Register when a resilient copy exists.
        // Prefer that copy here too, otherwise a stale dbo.Loads row can make Dispatch believe a
        // successfully allocated run has no driver/vehicle.
        Load? load = null;
        var register = false;
        try
        {
            load = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            register = load is not null;
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        if (load is null)
        {
            try
            {
                load = await db.Loads.Include(item => item.Stops).SingleOrDefaultAsync(item => item.Id == id, ct);
            }
            catch (Exception exception) when (IsSchemaUnavailable(exception))
            {
                db.ChangeTracker.Clear();
            }
        }
        if (load is null) return NotFound(new { message = "The run could not be found." });
        if (load.DriverId is null || load.VehicleId is null) return BadRequest(new { message = "Allocate both a driver and vehicle before sending the driver text." });

        var driver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        if (driver is null) return BadRequest(new { message = "The allocated driver could not be found." });
        if (string.IsNullOrWhiteSpace(driver.MobileNumber)) return BadRequest(new { message = "The assigned driver has no approved mobile number." });

        if (request.Dispatch)
        {
            if (request.RouteDrivingMinutes is not int routeDrivingMinutes || routeDrivingMinutes <= 0)
                return BadRequest(new { message = "The route must be calculated before dispatch so TachoMaster can check the driver's remaining hours." });
            var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
            if (vehicle is null) return BadRequest(new { message = "The allocated vehicle could not be found." });

            var structural = await new PreDispatchSafetyService(db).EvaluateAsync(id, ct);
            if (structural.Classification == "Blocked")
                return BadRequest(new { message = StructuralExplanation(structural), structural });
            if (structural.Classification == "Unverified" && !request.AcknowledgeUnverified)
                return BadRequest(new { message = "Pre-dispatch evidence is incomplete. Review and acknowledge the warnings before dispatch.", structural });

            var readiness = await AssessReadiness(load, driver, vehicle, routeDrivingMinutes, actualDispatch: true, ct);
            if (!readiness.CanDispatch && readiness.Status == "Unverified" && request.AcknowledgeUnverified)
                readiness = Acknowledged(readiness);
            readiness = readiness with { StructuralReadiness = readiness.Status.StartsWith("Unverified", StringComparison.OrdinalIgnoreCase) ? WithUnverifiedWarning(structural, readiness.Explanation) : structural };
            if (!readiness.CanDispatch) return BadRequest(new { message = readiness.Explanation, readiness });
        }

        var receipt = await sms.SendAsync(driver.MobileNumber, request.Message.Trim(), ct);
        if (request.Dispatch && load.Status == LoadStatus.Planned) load.Status = LoadStatus.Dispatched;

        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        else await db.SaveChangesAsync(ct);

        await RecordMessageEvent(load, request, receipt.Provider, receipt.MobileSuffix, receipt.MessageId, ct);

        return Accepted(new
        {
            receipt.MessageId,
            receipt.MobileSuffix,
            receipt.Provider,
            load.Status
        });
    }

    private async Task RecordMessageEvent(Load load, RunDriverMessageRequest request, string? provider, string? mobileSuffix, string? messageId, CancellationToken ct)
    {
        try
        {
            var message = request.Message.Trim().Replace("\r", string.Empty).Replace("\n", " · ");
            if (message.Length > 700) message = message[..700] + "…";
            db.DriverStatusLogs.Add(new DriverStatusLog
            {
                LoadId = load.Id,
                DriverId = load.DriverId,
                Status = request.Dispatch ? "Driver dispatched" : "Driver text update sent",
                Notes = $"{(request.Dispatch ? "Dispatch" : "Plain text update")} sent via {provider ?? "SMS"} to ***{mobileSuffix}. Message ID {messageId ?? "not returned"}. {message}",
                CapturedBy = User.Identity?.Name ?? "TMS planner"
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private Task<Load?> FindLoad(Guid id, CancellationToken ct)
        => PlanningResilience.ReadLoadAsync(db, id, ct);

    private async Task<RunDispatchReadinessResponse> AssessReadiness(
        Load load,
        Driver driver,
        Vehicle vehicle,
        int routeDrivingMinutes,
        bool actualDispatch,
        CancellationToken ct)
    {
        var minutes = Math.Max(1, routeDrivingMinutes);

        await MasterDetailStore.EnrichDriversAsync(db, [driver], ct);
        if (string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) && string.IsNullOrWhiteSpace(driver.TachoCardNumber))
        {
            if (DriverPopulationRules.IsSubcontractor(driver))
                return Unverified(minutes, 0, "This is an external subcontractor and no SLH TachoMaster identity is available. Confirm the subcontractor has independently verified the driver's legal hours, fitness and compliance for this route, then acknowledge this warning to dispatch. The acknowledgement is retained as unverified external-compliance evidence.");
            return Blocked(minutes, 0, "The allocated Driver Master record has no TachoMaster member number or driver card identity. Sync the Driver Master from TachoMaster before dispatch.");
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var ukNow = TimeZoneInfo.ConvertTime(nowUtc, London);
        var ukToday = DateOnly.FromDateTime(ukNow.DateTime);
        var plannedStartUtc = load.Stops
            .OrderBy(item => item.Sequence)
            .Select(item => item.PlannedArrivalUtc)
            .FirstOrDefault(value => value is not null);

        if (plannedStartUtc is null)
        {
            var dispatchState = (await DriverDispatchStateStore.ReadAsync(db, [load.Id], ct)).GetValueOrDefault(load.Id);
            plannedStartUtc = dispatchState?.PlannedStartUtc;
        }

        var weeklyRestReferenceUtc = plannedStartUtc
            ?? (load.PlanningDate == ukToday ? nowUtc : PlanningDayEndUtc(load.PlanningDate));
        var weeklyRest = await weeklyRestCompliance.EvaluateAsync(driver, load.PlanningDate, weeklyRestReferenceUtc, ct);
        if (weeklyRest.IsBlocked)
            return Blocked(minutes, 0, weeklyRest.Message);

        if (!actualDispatch && load.PlanningDate > ukToday)
            return new RunDispatchReadinessResponse(
                true,
                "FutureDuty",
                $"Future duty for {load.PlanningDate:dd/MM/yyyy}: canonical TachoMaster identity, Driver Master compliance, weekly rest and route checks passed. Live card and remaining hours will be revalidated when the duty becomes current.",
                minutes,
                0,
                driver.TachoName ?? driver.DisplayName,
                vehicle.Registration,
                null,
                driver.TachoDriveAvailableTodayMinutes,
                driver.TachoWorkAvailableWeekMinutes);

        if (!actualDispatch && load.PlanningDate == ukToday)
        {
            if (plannedStartUtc is DateTimeOffset plannedStart && plannedStart - nowUtc > SameDayLiveSignOnWindow)
            {
                var plannedLocal = TimeZoneInfo.ConvertTime(plannedStart, London);
                return new RunDispatchReadinessResponse(
                    true,
                    "AwaitingSignOn",
                    $"Driver Master matched ({DriverIdentitySummary(driver)}). Planned start {plannedLocal:HH:mm}. Live Falcon/TachoMaster sign-on is not expected yet; it will be required from 30 minutes before start and again at actual dispatch.",
                    minutes,
                    0,
                    driver.TachoName ?? driver.DisplayName,
                    vehicle.Registration,
                    null,
                    driver.TachoDriveAvailableTodayMinutes,
                    driver.TachoWorkAvailableWeekMinutes);
            }
        }

        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statuses;
        try
        {
            statuses = await tachoMaster.GetLiveDriverStatusesByVehicleAsync(load.PlanningDate, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unverified(minutes, 0, "TachoMaster could not be read live. If you have independently confirmed the driver is fit and legal to start this duty, acknowledge this warning to dispatch; the TMS will retain the warning as unverified evidence.");
        }

        var aliases = await ExecutionIdentityResolver.VehicleAliasesAsync(db, [vehicle], ct);
        var vehicleAliases = aliases.TryGetValue(vehicle.Id, out var knownAliases)
            ? knownAliases
            : ExecutionIdentityResolver.VehicleAliasVariants(vehicle.Registration).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tacho = ExecutionIdentityResolver.MatchLiveDriverIdentityForVehicle(vehicleAliases, driver, statuses);
        if (tacho is null)
            return Unverified(minutes, 0, "The Driver Master identity is present, but no live driver card, Falcon identity or TachoMaster duty is currently attached to this vehicle. If you have confirmed the driver has signed on and is fit and legal to drive, acknowledge this warning to dispatch.");
        if (!ExecutionIdentityResolver.DriverMatches(driver, tacho))
            return Blocked(minutes, 0, $"Live card/driver evidence is present for {tacho.DriverName}, but it does not match the planned driver. Correct the allocation before dispatch.", tacho);

        var breakMinutes = RequiredBreakMinutes(tacho, minutes);
        if (tacho.DriveAvailableTodayMinutes is not int driveAvailable)
            return Unverified(minutes, breakMinutes, $"{IdentitySource(tacho)} confirms {tacho.DriverName}, but remaining drive time is not currently available. If you have independently confirmed sufficient legal hours for this route, acknowledge this warning to dispatch.", tacho);

        if (minutes > driveAvailable)
            return Blocked(minutes, breakMinutes, $"This run needs about {minutes} minutes driving, but TachoMaster shows {driveAvailable} minutes available today for {tacho.DriverName}. Re-plan before dispatch.", tacho);

        var totalDutyMinutes = minutes + breakMinutes;
        if (tacho.WorkAvailableWeekMinutes is int workAvailable && totalDutyMinutes > workAvailable)
            return Blocked(minutes, breakMinutes, $"This run needs about {totalDutyMinutes} minutes including breaks, but TachoMaster shows {workAvailable} working minutes available this week for {tacho.DriverName}. Re-plan before dispatch.", tacho);

        var status = breakMinutes > 0 ? "BreakRequired" : "Ready";
        var explanation = breakMinutes > 0
            ? $"{IdentitySource(tacho)} confirms {tacho.DriverName} has {driveAvailable} driving minutes available. Dispatch can proceed and the ETA includes a {breakMinutes} minute statutory break."
            : $"{IdentitySource(tacho)} confirms {tacho.DriverName} has {driveAvailable} driving minutes available. Dispatch can proceed.";
        return new(true, status, explanation, minutes, breakMinutes, tacho.DriverName, tacho.VehicleCode, tacho.DutyStartUtc, tacho.DriveAvailableTodayMinutes, tacho.WorkAvailableWeekMinutes);
    }

    private static DateTimeOffset PlanningDayEndUtc(DateOnly date)
    {
        var local = date.AddDays(1).ToDateTime(TimeOnly.MinValue).AddTicks(-1);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, London), TimeSpan.Zero);
    }

    private static string DriverIdentitySummary(Driver driver)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(driver.TachoMasterDriverId)) parts.Add($"TachoMaster member {driver.TachoMasterDriverId.Trim()}");
        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber)) parts.Add($"card ending {CardSuffix(driver.TachoCardNumber)}");
        return parts.Count == 0 ? "canonical TachoMaster identity" : string.Join(" · ", parts);
    }

    private static string CardSuffix(string value)
    {
        var clean = new string(value.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length <= 4 ? clean : clean[^4..];
    }

    private static string IdentitySource(TachoVehicleDriverStatus tacho)
        => tacho.EvidenceSource == "FalconLiveCard" ? "Falcon live card evidence with TachoMaster profile metrics" : "TachoMaster";

    private static int RequiredBreakMinutes(TachoVehicleDriverStatus tacho, int routeDrivingMinutes)
    {
        var existingBreakMinutes = tacho.BreakMinutes ?? 0;
        var initialContinuousDriving = existingBreakMinutes >= 45 ? tacho.DriveMinutes % 270 : Math.Min(tacho.DriveMinutes, 270);
        var requiredBreaks = Math.Max(0, (int)Math.Floor((initialContinuousDriving + routeDrivingMinutes - 0.01) / 270d));
        return Math.Max(0, requiredBreaks * 45);
    }

    private static string StructuralExplanation(PreDispatchReadinessResult readiness)
    {
        var failures = readiness.Checks.Where(item => !item.Passed).Select(item => item.Message).Take(4).ToList();
        return failures.Count == 0
            ? "Pre-dispatch validation did not pass."
            : string.Join(" ", failures);
    }

    private static PreDispatchReadinessResult WithUnverifiedWarning(PreDispatchReadinessResult structural, string warning)
    {
        var checks = structural.Checks
            .Concat([new PreDispatchCheck("LiveTachoEvidence", false, "Warning", warning)])
            .ToList();
        return structural with
        {
            Classification = "Unverified",
            CanDispatch = true,
            RequiresAcknowledgement = true,
            Checks = checks
        };
    }

    private static RunDispatchReadinessResponse Acknowledged(RunDispatchReadinessResponse readiness)
        => readiness with
        {
            CanDispatch = true,
            Status = "UnverifiedAcknowledged",
            Explanation = $"Planner acknowledged incomplete live Tacho/Falcon evidence. {readiness.Explanation}"
        };

    private static RunDispatchReadinessResponse Unverified(int routeDrivingMinutes, int breakMinutes, string explanation, TachoVehicleDriverStatus? tacho = null)
        => new(false, "Unverified", explanation, routeDrivingMinutes, breakMinutes, tacho?.DriverName, tacho?.VehicleCode, tacho?.DutyStartUtc, tacho?.DriveAvailableTodayMinutes, tacho?.WorkAvailableWeekMinutes);

    private static RunDispatchReadinessResponse Blocked(int routeDrivingMinutes, int breakMinutes, string explanation, TachoVehicleDriverStatus? tacho = null, PreDispatchReadinessResult? structural = null)
        => new(false, "Blocked", explanation, routeDrivingMinutes, breakMinutes, tacho?.DriverName, tacho?.VehicleCode, tacho?.DutyStartUtc, tacho?.DriveAvailableTodayMinutes, tacho?.WorkAvailableWeekMinutes, structural);
}

public sealed record RunDriverMessageRequest(string Message, bool Dispatch = false, int? RouteDrivingMinutes = null, bool AcknowledgeUnverified = false);
public sealed record RunDispatchReadinessRequest(int RouteDrivingMinutes, bool AcknowledgeUnverified = false);
public sealed record RunDispatchReadinessResponse(bool CanDispatch, string Status, string Explanation, int RouteDrivingMinutes, int BreakMinutesIncluded, string? TachoDriverName, string? TachoVehicleCode, DateTimeOffset? DutyStartUtc, int? DriveAvailableTodayMinutes, int? WorkAvailableWeekMinutes, PreDispatchReadinessResult? StructuralReadiness = null);
