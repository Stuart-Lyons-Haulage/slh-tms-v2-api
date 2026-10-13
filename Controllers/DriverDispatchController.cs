using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/driver-dispatch"), Authorize]
public sealed class DriverDispatchController(
    TmsDbContext db,
    SageHrClient sageHr,
    TachoMasterClient tachoMaster,
    IMemoryCache cache,
    ILogger<DriverDispatchController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly LoadStatus[] ExecutedStatuses = [LoadStatus.Dispatched, LoadStatus.InProgress, LoadStatus.Completed];

    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var activeDrivers = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);
        var tachoLinkedDrivers = await db.Drivers.AsNoTracking().CountAsync(driver =>
            driver.Active && driver.TachoMasterDriverId != null && driver.TachoMasterDriverId != "", ct);
        var latestSyncUtc = await db.StagedImports.AsNoTracking()
            .Where(item => item.Status == StagingStatus.Promoted &&
                           (item.EntityType == "tachodrivermastersync" ||
                            item.EntityType == "tachodrivermasterorchestration" ||
                            item.EntityType == TachoDriverMasterSyncJobService.EntityType))
            .OrderByDescending(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .Select(item => item.ReviewedAtUtc ?? item.ReceivedAtUtc)
            .FirstOrDefaultAsync(ct);

        return Ok(new
        {
            healthy = activeDrivers > 0,
            activeDrivers,
            tachoLinkedDrivers,
            latestTachoSyncUtc = latestSyncUtc == default ? (DateTimeOffset?)null : latestSyncUtc,
            message = activeDrivers > 0
                ? "Driver Master contains an active roster."
                : "Driver Master contains no active drivers; Dispatch cannot calculate availability."
        });
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
        var cacheKey = $"driver-dispatch-snapshot:{planningDate:yyyy-MM-dd}";
        if (cache.TryGetValue<object>(cacheKey, out var cachedSnapshot)) return Ok(cachedSnapshot);
        var weekStart = DriverDispatchAgencyRosterStore.WeekStart(planningDate);
        var weekEnd = weekStart.AddDays(6);
        List<Load> loads;
        try
        {
            // Read the operational run table first. This is the normal path for built runs
            // and avoids scanning every audited planning source when the date is empty.
            using var planningBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            planningBudget.CancelAfter(TimeSpan.FromSeconds(10));
            // FirstPlanned reads the child-stop collection and is not SQL-translatable on
            // every provider. Bound the database read first, then apply the deterministic
            // operational ordering in memory so one bad provider cannot take Dispatch down.
            loads = (await db.Loads.AsNoTracking()
                .Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
                .Take(2000)
                .ToListAsync(planningBudget.Token))
                .OrderBy(load => FirstPlanned(load) ?? DateTimeOffset.MaxValue)
                .ThenBy(load => load.Reference)
                .ToList();

            // If the operational table is empty, give the resilient planning register a short
            // opportunity to recover an audit-only run, but never let that block the roster.
            if (loads.Count == 0)
            {
                using var recoveryBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                recoveryBudget.CancelAfter(TimeSpan.FromSeconds(5));
                loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, recoveryBudget.Token))
                    .Where(load => load.Status != LoadStatus.Cancelled)
                    .OrderBy(load => FirstPlanned(load) ?? DateTimeOffset.MaxValue)
                    .ThenBy(load => load.Reference)
                    .ToList();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Planning sources exceeded the bounded Driver Dispatch read budget; returning the canonical driver roster without optional routes.");
            db.ChangeTracker.Clear();
            loads = [];
        }
        if (loads.Count > 0)
        {
            try { await LoadCommercialStore.EnrichAsync(db, loads, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Driver Dispatch commercial enrichment was unavailable; the operational plan will still be returned.");
                db.ChangeTracker.Clear();
            }
        }

        // One bounded SQL history query still supplies yesterday/previous-run context and the
        // emergency fallback. Day number itself is calculated from TachoMaster duty history below.
        var history = loads.Count == 0
            ? new List<Load>()
            : await db.Loads.AsNoTracking()
                .Include(load => load.Stops)
                .Where(load => load.DriverId != null && load.PlanningDate >= planningDate.AddDays(-7) && load.PlanningDate < planningDate && load.Status != LoadStatus.Cancelled)
                .OrderByDescending(load => load.PlanningDate)
                .ThenByDescending(load => load.CreatedAtUtc)
                .ToListAsync(ct);

        var roster = loads.Count == 0
            ? new Dictionary<Guid, DriverDispatchAgencyRosterEntry>()
            : await DriverDispatchAgencyRosterStore.ReadForDateAsync(db, planningDate, ct);
        var relevantDriverIds = loads.Concat(history)
            .Where(load => load.DriverId is not null)
            .Select(load => load.DriverId!.Value)
            .ToHashSet();
        // Driver Master can carry stale Active flags while the promoted master-detail
        // record still identifies current employed drivers. Read the compact identity table
        // without the Active predicate, then apply the same positive driver/evidence rules
        // below so Dispatch does not lose the workforce after a refresh.
        var identityDrivers = await db.Drivers.AsNoTracking()
            .OrderBy(driver => driver.DisplayName)
            .ToListAsync(ct);
        if (loads.Count > 0)
            await MasterDetailStore.EnrichDriversAsync(db, identityDrivers, ct);
        RepairAllocatedDriverAliases(loads, identityDrivers);
        RepairAllocatedDriverAliases(history, identityDrivers);
        var driverCandidates = identityDrivers
            .Where(driver =>
                driver.Active ||
                DriverPopulationRules.IsDriver(driver) ||
                DriverAvailabilityService.CanonicalEmploymentType(driver.DriverType, driver.DriverGroup) != "Unknown")
            .OrderBy(driver => driver.DisplayName)
            .ToList();
        var vehicles = loads.Count == 0
            ? new List<Vehicle>()
            : await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).OrderBy(vehicle => vehicle.Registration).ToListAsync(ct);
        var trailers = loads.Count == 0
            ? new List<Trailer>()
            : await db.Trailers.AsNoTracking().Where(trailer => trailer.Active).OrderBy(trailer => trailer.TrailerNumber).ToListAsync(ct);
        var vehicleById = vehicles.ToDictionary(vehicle => vehicle.Id);
        // Read the same cached Staffing availability snapshot consumed by Dispatch and
        // the availability calendar. This avoids a second full Sage HR read on this path.
        var sage = loads.Count == 0 ? SageDispatchState.Unavailable : await ReadSageStateAsync(planningDate, ct);

        var currentDayDriverIds = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).ToHashSet();

        var selectedDrivers = loads.Count == 0
            ? identityDrivers.Where(driver => driver.Active).ToList()
            : driverCandidates.Where(driver =>
                currentDayDriverIds.Contains(driver.Id) ||
                DriverPopulationRules.IsDriver(driver) ||
                DriverAvailabilityService.CanonicalEmploymentType(driver.DriverType, driver.DriverGroup) != "Unknown").ToList();

        if (loads.Count > 0)
            await MasterDetailStore.EnrichDriversAsync(db, selectedDrivers, ct);

        // Detail enrichment supplies agency name, coding and skills, but does not
        // change the Master Data employment classification.
        selectedDrivers = selectedDrivers.OrderBy(driver => driver.DisplayName).ToList();

        // Start the bounded Tacho history read before the remaining planning enrichment so its
        // network latency overlaps with the local database/assistant work below.
        // With no built runs there is no route context to score or Tacho-cycle against.
        // Keep the workforce roster independent of those optional planning enrichments.
        var tachoDayTask = loads.Count == 0
            ? Task.FromResult(TachoDayNumberState.Unavailable)
            : ReadTachoDayNumbersAsync(selectedDrivers, planningDate, ct);
        var dispatchStates = loads.Count == 0
            ? new Dictionary<Guid, DriverDispatchState>()
            : await DriverDispatchStateStore.ReadAsync(db, loads.Select(load => load.Id), ct);
        var unavailableDriverIds = selectedDrivers
            .Where(driver => sage.Leave.ContainsKey(Normalise(driver.EmployeeNumber)))
            .Select(driver => driver.Id)
            .ToHashSet();
        var assistedPlan = loads.Count == 0
            ? new Dictionary<Guid, DriverDispatchAssistantSuggestion>()
            : await DriverDispatchAssistantService.BuildAsync(
                db,
                planningDate,
                selectedDrivers,
                loads,
                history,
                vehicles,
                trailers,
                unavailableDriverIds,
                ct);
        var tachoDays = await tachoDayTask;

        var rows = new List<DriverDispatchDriver>();
        foreach (var driver in selectedDrivers)
        {
            var allocated = loads.Where(load => load.DriverId == driver.Id).OrderBy(load => FirstPlanned(load) ?? DateTimeOffset.MaxValue).ToList();
            var previous = history.Where(load => load.DriverId == driver.Id)
                .OrderByDescending(load => load.PlanningDate)
                .ThenByDescending(load => load.CreatedAtUtc)
                .FirstOrDefault();
            var previousVehicle = previous?.VehicleId is Guid previousVehicleId && vehicleById.TryGetValue(previousVehicleId, out var foundVehicle) ? foundVehicle : null;
            var dayNumber = tachoDays.DayNumbers.TryGetValue(driver.Id, out var tachoDayNumber)
                ? tachoDayNumber
                : ConsecutiveWorkedDays(history, driver, planningDate) + 1;
            var employeeKey = Normalise(driver.EmployeeNumber);
            sage.Leave.TryGetValue(employeeKey, out var absence);
            roster.TryGetValue(driver.Id, out var rosterEntry);
            assistedPlan.TryGetValue(driver.Id, out var assisted);

            Guid? suggestedRunId = null;
            string? suggestedRunReference = null;
            Guid? suggestedVehicleId = null;
            string? suggestedVehicleRegistration = null;
            int? assistantScore = null;
            string? suggestion = null;
            var previousStops = previous?.Stops.OrderBy(stop => stop.Sequence).ToList() ?? [];
            var previousFinal = previousStops.LastOrDefault();
            var previousRoute = previousStops.Count == 0
                ? null
                : string.Join(" → ", previousStops.Select(stop => CleanStopName(stop.Name)));
            if (absence is not null)
            {
                suggestion = $"Unavailable · Sage HR {absence.PolicyName ?? "leave"}{(absence.IsPartOfDay ? " (part day)" : string.Empty)}.";
            }
            else if (allocated.Count > 0)
            {
                suggestion = allocated.Count == 1 ? "Already allocated." : $"{allocated.Count} routes already allocated.";
            }
            else if (assisted is not null)
            {
                suggestedRunId = assisted.LoadId;
                suggestedRunReference = assisted.LoadReference;
                suggestedVehicleId = assisted.VehicleId;
                suggestedVehicleRegistration = assisted.VehicleRegistration;
                assistantScore = assisted.Score;
                suggestion = assisted.Reason;
            }
            else if (DriverPopulationRules.IsSubcontractor(driver))
            {
                suggestion = $"Subcontractor · {driver.DriverGroup ?? driver.DisplayName} · allocate when external cover is required.";
            }
            else if (string.Equals(driver.Coding?.Trim(), "3", StringComparison.OrdinalIgnoreCase))
            {
                suggestion = $"Day {dayNumber} · Code 3 · keep to straightforward work.";
            }
            else if (IsAgency(driver))
            {
                var availability = rosterEntry is null ? string.Empty : $" · booked to {rosterEntry.ThroughDate:ddd dd/MM}";
                suggestion = $"Agency{(string.IsNullOrWhiteSpace(driver.AgencyName) ? string.Empty : $" · {driver.AgencyName}")}{availability} · use after suitable employed/casual drivers.";
            }
            else if (previousVehicle is not null)
            {
                suggestion = $"Suggest {previousVehicle.Registration} · driver used it on {previous!.PlanningDate:dd/MM}.";
            }

            rows.Add(new DriverDispatchDriver(
                driver.Id,
                driver.EmployeeNumber,
                driver.DisplayName,
                DriverAvailabilityService.CanonicalEmploymentType(driver.DriverType, driver.DriverGroup),
                driver.DriverGroup,
                driver.Skills,
                driver.Coding,
                driver.AgencyName,
                driver.TachoMasterDriverId,
                driver.TachoCardNumber,
                driver.LicenceExpiry,
                driver.LicenceStatus,
                dayNumber,
                absence is not null,
                absence?.PolicyName,
                absence?.Details,
                absence?.IsPartOfDay ?? false,
                previous?.Id,
                previous is null ? null : RunDisplayLabel.For(previous),
                previousVehicle?.Id,
                previousVehicle?.Registration,
                previousFinal?.Name,
                previousFinal?.Latitude,
                previousRoute,
                allocated.FirstOrDefault()?.Id,
                allocated.Count,
                suggestedRunId,
                suggestedRunReference,
                suggestedVehicleId,
                suggestedVehicleRegistration,
                assistantScore,
                suggestion,
                rosterEntry?.FromDate,
                rosterEntry?.ThroughDate));
        }

        var snapshot = new
        {
            planningDate,
            weekStart,
            weekEnd,
            generatedAtUtc = DateTimeOffset.UtcNow,
            leaveSource = sage.Available ? "Sage HR" : "Unavailable",
            driverPopulationSource = sage.Available ? "Sage HR driver roles + relevant Agency + active Subcontractors" : "Canonical driver evidence + relevant Agency + active Subcontractors",
            dayNumberSource = tachoDays.Available
                ? "TachoMaster duty history / weekly-rest cycle; TMS executed-run history only where no usable Tacho identity is available"
                : "TachoMaster unavailable; TMS executed-run history fallback",
            assistantSource = "Explainable matching using live/last Falcon position, work-day continuity, Driver skills/coding, route direction and learned regular vehicle preference",
            drivers = rows
                .OrderByDescending(row => row.AssignedLoadId is not null)
                .ThenBy(row => TypeOrder(row.DriverType))
                .ThenBy(row => row.DisplayName),
            vehicles,
            trailers,
            loads = loads.Select(load => new
            {
                load.Id,
                reference = RunDisplayLabel.For(load),
                rawReference = load.Reference,
                load.PlanningDate,
                status = load.Status.ToString(),
                load.DriverId,
                load.VehicleId,
                load.TrailerId,
                load.PalletSpacesUsed,
                load.TotalPalletSpaces,
                load.CapacityType,
                load.PlannerNotes,
                relayPlan = load.RelayPlan,
                southbound = IsSouthbound(load),
                plannedStartUtc = dispatchStates.GetValueOrDefault(load.Id)?.PlannedStartUtc,
                stops = load.Stops.OrderBy(stop => stop.Sequence).Select(stop => new
                {
                    stop.Id,
                    stop.Sequence,
                    stop.Name,
                    stop.Address,
                    stop.Latitude,
                    stop.Longitude,
                    stop.PlannedArrivalUtc,
                    stop.PlannerNote
                })
            })
        };
        cache.Set(cacheKey, snapshot, TimeSpan.FromSeconds(10));
        return Ok(snapshot);
    }

    [HttpPost("drivers"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> AddOrRosterDriver(DriverDispatchAddDriverRequest request, CancellationToken ct)
    {
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) return BadRequest(new { message = "Enter the driver's name." });

        var type = CanonicalRequestedType(request.DriverType);
        if (type is null) return BadRequest(new { message = "Driver type must be Employed, Casual or Agency." });
        if (type != "Agency" && string.IsNullOrWhiteSpace(request.EmployeeNumber))
            return BadRequest(new { message = "Enter the employee number for an employed/casual driver, or use Sync Drivers first." });
        if (type == "Agency" && string.IsNullOrWhiteSpace(request.AgencyName))
            return BadRequest(new { message = "Enter the agency name for a new agency driver." });
        if (type == "Agency" && (request.Days is null || request.Days < 1 || request.Days > 7))
            return BadRequest(new { message = "Tell Dispatch how many days we have the agency driver (1 to 7)." });

        var employeeNumber = request.EmployeeNumber?.Trim();
        Driver? driver = null;
        if (!string.IsNullOrWhiteSpace(employeeNumber))
            driver = await db.Drivers.FirstOrDefaultAsync(item => item.Active && item.EmployeeNumber == employeeNumber, ct);

        if (driver is null)
        {
            var nameMatches = await db.Drivers.Where(item => item.Active && item.DisplayName.ToUpper() == name.ToUpper()).ToListAsync(ct);
            if (nameMatches.Count > 1)
                return Conflict(new { message = "More than one active Driver Master record has that name. Sync Drivers and select the canonical record before adding it to Dispatch." });
            driver = nameMatches.SingleOrDefault();
        }

        var created = false;
        if (driver is null)
        {
            employeeNumber ??= type == "Agency"
                ? $"AGY-{DateTime.UtcNow:yyMMdd}-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}"
                : throw new InvalidOperationException("Employee number was validated above.");

            if (await db.Drivers.AnyAsync(item => item.EmployeeNumber == employeeNumber, ct))
                return Conflict(new { message = "That employee number already exists in Driver Master. Use Sync Drivers or enter the existing driver instead." });

            driver = new Driver
            {
                EmployeeNumber = employeeNumber,
                DisplayName = name,
                DriverType = type,
                DriverGroup = type == "Agency" ? request.AgencyName!.Trim() : type,
                Active = true
            };
            db.Drivers.Add(driver);
            await db.SaveChangesAsync(ct);
            created = true;
        }

        await MasterDetailStore.EnrichDriversAsync(db, [driver], ct);
        if (type == "Agency" && !IsAgency(driver) && !created)
            return Conflict(new { message = "That name belongs to a non-agency Driver Master record. Use the existing driver or Sync Drivers before creating another identity." });

        driver.DriverType = type;
        driver.DriverGroup = type == "Agency" ? request.AgencyName!.Trim() : driver.DriverGroup ?? type;
        driver.AgencyName = type == "Agency" ? request.AgencyName!.Trim() : driver.AgencyName;
        if (type == "Agency") driver.Coding = "4";
        await db.SaveChangesAsync(ct);

        var actor = User.Identity?.Name ?? "TMS planner";
        await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(new
        {
            driver.EmployeeNumber,
            driver.DisplayName,
            driver.TachoName,
            driver.MobileNumber,
            driver.DriverType,
            driver.DriverGroup,
            driver.Skills,
            driver.Coding,
            driver.AgencyName,
            driver.Notes,
            driver.TachoMasterDriverId,
            driver.TachoCardNumber,
            driver.TachoDriveAvailableTodayMinutes,
            driver.TachoDriveAvailableWeekMinutes,
            driver.TachoWorkAvailableWeekMinutes,
            driver.DrivingLicenceNumber,
            driver.LicenceExpiry,
            driver.LicenceStatus,
            driver.LastTachoSyncUtc,
            driver.Active
        }), created ? "Driver Dispatch provisional add" : "Driver Dispatch roster update", actor, ct);

        DriverDispatchAgencyRosterEntry? rosterEntry = null;
        if (type == "Agency")
        {
            var fromDate = request.StartDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
            rosterEntry = await DriverDispatchAgencyRosterStore.UpsertAsync(db, driver, fromDate, request.Days!.Value, actor, ct);
        }
        DriverAvailabilityService.Invalidate(cache);

        return Ok(new
        {
            created,
            driverId = driver.Id,
            driver.DisplayName,
            driver.EmployeeNumber,
            driverType = type,
            roster = rosterEntry,
            message = type == "Agency"
                ? created
                    ? $"{driver.DisplayName} was added provisionally and placed on the agency roster. Sync Drivers will bind the TachoMaster identity when it is available."
                    : $"{driver.DisplayName} already existed and was added to the agency roster."
                : created
                    ? $"{driver.DisplayName} was added to Driver Master. Run Sync Drivers to bind the live TachoMaster identity."
                    : $"{driver.DisplayName} already exists in Driver Master."
        });
    }

    [HttpPost("agency-roster"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> AddAgencyRoster(DriverDispatchAgencyRosterRequest request, CancellationToken ct)
    {
        if (request.Days < 1 || request.Days > 7) return BadRequest(new { message = "Agency availability must be between 1 and 7 days." });
        var driver = await db.Drivers.FirstOrDefaultAsync(item => item.Active && item.Id == request.DriverId, ct);
        if (driver is null) return NotFound(new { message = "The driver could not be found." });
        await MasterDetailStore.EnrichDriversAsync(db, [driver], ct);
        if (!IsAgency(driver)) return BadRequest(new { message = "Only Agency drivers can be added to the weekly agency roster." });
        var actor = User.Identity?.Name ?? "TMS planner";
        var entry = await DriverDispatchAgencyRosterStore.UpsertAsync(db, driver, request.StartDate, request.Days, actor, ct);
        DriverAvailabilityService.Invalidate(cache);
        return Ok(entry);
    }

    [HttpPut("{loadId:guid}/start-time"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SetStartTime(Guid loadId, DriverDispatchStartTimeRequest request, CancellationToken ct)
    {
        var load = await PlanningResilience.ReadLoadAsync(db, loadId, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        DateTimeOffset? plannedStartUtc = null;
        if (!string.IsNullOrWhiteSpace(request.StartTime))
        {
            if (!TimeOnly.TryParse(request.StartTime.Trim(), out var time)) return BadRequest(new { message = "Start time must be a valid HH:mm time." });
            var local = DateTime.SpecifyKind(load.PlanningDate.ToDateTime(time), DateTimeKind.Unspecified);
            plannedStartUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, London), TimeSpan.Zero);
        }

        var actor = User.Identity?.Name ?? "TMS planner";
        var state = await DriverDispatchStateStore.SetPlannedStartAsync(db, loadId, plannedStartUtc, actor, ct, "Manual override", load.DriverId, false);
        try
        {
            db.DriverStatusLogs.Add(new DriverStatusLog
            {
                LoadId = loadId,
                DriverId = load.DriverId,
                Status = plannedStartUtc is null ? "Planned start cleared" : "Planned start set",
                Notes = plannedStartUtc is null
                    ? "Driver Dispatch planned start time cleared."
                    : $"Planned start {TimeZoneInfo.ConvertTime(plannedStartUtc.Value, London):HH:mm} for {load.PlanningDate:dd/MM/yyyy}.",
                CapturedBy = actor
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Could not append planned start audit event for run {LoadId}; dispatch state was retained.", loadId);
            db.ChangeTracker.Clear();
        }
        return Ok(state);
    }

    private async Task<TachoDayNumberState> ReadTachoDayNumbersAsync(IReadOnlyCollection<Driver> drivers, DateOnly planningDate, CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured || drivers.Count == 0) return TachoDayNumberState.Unavailable;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, London).DateTime);
            var throughDate = planningDate <= today ? planningDate : today;
            var fromDate = throughDate.AddDays(-8);
            using var gate = new SemaphoreSlim(4, 4);
            var dutyDates = Enumerable.Range(0, throughDate.DayNumber - fromDate.DayNumber + 1)
                .Select(offset => fromDate.AddDays(offset));
            var dutyBatches = await Task.WhenAll(dutyDates.Select(async dutyDate =>
            {
                await gate.WaitAsync(timeout.Token);
                try { return await tachoMaster.GetDriverDutyStatusesAsync(dutyDate, timeout.Token); }
                finally { gate.Release(); }
            }));
            var duties = dutyBatches.SelectMany(batch => batch).ToList();

            var dayNumbers = new Dictionary<Guid, int>();
            foreach (var driver in drivers)
            {
                var matched = duties.Where(duty => DriverDayCycleCalculator.MatchesDriver(driver, duty)).ToList();
                if (matched.Count == 0 && !DriverDayCycleCalculator.HasBoundTachoIdentity(driver)) continue;
                dayNumbers[driver.Id] = DriverDayCycleCalculator.Calculate(planningDate, matched);
            }

            logger.LogInformation(
                "Driver Dispatch calculated TachoMaster day-cycle values for {MatchedDrivers} of {DriverCount} planning driver(s) using {DutyCount} duty record(s) from {FromDate} to {ThroughDate}.",
                dayNumbers.Count, drivers.Count, duties.Count, fromDate, throughDate);
            return new TachoDayNumberState(true, dayNumbers);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("TachoMaster exceeded the 20 second Driver Dispatch day-cycle budget; TMS run history will be used only as a fallback for this response.");
            return TachoDayNumberState.Unavailable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster duty history was unavailable for Driver Dispatch day-cycle calculation; TMS run history will be used only as a fallback for this response.");
            return TachoDayNumberState.Unavailable;
        }
    }

    private async Task<SageDispatchState> ReadSageStateAsync(DateOnly date, CancellationToken ct)
    {
        try
        {
            // Availability is a shared Staffing snapshot, but Dispatch must not wait on
            // a cold Sage/external refresh before showing the local workforce. Keep this
            // enrichment bounded; the roster itself is sourced from Driver Master below.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var snapshot = await DriverAvailabilityService.ReadAsync(db, date, timeout.Token, sageHr, logger, cache);
            var leave = new Dictionary<string, LeaveState>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in snapshot.Drivers.Where(item => item.BlockReasons.Contains("Leave", StringComparer.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrWhiteSpace(item.EmployeeNumber)) continue;
                leave[Normalise(item.EmployeeNumber)] = new LeaveState("Leave", "Leave is recorded in Staffing availability.", false);
            }
            return new SageDispatchState(snapshot.SageRosterAvailable, new HashSet<string>(StringComparer.OrdinalIgnoreCase), leave);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Staffing availability snapshot exceeded the 8 second Driver Dispatch enrichment budget; local Driver Master roster will be used.");
            return SageDispatchState.Unavailable;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Staffing availability snapshot was unavailable for Driver Dispatch; the canonical Driver Master fallback will be used.");
            return SageDispatchState.Unavailable;
        }
    }

    private static int ConsecutiveWorkedDays(IEnumerable<Load> history, Driver driver, DateOnly planningDate)
    {
        var executedTmsDates = history
            .Where(load => load.DriverId == driver.Id && ExecutedStatuses.Contains(load.Status))
            .Select(load => load.PlanningDate)
            .ToHashSet();

        var count = 0;
        for (var day = planningDate.AddDays(-1); ; day = day.AddDays(-1))
        {
            if (!executedTmsDates.Contains(day)) break;
            count++;
            if (count >= 7) break;
        }
        return count;
    }

    private static void RepairAllocatedDriverAliases(IEnumerable<Load> loads, IReadOnlyCollection<Driver> drivers)
    {
        var byId = drivers.ToDictionary(driver => driver.Id);
        var active = drivers.Where(driver => driver.Active).ToList();
        foreach (var load in loads)
        {
            if (load.DriverId is not Guid storedId || !byId.TryGetValue(storedId, out var stored) || stored.Active) continue;
            var matches = active.Where(candidate => StableDriverIdentityMatch(stored, candidate)).Take(2).ToList();
            if (matches.Count == 1) load.DriverId = matches[0].Id;
        }
    }

    private static bool StableDriverIdentityMatch(Driver left, Driver right)
    {
        var leftEmployee = Normalise(left.EmployeeNumber);
        var rightEmployee = Normalise(right.EmployeeNumber);
        if (leftEmployee.Length > 0 && leftEmployee == rightEmployee) return true;

        var leftMember = Normalise(left.TachoMasterDriverId);
        var rightMember = Normalise(right.TachoMasterDriverId);
        if (leftMember.Length > 0 && leftMember == rightMember) return true;

        var leftCard = Normalise(left.TachoCardNumber);
        var rightCard = Normalise(right.TachoCardNumber);
        if (leftCard.Length > 0 && leftCard == rightCard) return true;

        var leftName = Normalise(left.TachoName ?? left.DisplayName);
        var rightName = Normalise(right.TachoName ?? right.DisplayName);
        return leftName.Length > 0 && leftName == rightName;
    }

    private static DateTimeOffset? FirstPlanned(Load load) => load.Stops.OrderBy(stop => stop.Sequence).Select(stop => stop.PlannedArrivalUtc).FirstOrDefault(value => value is not null);
    private static string CleanStopName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Stop";
        var clean = value.Trim();
        foreach (var prefix in new[] { "Collect · ", "Deliver · ", "Collect - ", "Deliver - " })
            if (clean.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return clean[prefix.Length..].Trim();
        return clean;
    }
    private static bool IsAgency(Driver driver) => new[] { driver.DriverType, driver.DriverGroup, driver.AgencyName }.Any(value => value?.Contains("agency", StringComparison.OrdinalIgnoreCase) == true) || string.Equals(driver.Coding?.Trim(), "4", StringComparison.OrdinalIgnoreCase);
    private static int TypeOrder(string type) => type == "Employed" ? 0 : type == "Casual" ? 1 : type == "Agency" ? 2 : type == "Subcontractor" ? 3 : 4;
    private static string? CanonicalRequestedType(string? value)
    {
        if (string.Equals(value?.Trim(), "Agency", StringComparison.OrdinalIgnoreCase)) return "Agency";
        if (string.Equals(value?.Trim(), "Casual", StringComparison.OrdinalIgnoreCase)) return "Casual";
        if (string.Equals(value?.Trim(), "Employed", StringComparison.OrdinalIgnoreCase)) return "Employed";
        return null;
    }
    private static bool IsSouthbound(Load load)
    {
        var text = $"{load.Reference} {load.PlannerNotes}";
        return text.Contains("southbound", StringComparison.OrdinalIgnoreCase) ||
            text.Split(new char[] {' ', '-', '_', ':', '|'}, StringSplitOptions.RemoveEmptyEntries).Any(token => token.Equals("SB", StringComparison.OrdinalIgnoreCase));
    }
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record LeaveState(string? PolicyName, string? Details, bool IsPartOfDay);
    private sealed record SageDispatchState(bool Available, HashSet<string> ActiveDriverEmployeeNumbers, Dictionary<string, LeaveState> Leave)
    {
        public static SageDispatchState Unavailable { get; } = new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, LeaveState>(StringComparer.OrdinalIgnoreCase));
    }
    private sealed record TachoDayNumberState(bool Available, Dictionary<Guid, int> DayNumbers)
    {
        public static TachoDayNumberState Unavailable { get; } = new(false, new Dictionary<Guid, int>());
    }
}

public sealed record DriverDispatchStartTimeRequest(string? StartTime);
public sealed record DriverDispatchAddDriverRequest(
    string? DisplayName,
    string? EmployeeNumber,
    string? DriverType,
    string? AgencyName,
    DateOnly? StartDate,
    int? Days);
public sealed record DriverDispatchAgencyRosterRequest(Guid DriverId, DateOnly StartDate, int Days);
public sealed record DriverDispatchDriver(
    Guid DriverId,
    string EmployeeNumber,
    string DisplayName,
    string DriverType,
    string? DriverGroup,
    string? Skills,
    string? Coding,
    string? AgencyName,
    string? TachoMasterDriverId,
    string? TachoCardNumber,
    DateOnly? LicenceExpiry,
    string? LicenceStatus,
    int DayNumber,
    bool OnLeave,
    string? LeaveType,
    string? LeaveDetails,
    bool PartDayLeave,
    Guid? PreviousLoadId,
    string? PreviousRunReference,
    Guid? PreviousVehicleId,
    string? PreviousVehicleRegistration,
    string? PreviousFinalStop,
    decimal? PreviousFinalLatitude,
    string? PreviousRoute,
    Guid? AssignedLoadId,
    int AssignedRunCount,
    Guid? SuggestedRunId,
    string? SuggestedRunReference,
    Guid? SuggestedVehicleId,
    string? SuggestedVehicleRegistration,
    int? AssistantScore,
    string? Suggestion,
    DateOnly? AgencyBookedFrom,
    DateOnly? AgencyBookedThrough);
