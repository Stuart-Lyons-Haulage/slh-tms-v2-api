using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DispatchService(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    SageHrClient sageHr,
    DispatchOptions options,
    ILogger<DispatchService> logger)
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private const string DriverDetailType = "masterdetail:driver";

    public async Task<IReadOnlyList<DispatchDriverDto>> GetDriversAsync(DateOnly planningDate, CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking().Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        // TmsDbContext is scoped and must not execute concurrent EF operations.
        // Keep these reads sequential so Dispatch cannot intermittently fail with
        // "A second operation was started on this context" under load.
        var profiles = await ReadDriverMasterProfilesAsync(drivers, ct);
        var duties = await ReadDutiesAsync(planningDate, ct);
        var runProfiles = await ReadRunProfilesAsync(planningDate, ct);
        var liveStatuses = await ReadLiveStatusesAsync(ct);
        var history = await ReadRecentHistoryAsync(planningDate, ct);
        var signOffTracking = await ReadTachoSignOffTrackingAsync(duties, ct);
        var availabilitySnapshot = await DriverAvailabilityService.ReadAsync(db, planningDate, ct, sageHr, logger);
        var availabilityByDriver = availabilitySnapshot.Drivers.ToDictionary(item => item.DriverId);
        var today = LondonDate(DateTimeOffset.UtcNow);
        var activityReferenceDate = planningDate > today ? today : planningDate;

        var rows = new List<DispatchDriverDto>(drivers.Count);
        foreach (var driver in drivers)
        {
            var profile = profiles.GetValueOrDefault(driver.Id) ?? DriverMasterProfile.Fallback(driver);
            var driverDuties = duties.Where(duty => DriverDayCycleCalculator.MatchesDriver(driver, duty)).ToList();
            var day = DriverDayCycleCalculator.Calculate(planningDate, driverDuties);
            var rest = DispatchTachoRules.DeriveRequiredRestPeriod(driver, driverDuties);
            var shiftEnd = DispatchTachoRules.LatestShiftEndUtc(driver, driverDuties);
            var lastVehicle = DispatchTachoRules.LastVehicleRegistration(driver, driverDuties);
            var completedVehicle = DispatchTachoRules.LastCompletedVehicleRegistration(driver, driverDuties);
            var reducedRestAvailable = DispatchTachoRules.ReducedDailyRestAvailable(driver, driverDuties);
            var weeklyWorking = DispatchTachoRules.WeeklyWorkingTimeHours(driver, driverDuties, activityReferenceDate);
            var dailyDriving = DispatchTachoRules.DailyDrivingTimeHours(driver, driverDuties, activityReferenceDate);
            var breakCompliant = DispatchTachoRules.BreakCompliant(driver, driverDuties, activityReferenceDate);
            var dailyDrivingLimit = DispatchTachoRules.DailyDrivingLimitMinutes(driver, driverDuties);
            var driveAvailable = DispatchTachoRules.DriveAvailablePlanningDayMinutes(driver, driverDuties, planningDate, today);
            var workAvailable = DispatchTachoRules.WorkAvailableWeekMinutes(driver, driverDuties);
            var skills = DispatchSkillRules.Parse(profile.SkillsText ?? driver.Skills);

            var hasOpenDuty = driverDuties.Any(duty => duty.DutyEndUtc is null);
            var live = MatchLiveDriver(driver, liveStatuses, hasOpenDuty, shiftEnd);
            var signOff = live is null
                ? DispatchLocationRules.ResolveTachoSignOffPosition(completedVehicle, shiftEnd, signOffTracking)
                : null;
            var previous = history.Where(load => load.DriverId == driver.Id)
                .OrderByDescending(load => load.PlanningDate)
                .ThenByDescending(load => load.CreatedAtUtc)
                .FirstOrDefault();
            var previousFinal = previous is null ? null : OperationalStopOrdering.Order(previous.Stops).LastOrDefault();
            var latitude = live?.Latitude ?? signOff?.Latitude ?? previousFinal?.Latitude;
            var longitude = live?.Longitude ?? signOff?.Longitude ?? previousFinal?.Longitude;
            var locationName = live is not null
                ? HumanLocation(live.LastKnownStatus) ?? $"Live GPS · {live.VehicleIdentifier}"
                : signOff?.Label ?? previousFinal?.Name;
            var positionAtUtc = live?.LastEventTimeUtc ?? signOff?.AtUtc;

            var onHoliday = profile.HolidayDates.Contains(planningDate);
            var offContract = IsHalfTramper(profile.EmploymentType) &&
                              profile.ContractedDays.Count > 0 &&
                              !profile.ContractedDays.Contains(planningDate.DayOfWeek);
            availabilityByDriver.TryGetValue(driver.Id, out var sharedAvailability);
            var tachoBlockReason = planningDate <= today
                ? TachoAvailabilityBlockReason(driveAvailable ?? 0, workAvailable ?? 0, weeklyWorking, dailyDriving, dailyDrivingLimit)
                : null;
            // An existing allocation is not a blocker for the driver's own row:
            // it must remain actionable so planners can prepare/export the route
            // already assigned to that driver. The run-owner rules still prevent
            // the same route being selected for a different driver.
            var actionableBlockReasons = sharedAvailability?.BlockReasons
                .Where(reason => !reason.StartsWith("Already allocated", StringComparison.Ordinal))
                .ToArray() ?? [];
            var blockedReason = actionableBlockReasons.Length > 0
                ? string.Join(" · ", actionableBlockReasons)
                : onHoliday ? "Annual leave" : offContract ? "Not contracted tomorrow" : tachoBlockReason;
            var needsReturn = DispatchReturnRules.NeedsReturn(day, latitude, options.NorthernLatitudeThreshold);
            var suggestion = blockedReason is null
                ? ChooseSuggestion(driver.Id, skills, needsReturn, latitude, longitude, runProfiles)
                : DispatchSuggestion.None;

            rows.Add(new DispatchDriverDto(
                driver.Id,
                profile.DriverCode ?? driver.EmployeeNumber,
                driver.DisplayName,
                sharedAvailability?.EmploymentType ?? DriverAvailabilityService.CanonicalEmploymentType(profile.EmploymentType ?? driver.DriverType, driver.DriverGroup),
                skills,
                profile.HolidayDates,
                profile.ContractedDays,
                profile.HomeDepot,
                new DispatchTachoDataDto(
                    day,
                    shiftEnd,
                    weeklyWorking,
                    dailyDriving,
                    breakCompliant,
                    lastVehicle,
                    rest.Hours,
                    rest.ReducedDailyRestsUsed,
                    dailyDrivingLimit,
                    driveAvailable,
                    workAvailable,
                    reducedRestAvailable),
                new DispatchTrackingDataDto(
                    latitude is decimal lat && longitude is decimal lon ? new DispatchGeoPointDto(lat, lon) : null,
                    CleanStopName(locationName),
                    positionAtUtc),
                needsReturn,
                DispatchTachoRules.AvailableFrom(shiftEnd, rest),
                blockedReason is not null,
                blockedReason,
                suggestion.RunId,
                suggestion.Reference,
                suggestion.DistanceMiles,
                suggestion.IsBackload,
                suggestion.DeadheadReductionMiles,
                blockedReason ?? suggestion.Message,
                suggestion.Score,
                suggestion.Reasons));
        }

        return rows
            .OrderBy(row => row.IsBlocked)
            .ThenByDescending(row => row.NeedsReturn)
            .ThenBy(row => row.Name)
            .ToArray();
    }

    public async Task<IReadOnlyList<DispatchRunDto>> GetRunsAsync(DateOnly planningDate, CancellationToken ct) =>
        (await ReadRunProfilesAsync(planningDate, ct)).Select(profile => profile.Dto).ToArray();

    public async Task<IReadOnlyList<DispatchAvailableTimeDto>> GetAvailableTimesAsync(
        DispatchAvailableTimesRequest request,
        CancellationToken ct)
    {
        var ids = request.DriverIds.Distinct().ToArray();
        if (ids.Length == 0) return [];
        var reducedRestDriverIds = request.ReducedRestDriverIds?.ToHashSet() ?? new HashSet<Guid>();

        var drivers = await db.Drivers.AsNoTracking().Where(driver => driver.Active && ids.Contains(driver.Id)).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        // Do not parallelise EF-backed reads on the scoped TmsDbContext.
        var profiles = await ReadDriverMasterProfilesAsync(drivers, ct);
        var duties = await ReadDutiesAsync(request.PlanningDate, ct);
        var availabilitySnapshot = await DriverAvailabilityService.ReadAsync(db, request.PlanningDate, ct, sageHr, logger);
        var availabilityByDriver = availabilitySnapshot.Drivers.ToDictionary(item => item.DriverId);
        var today = LondonDate(DateTimeOffset.UtcNow);
        var referenceDate = request.PlanningDate > today ? today : request.PlanningDate;

        var result = new List<DispatchAvailableTimeDto>(drivers.Count);
        foreach (var driver in drivers)
        {
            var profile = profiles.GetValueOrDefault(driver.Id) ?? DriverMasterProfile.Fallback(driver);
            var driverDuties = duties.Where(duty => DriverDayCycleCalculator.MatchesDriver(driver, duty)).ToList();
            var useReducedRest = reducedRestDriverIds.Contains(driver.Id);
            var rest = DispatchTachoRules.DeriveRequiredRestPeriod(driver, driverDuties, useReducedRest);
            var shiftEnd = DispatchTachoRules.LatestShiftEndUtc(driver, driverDuties);
            var weeklyWorking = DispatchTachoRules.WeeklyWorkingTimeHours(driver, driverDuties, referenceDate);
            var dailyDriving = DispatchTachoRules.DailyDrivingTimeHours(driver, driverDuties, referenceDate);
            var breakCompliant = DispatchTachoRules.BreakCompliant(driver, driverDuties, referenceDate);
            var dailyLimitHours = DispatchTachoRules.DailyDrivingLimitMinutes(driver, driverDuties) / 60m;
            var availableFrom = DispatchTachoRules.AvailableFrom(shiftEnd, rest);

            availabilityByDriver.TryGetValue(driver.Id, out var sharedAvailability);
            string? breach = sharedAvailability is { Dispatchable: false }
                ? string.Join(" · ", sharedAvailability.BlockReasons)
                : null;
            if (breach is null)
            {
                if (useReducedRest && rest.Hours != 9)
                    breach = "Reduced 9h daily rest is not available; the statutory reduced-rest allowance is exhausted or cannot be evidenced.";
                else if (profile.HolidayDates.Contains(request.PlanningDate)) breach = "Annual leave";
                else if (IsHalfTramper(profile.EmploymentType) && profile.ContractedDays.Count > 0 && !profile.ContractedDays.Contains(request.PlanningDate.DayOfWeek))
                    breach = "Not contracted tomorrow";
                else if (!breakCompliant) breach = "Break compliance breach — required Tacho break is not evidenced.";
                else if (weeklyWorking >= 60m) breach = "WTD breach — 60-hour weekly limit already reached.";
                else if (request.PlanningDate <= today && dailyDriving >= dailyLimitHours) breach = "Daily driving limit already reached.";
                else if (shiftEnd is null) breach = "No completed TachoMaster duty is available to anchor the legal rest calculation.";
            }

            result.Add(new DispatchAvailableTimeDto(
                driver.Id,
                availableFrom,
                rest.Hours,
                weeklyWorking,
                dailyDriving,
                DispatchTachoRules.WtdStatus(weeklyWorking),
                breach));
        }

        return ids
            .Select(id => result.FirstOrDefault(item => item.DriverId == id) ??
                          new DispatchAvailableTimeDto(id, null, 11, 0, 0, "red", "Driver is not active or could not be found."))
            .ToArray();
    }

    public async Task<DispatchLockResponse> LockAsync(DispatchLockRequest request, string? actor, CancellationToken ct)
    {
        if (request.Allocations.Count == 0)
            return new DispatchLockResponse(false, [new DispatchLockFailure(Guid.Empty, null, "No allocations were supplied to lock.")]);

        var failures = new List<DispatchLockFailure>();
        foreach (var duplicate in request.Allocations.GroupBy(item => item.DriverId).Where(group => group.Count() > 1))
            failures.Add(new DispatchLockFailure(duplicate.Key, null, "The same driver appears more than once in the lock request."));
        foreach (var duplicate in request.Allocations.GroupBy(item => item.RunId).Where(group => group.Count() > 1))
            foreach (var allocation in duplicate)
                failures.Add(new DispatchLockFailure(allocation.DriverId, allocation.RunId, "The same run appears more than once in the lock request."));
        if (failures.Count > 0) return new DispatchLockResponse(false, failures);

        var driverIds = request.Allocations.Select(item => item.DriverId).Distinct().ToArray();
        var vehicleIds = request.Allocations.Select(item => item.VehicleId).Distinct().ToArray();
        var trailerIds = request.Allocations.Where(item => item.TrailerId is not null).Select(item => item.TrailerId!.Value).Distinct().ToArray();

        var drivers = await db.Drivers.AsNoTracking().Where(driver => driver.Active && driverIds.Contains(driver.Id)).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        // Lock is an operational write path: every EF-backed read must complete before
        // another query starts on this scoped DbContext. Parallel Task.WhenAll here
        // caused the live HTTP 500 seen when Dispatch attempted a single-row lock.
        var profiles = await ReadDriverMasterProfilesAsync(drivers, ct);
        var duties = await ReadDutiesAsync(request.PlanningDate, ct);
        var availabilitySnapshot = await DriverAvailabilityService.ReadAsync(db, request.PlanningDate, ct, sageHr, logger);
        var availabilityByDriver = availabilitySnapshot.Drivers.ToDictionary(item => item.DriverId);
        var runProfiles = await ReadRunProfilesAsync(request.PlanningDate, ct);
        var vehicleById = await db.Vehicles.AsNoTracking()
            .Where(vehicle => vehicle.Active && vehicleIds.Contains(vehicle.Id))
            .ToDictionaryAsync(vehicle => vehicle.Id, ct);
        var trailerById = await db.Trailers.AsNoTracking()
            .Where(trailer => trailer.Active && trailerIds.Contains(trailer.Id))
            .ToDictionaryAsync(trailer => trailer.Id, ct);
        var runs = runProfiles.ToDictionary(profile => profile.Dto.RunId);
        var driverById = drivers.ToDictionary(driver => driver.Id);
        var today = LondonDate(DateTimeOffset.UtcNow);
        var referenceDate = request.PlanningDate > today ? today : request.PlanningDate;

        foreach (var allocation in request.Allocations)
        {
            if (!driverById.TryGetValue(allocation.DriverId, out var driver))
            {
                failures.Add(new DispatchLockFailure(allocation.DriverId, allocation.RunId, "Driver is not active or could not be found."));
                continue;
            }
            if (!runs.TryGetValue(allocation.RunId, out var run))
            {
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, "Run is not available on the requested planning date."));
                continue;
            }
            if (!availabilityByDriver.TryGetValue(driver.Id, out var sharedAvailability) || !sharedAvailability.Dispatchable)
            {
                var reason = sharedAvailability is null ? "No shared Driver Availability decision is available." : string.Join(" · ", sharedAvailability.BlockReasons);
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{driver.DisplayName} is not dispatchable: {reason}"));
            }
            else if (sharedAvailability.EmploymentType is "Agency" or "Casual" &&
                     (sharedAvailability.AvailableFromUtc is null || sharedAvailability.AvailableUntilUtc is null ||
                      allocation.PlannedStartTime < sharedAvailability.AvailableFromUtc || allocation.PlannedStartTime >= sharedAvailability.AvailableUntilUtc))
            {
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId,
                    $"{driver.DisplayName}'s confirmed availability window does not cover the planned start time."));
            }
            if (!vehicleById.ContainsKey(allocation.VehicleId))
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, "Vehicle is not active or could not be found."));
            if (allocation.TrailerId is Guid requestedTrailer && !trailerById.ContainsKey(requestedTrailer))
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, "Trailer is not active or could not be found."));

            var profile = profiles.GetValueOrDefault(driver.Id) ?? DriverMasterProfile.Fallback(driver);
            if (profile.HolidayDates.Contains(request.PlanningDate))
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{driver.DisplayName} is on annual leave on {request.PlanningDate:dd/MM/yyyy}."));
            if (IsHalfTramper(profile.EmploymentType) && profile.ContractedDays.Count > 0 && !profile.ContractedDays.Contains(request.PlanningDate.DayOfWeek))
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{driver.DisplayName} is not contracted to work {request.PlanningDate:dddd}."));

            var heldSkills = DispatchSkillRules.Parse(profile.SkillsText ?? driver.Skills);
            if (!DispatchSkillRules.HasAll(heldSkills, run.Dto.RequiredSkills))
            {
                var missingSkills = DispatchSkillRules.MissingNames(heldSkills, run.Dto.RequiredSkills);
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId,
                    $"{driver.DisplayName} does not hold the required skills for {run.Dto.Reference}: missing {missingSkills}."));
            }

            if (run.Dto.RequiresDoubleDeck && !TrailerMatches(allocation.TrailerId, trailerById, "double", "deck"))
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{run.Dto.Reference} requires a double-deck trailer."));
            if (run.Dto.RequiresRefrigerated && !TrailerMatches(allocation.TrailerId, trailerById, "refrig", "fridge", "chill", "temp"))
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{run.Dto.Reference} requires a refrigerated trailer."));

            var driverDuties = duties.Where(duty => DriverDayCycleCalculator.MatchesDriver(driver, duty)).ToList();
            if (driverDuties.Count == 0)
            {
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId,
                    $"No TachoMaster duty evidence is available for {driver.DisplayName}; legal rest and hours cannot be validated."));
                continue;
            }

            var rest = DispatchTachoRules.DeriveRequiredRestPeriod(driver, driverDuties, allocation.UseReducedDailyRest);
            if (allocation.UseReducedDailyRest && rest.Hours != 9)
            {
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId,
                    $"{driver.DisplayName} cannot use reduced 9h daily rest because the statutory reduced-rest allowance is exhausted or cannot be evidenced."));
                continue;
            }

            var availableFrom = DispatchTachoRules.AvailableFrom(DispatchTachoRules.LatestShiftEndUtc(driver, driverDuties), rest);
            if (availableFrom is DateTimeOffset legalStart && allocation.PlannedStartTime < legalStart)
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId,
                    $"{driver.DisplayName} cannot legally start before {TimeZoneInfo.ConvertTime(legalStart, London):dd/MM HH:mm}; Tacho requires {rest.Hours}h daily rest."));

            var weeklyWorking = DispatchTachoRules.WeeklyWorkingTimeHours(driver, driverDuties, referenceDate);
            var currentDailyDriving = request.PlanningDate > today ? 0m : DispatchTachoRules.DailyDrivingTimeHours(driver, driverDuties, referenceDate);
            var breakCompliant = DispatchTachoRules.BreakCompliant(driver, driverDuties, referenceDate);
            if (!breakCompliant)
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{driver.DisplayName} has an unresolved Tacho break-compliance breach."));

            var dailyLimitHours = DispatchTachoRules.DailyDrivingLimitMinutes(driver, driverDuties) / 60m;
            decimal? providerWork = DispatchTachoRules.WorkAvailableWeekMinutes(driver, driverDuties) is int workMinutes ? workMinutes / 60m : null;
            decimal? providerDrive = DispatchTachoRules.DriveAvailablePlanningDayMinutes(driver, driverDuties, request.PlanningDate, today) is int driveMinutes ? driveMinutes / 60m : null;
            var projected = DispatchTachoRules.DetectProjectedBreach(
                weeklyWorking,
                currentDailyDriving,
                run.EstimatedDutyHours,
                run.EstimatedDrivingHours,
                dailyLimitHours,
                providerWork,
                providerDrive);
            if (projected is not null)
                failures.Add(new DispatchLockFailure(driver.Id, allocation.RunId, $"{driver.DisplayName}: {projected.Detail}."));
        }

        if (failures.Count > 0) return new DispatchLockResponse(false, failures);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var allocation in request.Allocations)
            {
                var registered = await PlanningRegisterStore.GetLoadAsync(db, allocation.RunId, ct);
                if (registered is not null)
                {
                    registered.DriverId = allocation.DriverId;
                    registered.VehicleId = allocation.VehicleId;
                    registered.TrailerId = allocation.TrailerId;
                    registered.Status = LoadStatus.Planned;
                    await PlanningRegisterStore.SaveLoadAsync(db, registered, actor, ct);
                }
                else
                {
                    var load = await db.Loads.SingleOrDefaultAsync(item => item.Id == allocation.RunId && item.PlanningDate == request.PlanningDate, ct);
                    if (load is null) throw new InvalidOperationException($"Run {allocation.RunId} disappeared before the atomic lock write.");
                    load.DriverId = allocation.DriverId;
                    load.VehicleId = allocation.VehicleId;
                    load.TrailerId = allocation.TrailerId;
                    load.Status = LoadStatus.Planned;
                    await db.SaveChangesAsync(ct);
                }

                await DriverDispatchStateStore.SetPlannedStartAsync(
                    db,
                    allocation.RunId,
                    allocation.PlannedStartTime.ToUniversalTime(),
                    actor,
                    ct,
                    allocation.UseReducedDailyRest ? "Locked smart dispatch plan · reduced 9h daily rest" : "Locked smart dispatch plan · regular 11h daily rest",
                    allocation.DriverId,
                    allocation.UseReducedDailyRest);
                db.OperationalHistoryEvents.Add(new OperationalHistoryEvent
                {
                    EntityType = "Load", EntityId = allocation.RunId, EventType = "DispatchLocked", Actor = actor,
                    PayloadJson = JsonSerializer.Serialize(new { allocation.DriverId, allocation.VehicleId, allocation.TrailerId, allocation.PlannedStartTime, request.PlanningDate }),
                    OccurredAtUtc = DateTimeOffset.UtcNow
                });
            }

            await transaction.CommitAsync(ct);
            return new DispatchLockResponse(true, []);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            logger.LogError(exception, "Smart Dispatch atomic lock failed for {PlanningDate}; transaction rolled back.", request.PlanningDate);
            return new DispatchLockResponse(false,
                [new DispatchLockFailure(Guid.Empty, null, "Plan could not be locked atomically. No allocations were saved; refresh and try again.")]);
        }
    }

    private async Task<List<RunProfile>> ReadRunProfilesAsync(DateOnly planningDate, CancellationToken ct)
    {
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToList();
        try { await RunOperationalStore.EnrichAsync(db, loads, ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Dispatch run operational enrichment was unavailable; core run data will still be returned.");
            db.ChangeTracker.Clear();
        }

        var trailerIds = loads.Where(load => load.TrailerId is not null).Select(load => load.TrailerId!.Value).Distinct().ToArray();
        var trailerById = trailerIds.Length == 0
            ? new Dictionary<Guid, Trailer>()
            : await db.Trailers.AsNoTracking().Where(trailer => trailerIds.Contains(trailer.Id)).ToDictionaryAsync(trailer => trailer.Id, ct);

        var result = new List<RunProfile>(loads.Count);
        foreach (var load in loads)
        {
            var stops = OperationalStopOrdering.Order(load.Stops).ToList();
            var first = stops.FirstOrDefault(stop => stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase)) ?? stops.FirstOrDefault();
            var last = stops.LastOrDefault();
            var trailer = load.TrailerId is Guid trailerId ? trailerById.GetValueOrDefault(trailerId) : null;
            var text = $"{load.Reference} {load.PlannerNotes} {load.CapacityType} {string.Join(' ', stops.Select(stop => stop.Name))}";

            var market = ContainsAny(text, "market", "covent garden", "new spitalfields", "spitalfields", "smithfield");
            var doubleDeck = ContainsAny(trailer?.Type, "double", "deck") || ContainsAny(text, "double deck", "double-deck", " dd ");
            var refrigerated = load.TemperatureC is not null || ContainsAny(trailer?.Type, "refrig", "fridge", "chill", "temp") ||
                               ContainsAny(text, "refrigerated", "fridge", "chilled", "frozen", "temperature controlled");

            var required = DispatchSkill.None;
            if (market) required |= DispatchSkill.MarketRun;
            if (doubleDeck) required |= DispatchSkill.DoubleDecker;
            if (refrigerated) required |= DispatchSkill.RefrigeratedUnit;
            if (ContainsAny(text, " adr ", "hazchem", "haz chem")) required |= DispatchSkill.HazChem;
            if (ContainsAny(text, "moffett", " moff ")) required |= DispatchSkill.Moffett;
            if (ContainsAny(text, "tail lift", "tail-lift")) required |= DispatchSkill.TailLift;
            if (ContainsAny(text, "manual handling")) required |= DispatchSkill.ManualHandling;

            var southbound = first?.Latitude is decimal firstLat && last?.Latitude is decimal lastLat &&
                             lastLat <= firstLat - options.SouthboundMinimumLatitudeDrop;
            var explicitBackload = ContainsAny(text, "backload", "back load", "return load", "return-run", "return run");
            var backload = explicitBackload || (southbound && first?.Latitude is decimal collectionLat && collectionLat > options.HomeLatitude + 0.5m);
            var estimatedDriving = EstimateDrivingHours(load, stops);
            var estimatedDuty = EstimateDutyHours(stops, estimatedDriving);

            result.Add(new RunProfile(
                load,
                new DispatchRunDto(
                    load.Id,
                    RunDisplayLabel.For(load),
                    new DispatchCollectionPointDto(CleanStopName(first?.Name) ?? first?.Name ?? "Collection", first?.Latitude, first?.Longitude),
                    required,
                    doubleDeck,
                    refrigerated,
                    backload,
                    market,
                    southbound,
                    load.RelayPlan is { Enabled: true } relay
                        ? new DispatchRelayDto(
                            true,
                            relay.HandoverSite,
                            relay.HandoverSiteId,
                            relay.HandoverAfterStopSequence,
                            relay.PlannedHandoverUtc,
                            relay.DeliveryDriverId,
                            relay.DeliveryVehicleId,
                            relay.DeliveryTrailerId)
                        : null),
                last?.Latitude,
                last?.Longitude,
                estimatedDuty,
                estimatedDriving));
        }

        return result;
    }

    private DispatchSuggestion ChooseSuggestion(
        Guid driverId,
        DispatchSkill skills,
        bool needsReturn,
        decimal? latitude,
        decimal? longitude,
        IReadOnlyCollection<RunProfile> runProfiles)
    {
        var eligible = runProfiles
            .Where(profile => profile.Load.DriverId is null || profile.Load.DriverId == driverId)
            .Where(profile => DispatchSkillRules.HasAll(skills, profile.Dto.RequiredSkills))
            .Select(profile => new
            {
                Profile = profile,
                Distance = DispatchReturnRules.DistanceMiles(latitude, longitude, profile.Dto.CollectionPoint.Latitude, profile.Dto.CollectionPoint.Longitude)
            })
            .ToList();

        if (needsReturn)
        {
            var homeDistance = DispatchReturnRules.DistanceMiles(latitude, longitude, options.HomeLatitude, options.HomeLongitude);
            var backload = eligible
                .Where(item => (item.Profile.Dto.IsBackload || item.Profile.Dto.IsSouthbound) && item.Distance is not null && item.Distance <= options.BackloadRadiusMiles)
                .Where(item => item.Profile.FinalLatitude is null || latitude is null || item.Profile.FinalLatitude < latitude)
                .OrderBy(item => item.Distance)
                .ThenBy(item => item.Profile.Dto.Reference)
                .FirstOrDefault();

            if (backload is null)
                return new DispatchSuggestion(null, null, null, false, null, "Return run needed — no backload available");

            var reduction = homeDistance is decimal directHome && backload.Distance is decimal toCollection
                ? Math.Max(0m, Math.Round(directHome - toCollection, 1))
                : (decimal?)null;
            var reductionText = reduction is decimal saved ? $" — reduces deadhead by {saved:0.#}mi" : string.Empty;
            return new DispatchSuggestion(
                backload.Profile.Dto.RunId,
                backload.Profile.Dto.Reference,
                backload.Distance,
                true,
                reduction,
                $"Backload candidate{reductionText}",
                ScoreSuggestion(backload.Profile, backload.Distance, skills, needsReturn, true, driverId),
                SuggestionReasons(backload.Profile, backload.Distance, skills, needsReturn, true, reduction));
        }

        var closest = eligible.Where(item => item.Distance is not null).OrderBy(item => item.Distance).ThenBy(item => item.Profile.Dto.Reference).FirstOrDefault();
        if (closest is not null)
            return new DispatchSuggestion(
                closest.Profile.Dto.RunId,
                closest.Profile.Dto.Reference,
                closest.Distance,
                closest.Profile.Dto.IsBackload,
                null,
                $"This driver is {closest.Distance:0.#}mi from {closest.Profile.Dto.CollectionPoint.Name} — good fit for {closest.Profile.Dto.Reference}",
                ScoreSuggestion(closest.Profile, closest.Distance, skills, needsReturn, closest.Profile.Dto.IsBackload, driverId),
                SuggestionReasons(closest.Profile, closest.Distance, skills, needsReturn, closest.Profile.Dto.IsBackload, null));

        var first = eligible.OrderBy(item => item.Profile.Dto.Reference).FirstOrDefault();
        return first is null
            ? DispatchSuggestion.None
            : new DispatchSuggestion(first.Profile.Dto.RunId, first.Profile.Dto.Reference, null, first.Profile.Dto.IsBackload, null, "Compatible run available; live distance is not currently available.",
                ScoreSuggestion(first.Profile, null, skills, needsReturn, first.Profile.Dto.IsBackload, driverId),
                SuggestionReasons(first.Profile, null, skills, needsReturn, first.Profile.Dto.IsBackload, null));
    }

    private static int ScoreSuggestion(RunProfile profile, decimal? distanceMiles, DispatchSkill heldSkills, bool needsReturn, bool isBackload, Guid driverId)
    {
        var score = distanceMiles is decimal distance
            ? Math.Clamp((int)Math.Round(45m - distance * 0.65m), 0, 45)
            : 12;
        if (profile.Dto.RequiredSkills == DispatchSkill.None) score += 15;
        else if (DispatchSkillRules.HasAll(heldSkills, profile.Dto.RequiredSkills)) score += 25;
        if (needsReturn && isBackload) score += 25;
        else if (!needsReturn && !isBackload) score += 10;
        if (profile.Load.DriverId == driverId) score += 10;
        return Math.Clamp(score, 0, 100);
    }

    private static IReadOnlyList<string> SuggestionReasons(RunProfile profile, decimal? distanceMiles, DispatchSkill heldSkills, bool needsReturn, bool isBackload, decimal? deadheadReductionMiles)
    {
        var reasons = new List<string>();
        if (distanceMiles is decimal distance) reasons.Add($"{distance:0.#}mi to first collection");
        else reasons.Add("Live distance unavailable; location fallback used");
        if (profile.Dto.RequiredSkills == DispatchSkill.None) reasons.Add("No specialist skills required");
        else reasons.Add($"Skills matched: {string.Join(", ", DispatchSkillRules.Names(profile.Dto.RequiredSkills))}");
        if (needsReturn && isBackload)
            reasons.Add(deadheadReductionMiles is decimal saved && saved > 0 ? $"Return/backload fit; saves about {saved:0.#}mi deadhead" : "Return/backload fit");
        else if (!needsReturn && !isBackload)
            reasons.Add("Normal work matched by collection proximity");
        if (profile.Load.DriverId is not null) reasons.Add("Already allocated to this driver");
        return reasons;
    }

    private async Task<Dictionary<Guid, DriverMasterProfile>> ReadDriverMasterProfilesAsync(IReadOnlyCollection<Driver> drivers, CancellationToken ct)
    {
        if (drivers.Count == 0) return [];
        var byCode = drivers
            .GroupBy(driver => Normalise(driver.EmployeeNumber), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == DriverDetailType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        var result = new Dictionary<Guid, DriverMasterProfile>();
        var applied = new HashSet<Guid>();
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var driverCode = Text(root, "driverCode") ?? Text(root, "employeeNumber") ?? Text(root, "driverId") ?? Text(root, "payrollNumber");
                if (string.IsNullOrWhiteSpace(driverCode) || !byCode.TryGetValue(Normalise(driverCode), out var driver) || !applied.Add(driver.Id)) continue;

                var holidayDates = Values(root, "holidayDates")
                    .Concat(Values(root, "holidays"))
                    .Select(value => DateOnly.TryParse(value, out var date) ? date : (DateOnly?)null)
                    .Where(date => date is not null)
                    .Select(date => date!.Value)
                    .Distinct()
                    .OrderBy(date => date)
                    .ToArray();
                var contractedDays = Values(root, "contractedDays")
                    .Select(ParseDayOfWeek)
                    .Where(day => day is not null)
                    .Select(day => day!.Value)
                    .Distinct()
                    .ToArray();

                result[driver.Id] = new DriverMasterProfile(
                    driverCode.Trim(),
                    Text(root, "employmentType") ?? Text(root, "driverType"),
                    Text(root, "skills") ?? driver.Skills,
                    holidayDates,
                    contractedDays,
                    Text(root, "homeDepot") ?? Text(root, "baseDepot"));
            }
            catch (JsonException exception)
            {
                logger.LogWarning(exception, "Ignoring malformed Driver Master detail row {RowId} while building Dispatch.", row.Id);
            }
        }

        return result;
    }

    private async Task<List<TachoDriverDutyStatus>> ReadDutiesAsync(DateOnly planningDate, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured) return [];
        var today = LondonDate(DateTimeOffset.UtcNow);
        var through = planningDate < today ? planningDate : today;
        var from = through.AddDays(-Math.Max(8, options.TachoHistoryDays));
        var result = new List<TachoDriverDutyStatus>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var gate = new SemaphoreSlim(4, 4);
            var dutyDates = Enumerable.Range(0, through.DayNumber - from.DayNumber + 1)
                .Select(offset => from.AddDays(offset));
            var dutyBatches = await Task.WhenAll(dutyDates.Select(async date =>
            {
                await gate.WaitAsync(timeout.Token);
                try { return await tachoMaster.GetDriverDutyStatusesAsync(date, timeout.Token); }
                finally { gate.Release(); }
            }));
            result.AddRange(dutyBatches.SelectMany(batch => batch));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("TachoMaster exceeded the Smart Dispatch history budget; partial Tacho evidence will be used and lock validation will fail closed where evidence is missing.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster history was unavailable for Smart Dispatch; lock validation will fail closed where evidence is missing.");
        }
        return result;
    }

    private async Task<List<VehicleLiveStatus>> ReadLiveStatusesAsync(CancellationToken ct)
    {
        try { return await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Live tracking status was unavailable for Smart Dispatch suggestions.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private async Task<List<VehicleTrackingEvent>> ReadTachoSignOffTrackingAsync(
        IReadOnlyCollection<TachoDriverDutyStatus> duties,
        CancellationToken ct)
    {
        var completed = duties
            .Where(duty => duty.DutyEndUtc is not null && !string.IsNullOrWhiteSpace(duty.VehicleCode))
            .ToList();
        if (completed.Count == 0) return [];

        var latestEnd = completed.Max(duty => duty.DutyEndUtc!.Value);
        var recentCutoff = latestEnd.AddHours(-48);
        var candidateIdentifiers = completed
            .Where(duty => duty.DutyEndUtc >= recentCutoff)
            .SelectMany(duty => new[]
            {
                duty.VehicleCode.Trim(),
                ExecutionIdentityResolver.NormaliseVehicle(duty.VehicleCode)
            })
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidateIdentifiers.Length == 0) return [];

        var from = recentCutoff.AddHours(-12);
        var through = latestEnd.AddMinutes(30);
        try
        {
            return await db.VehicleTrackingEvents.AsNoTracking()
                .Where(item =>
                    item.EventTimeUtc >= from &&
                    item.EventTimeUtc <= through &&
                    candidateIdentifiers.Contains(item.VehicleIdentifier))
                .OrderByDescending(item => item.EventTimeUtc)
                .Take(25000)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Historical RoadTech positions were unavailable for Tacho sign-off location fallback.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private async Task<List<Load>> ReadRecentHistoryAsync(DateOnly planningDate, CancellationToken ct)
    {
        var from = planningDate.AddDays(-7);
        try
        {
            return await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.DriverId != null && load.PlanningDate >= from && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
                .OrderByDescending(load => load.PlanningDate)
                .ThenByDescending(load => load.CreatedAtUtc)
                .Take(1000)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Recent run history was unavailable for Smart Dispatch location fallback.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static VehicleLiveStatus? MatchLiveDriver(
        Driver driver,
        IEnumerable<VehicleLiveStatus> statuses,
        bool hasOpenDuty,
        DateTimeOffset? latestCompletedDutyEnd)
    {
        var driverCard = Normalise(driver.TachoCardNumber);
        var driverNames = new[] { driver.TachoName, driver.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalisePerson)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return statuses
            .Where(status =>
            {
                var liveCard = Normalise(status.CurrentDriverCardNumber);
                var cardMatch = driverCard.Length >= 8 && liveCard.Length >= 8 &&
                    (driverCard == liveCard || driverCard.EndsWith(liveCard, StringComparison.OrdinalIgnoreCase) || liveCard.EndsWith(driverCard, StringComparison.OrdinalIgnoreCase));
                var liveName = NormalisePerson(status.CurrentDriverName);
                var nameMatch = liveName.Length > 0 && driverNames.Contains(liveName);
                if (!cardMatch && !nameMatch) return false;

                // If Tacho says the duty has ended, don't let a stale driver identity on a
                // vehicle that was moved later become this driver's current position.
