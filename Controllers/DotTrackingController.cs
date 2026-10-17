using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Secure, read-only RoadTech Falcon telemetry preview.
/// It does not create planning records or alter vehicle master data.
/// </summary>
[ApiController]
[Route("api/v1/tracking/dot")]
[Authorize(Policy = "TmsWrite")]
public sealed class DotTrackingController(
    DotTrackingClient trackingClient,
    TachoMasterClient tachoMasterClient,
    TmsDbContext db,
    DotTrackingTelemetryStore telemetryStore,
    ILogger<DotTrackingController> logger) : ControllerBase
{
    [HttpGet("telemetry")]
    [ProducesResponseType(typeof(DotTelemetryResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<DotTelemetryResponse>> GetCurrentTelemetry(
        CancellationToken cancellationToken)
    {
        try
        {
            var telemetry =
                await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);

            var records = telemetry
                .Select(DotTelemetryRecord.FromProvider)
                .ToList();

            if (records.Count == 0)
            {
                return Ok(
                    await StoredTelemetry(
                        "RoadTech Falcon · stored fallback",
                        cancellationToken));
            }

            await telemetryStore.PersistAsync(records, cancellationToken);

            return Ok(new DotTelemetryResponse(
                "RoadTech Falcon",
                DateTimeOffset.UtcNow,
                records.Count,
                records));
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                ex,
                "DOT Tracking configuration is not ready.");

            return Ok(
                await StoredTelemetry(
                    "RoadTech Falcon · stored fallback",
                    cancellationToken));
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "RoadTech Falcon telemetry request failed.");

            return Ok(
                await StoredTelemetry(
                    "RoadTech Falcon · stored fallback",
                    cancellationToken));
        }
    }

    private async Task<DotTelemetryResponse> StoredTelemetry(
        string provider,
        CancellationToken ct)
    {
        var statuses = await db.VehicleLiveStatuses
            .AsNoTracking()
            .OrderBy(status => status.VehicleIdentifier)
            .ToListAsync(ct);

        var records = statuses
            .Select(status => new DotTelemetryRecord(
                $"stored-{status.Id}",
                status.VehicleIdentifier,
                status.LastEventTimeUtc,
                status.Latitude,
                status.Longitude,
                status.SpeedKph,
                status.IgnitionOn,
                status.IsMoving,
                status.LastKnownStatus ?? "Stored position",
                "{}"))
            .ToList();

        return new DotTelemetryResponse(
            provider,
            DateTimeOffset.UtcNow,
            records.Count,
            records);
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(
        [FromQuery] DateOnly? date,
        [FromQuery] string? vehicle,
        [FromQuery] int take = 1000,
        CancellationToken cancellationToken = default)
    {
        var selectedDate =
            date ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var from = new DateTimeOffset(
            selectedDate.ToDateTime(
                TimeOnly.MinValue,
                DateTimeKind.Utc));

        var to = from.AddDays(1);

        var query = db.VehicleTrackingEvents
            .AsNoTracking()
            .Where(item =>
                item.EventTimeUtc >= from &&
                item.EventTimeUtc < to);

        if (!string.IsNullOrWhiteSpace(vehicle))
        {
            query = query.Where(
                item => item.VehicleIdentifier == vehicle.Trim());
        }

        var records = await query
            .OrderBy(item => item.EventTimeUtc)
            .Take(Math.Clamp(take, 1, 5000))
            .Select(item => new
            {
                item.VehicleIdentifier,
                item.EventTimeUtc,
                item.Latitude,
                item.Longitude,
                item.SpeedKph,
                item.IsMoving,
                status = item.MatchStatus
            })
            .ToListAsync(cancellationToken);

        return Ok(new
        {
            provider = "RoadTech Falcon",
            date = selectedDate,
            recordCount = records.Count,
            records
        });
    }

    [HttpGet("fleet-status")]
    public async Task<ActionResult<FleetStatusResponse>> GetFleetStatus(
        CancellationToken cancellationToken)
    {
        var freshLiveStatuses =
            await TryGetProviderLiveStatuses(cancellationToken);

        // Keep tracking available while optional vehicle-master columns
        // are being repaired.
        var vehicleRows = await db.Vehicles
            .AsNoTracking()
            .Where(vehicle => vehicle.Active)
            .OrderBy(vehicle => vehicle.Registration)
            .Select(vehicle => new
            {
                vehicle.Id,
                vehicle.Registration,
                vehicle.FleetNumber,
                vehicle.Abbreviation,
                vehicle.FleetioStatus
            })
            .ToListAsync(cancellationToken);

        var vehicles = vehicleRows
            .Where(vehicle =>
                !Regex.IsMatch(
                    vehicle.Registration,
                    "^C\\d{5,}$",
                    RegexOptions.IgnoreCase))
            .Select(vehicle => new FleetVehicleMaster(
                vehicle.Id,
                vehicle.Registration,
                vehicle.FleetNumber,
                vehicle.Abbreviation,
                vehicle.FleetioStatus,
                null,
                null,
                null,
                null))
            .ToList();

        List<VehicleLiveStatus> liveStatuses =
            freshLiveStatuses;

        if (liveStatuses.Count == 0)
        {
            try
            {
                liveStatuses = await db.VehicleLiveStatuses
                    .AsNoTracking()
                    .ToListAsync(cancellationToken);
            }
            catch (Exception ex)
                when (ex is InvalidOperationException or DbUpdateException)
            {
                logger.LogWarning(
                    ex,
                    "Vehicle live status is unavailable; returning master-data fleet fallback.");

                return Ok(
                    MasterFleetFallback(
                        vehicles,
                        "Master data"));
            }
        }

        var latestByIdentifier = liveStatuses
            .GroupBy(status =>
                NormaliseIdentifier(status.VehicleIdentifier))
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(
                        status => status.LastEventTimeUtc)
                    .First());

        var latestBySuffix = liveStatuses
            .SelectMany(status =>
                IdentifierAliases(status.VehicleIdentifier)
                    .Select(alias => new
                    {
                        Status = status,
                        Alias = alias
                    }))
            .Where(item => item.Alias.Length >= 3)
            .GroupBy(item => item.Alias)
            .ToDictionary(
                group => group.Key,
                group => group
                    .Select(item => item.Status)
                    .OrderByDescending(
                        status => status.LastEventTimeUtc)
                    .First());

        var now = DateTimeOffset.UtcNow;
        var today = UkOperatingDate(now);

        IReadOnlyDictionary<string, TachoVehicleDriverStatus>
            tachoDrivers =
                new Dictionary<string, TachoVehicleDriverStatus>();

        try
        {
            tachoDrivers =
                await tachoMasterClient
                    .GetCurrentDriverStatusesByVehicleAsync(
                        today,
                        cancellationToken);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "TachoMaster driver lookup failed; continuing with live Falcon and allocation names.");
        }

        var assignments =
            await LoadLiveAssignmentsAsync(
                today,
                cancellationToken);

        var driverIds = assignments
            .Where(load => load.DriverId != null)
            .Select(load => load.DriverId!.Value)
            .Distinct()
            .ToList();

        var drivers =
            await LoadDriverIdentities(
                driverIds,
                cancellationToken);

        var tachoMappings =
            await LoadIntegrationMappingsAsync(
                "TachoMaster",
                "Driver",
                cancellationToken);

        var tachoVehicleMappings =
            await LoadIntegrationMappingsAsync(
                "TachoMaster",
                "Vehicle",
                cancellationToken);

        var dotVehicleMappings =
            await LoadIntegrationMappingsAsync(
                "DotTracking",
                "Vehicle",
                cancellationToken);

        var matchedLiveIds = new HashSet<Guid>();

        var matchedTachoKeys =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        var records = vehicles.Select(vehicle =>
        {
            var keys = new[]
            {
                vehicle.Registration,
                vehicle.FleetNumber,
                vehicle.Abbreviation
            }
            .Where(value =>
                !string.IsNullOrWhiteSpace(value))
            .Select(value =>
                NormaliseIdentifier(value!))
            .ToList();

            var aliases = keys
                .SelectMany(IdentifierAliases)
                .Distinct()
                .ToList();

            // Explicit integration mapping aliases.
            var tachoVehicleCode =
                tachoVehicleMappings
                    .FirstOrDefault(
                        mapping =>
                            mapping.Value == vehicle.Id)
                    .Key;

            if (!string.IsNullOrWhiteSpace(tachoVehicleCode))
            {
                aliases.Add(tachoVehicleCode);
            }

            var dotVehicleCode =
                dotVehicleMappings
                    .FirstOrDefault(
                        mapping =>
                            mapping.Value == vehicle.Id)
                    .Key;

            if (!string.IsNullOrWhiteSpace(dotVehicleCode))
            {
                aliases.Add(dotVehicleCode);
            }

            aliases = aliases
                .SelectMany(IdentifierAliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var exactLive = aliases
                .Select(key =>
                    latestByIdentifier.GetValueOrDefault(key))
                .Where(status => status is not null);

            var suffixLive = aliases
                .Select(key =>
                    latestBySuffix.GetValueOrDefault(key))
                .Where(status => status is not null);

            var live = exactLive
                .Concat(suffixLive)
                .OrderByDescending(
                    status => ObservedAt(status!, now))
                .FirstOrDefault();

            if (live is not null)
            {
                matchedLiveIds.Add(live.Id);
            }

            var observedAt =
                live is null
                    ? (DateTimeOffset?)null
                    : ObservedAt(live, now);

            var age =
                observedAt is null
                    ? (TimeSpan?)null
                    : now - observedAt;

            var assignment = assignments
                .Where(load =>
                    load.VehicleId == vehicle.Id)
                .OrderByDescending(load =>
                    LoadPriority(load.Status))
                .FirstOrDefault();

            var allocatedDriver =
                assignment?.DriverId is Guid driverId
                    ? drivers.GetValueOrDefault(driverId)
                    : null;

            var tachoStatus =
                TachoDriverStatus(
                    aliases,
                    tachoDrivers);

            if (tachoStatus is not null)
            {
                matchedTachoKeys.Add(
                    NormaliseIdentifier(
                        tachoStatus.VehicleCode));
            }

            var tachoName =
                CleanDriverName(tachoStatus?.DriverName);

            var falconName =
                CleanDriverName(live?.CurrentDriverName);

            // Driver priority:
            // 1. TachoMaster
            // 2. Live DOT/Falcon identity
            // 3. Planned allocation
            var currentDriverName =
                tachoName ?? falconName;

            var condition = DetermineCondition(
                live,
                !string.IsNullOrWhiteSpace(
                    currentDriverName),
                now);

            var (tachoDriver, matchReason) =
                MatchTachoDriverWithReason(
                    tachoStatus,
                    drivers.Values,
                    tachoMappings);

            var driverName =
                tachoDriver?.DisplayName
                ?? tachoName
                ?? falconName
                ?? allocatedDriver?.DisplayName;

            var driverSource =
                !string.IsNullOrWhiteSpace(tachoName)
                    ? "TachoMaster"
                    : !string.IsNullOrWhiteSpace(falconName)
                        ? "DOT/Falcon"
                        : allocatedDriver is not null
                            ? "Allocation"
                            : null;

            if (matchReason is null &&
                !string.IsNullOrWhiteSpace(falconName))
            {
                matchReason = "DOTLive";
            }

            var driverMismatch =
                !string.IsNullOrWhiteSpace(
                    currentDriverName) &&
                allocatedDriver is not null &&
                !SameDriverName(
                    tachoDriver,
                    currentDriverName!,
                    allocatedDriver);

            var plannedDutyUtc =
                assignment?.Stops
                    .Where(stop =>
                        stop.PlannedArrivalUtc != null)
                    .OrderBy(stop =>
                        stop.PlannedArrivalUtc)
                    .Select(stop =>
                        stop.PlannedArrivalUtc)
                    .FirstOrDefault();

            return new FleetVehicleStatus(
                vehicle.Id,
                vehicle.Registration,
                vehicle.FleetNumber,
                live?.VehicleIdentifier,
                condition,
                observedAt,
                live?.IgnitionOn,
                live?.IsMoving,
                live?.SpeedKph,
                LiveLatitude(live),
                LiveLongitude(live),
                age is null
                    ? null
                    : (int)Math.Max(
                        0,
                        age.Value.TotalMinutes),
                assignment?.Id,
                assignment?.Reference,
                assignment?.Status.ToString(),
                tachoDriver?.Id ?? allocatedDriver?.Id,
                driverName,
                tachoName,
                driverSource,
                allocatedDriver?.DisplayName,
                driverMismatch,
                plannedDutyUtc,
                tachoStatus,
                null,
                null,
                vehicle.FleetioStatus,
                vehicle.FleetioVor,
                vehicle.FleetioPmiDueUtc,
                vehicle.FleetioMotDueUtc,
                vehicle.FleetioServiceStatus,
                matchReason);
        })
        .ToList();

        // Add Falcon vehicles that are live but have not matched
        // a vehicle master record.
        records.AddRange(
            liveStatuses
                .Where(status =>
                    !matchedLiveIds.Contains(status.Id))
                .OrderBy(status =>
                    status.VehicleIdentifier)
                .Select(status =>
                {
                    var observedAt =
                        ObservedAt(status, now);

                    var age =
                        now - observedAt;

                    var aliases =
                        IdentifierAliases(
                            status.VehicleIdentifier);

                    var tachoStatus =
                        TachoDriverStatus(
                            aliases,
                            tachoDrivers);

                    if (tachoStatus is not null)
                    {
                        matchedTachoKeys.Add(
                            NormaliseIdentifier(
                                tachoStatus.VehicleCode));
                    }

                    var tachoName =
                        CleanDriverName(
                            tachoStatus?.DriverName);

                    var falconName =
                        CleanDriverName(
                            status.CurrentDriverName);

                    var currentDriverName =
                        tachoName ?? falconName;

                    var (tachoDriver, matchReason) =
                        MatchTachoDriverWithReason(
                            tachoStatus,
                            drivers.Values,
                            tachoMappings);

                    var driverName =
                        tachoDriver?.DisplayName
                        ?? tachoName
                        ?? falconName;

                    var driverSource =
                        !string.IsNullOrWhiteSpace(tachoName)
                            ? "TachoMaster"
                            : !string.IsNullOrWhiteSpace(falconName)
                                ? "DOT/Falcon"
                                : null;

                    if (matchReason is null &&
                        !string.IsNullOrWhiteSpace(falconName))
                    {
                        matchReason = "DOTLive";
                    }

                    return new FleetVehicleStatus(
                        status.Id,
                        status.VehicleIdentifier,
                        null,
                        status.VehicleIdentifier,
                        DetermineCondition(
                            status,
                            !string.IsNullOrWhiteSpace(
                                currentDriverName),
                            now),
                        observedAt,
                        status.IgnitionOn,
                        status.IsMoving,
                        status.SpeedKph,
                        LiveLatitude(status),
                        LiveLongitude(status),
                        (int)Math.Max(
                            0,
                            age.TotalMinutes),
                        null,
                        null,
                        null,
                        tachoDriver?.Id,
                        driverName,
                        tachoName,
                        driverSource,
                        null,
                        false,
                        null,
                        tachoStatus,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        matchReason);
                }));

        // Add TachoMaster vehicles which have a driver/card
        // but no corresponding RoadTech/master vehicle match.
        records.AddRange(
            tachoDrivers
                .Where(item =>
                    !matchedTachoKeys.Contains(
                        NormaliseIdentifier(
                            item.Value.VehicleCode)))
                .OrderBy(item =>
                    item.Value.VehicleCode)
                .Select(item =>
                {
                    var status =
                        item.Value;

                    var (tachoDriver, matchReason) =
                        MatchTachoDriverWithReason(
                            status,
                            drivers.Values,
                            tachoMappings);

                    return new FleetVehicleStatus(
                        DeterministicGuid(
                            $"tachomaster:{status.VehicleCode}:{status.MemberCode}"),
                        status.VehicleCode,
                        null,
                        status.VehicleCode,
                        "SignedOn",
                        status.DutyStartUtc,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        tachoDriver?.Id,
                        tachoDriver?.DisplayName ??
                            status.DriverName,
                        status.DriverName,
                        "TachoMaster",
                        null,
                        false,
                        null,
                        status,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        matchReason);
                }));

        return Ok(
            new FleetStatusResponse(
                "RoadTech Falcon + TachoMaster",
                now,
                records.Count,
                records.Count(record =>
                    record.Condition == "Moving"),
                records.Count(record =>
                    record.Condition != "Moving"),
                records));
    }

    private async Task<List<VehicleLiveStatus>>
        TryGetProviderLiveStatuses(
            CancellationToken cancellationToken)
    {
        try
        {
            var telemetry =
                await trackingClient
                    .GetLatestVehicleEventsAsync(
                        cancellationToken);

            var records = telemetry
                .Select(DotTelemetryRecord.FromProvider)
                .ToList();

            if (records.Count == 0)
            {
                return [];
            }

            try
            {
                await telemetryStore.PersistAsync(
                    records,
                    cancellationToken);
            }
            catch (Exception ex)
                when (ex is
                    InvalidOperationException or
                    DbUpdateException)
            {
                logger.LogWarning(
                    ex,
                    "RoadTech Falcon cache write failed; using fresh provider telemetry for this response.");
            }

            var receivedAt =
                DateTimeOffset.UtcNow;

            return records
                .Select(record =>
                    new VehicleLiveStatus
                    {
                        Id = Guid.NewGuid(),
                        VehicleIdentifier =
                            record.VehicleIdentifier,
                        LastEventTimeUtc =
                            record.EventTimeUtc,
                        LastReceivedAtUtc =
                            receivedAt,
                        Latitude =
                            record.Latitude ?? 0,
                        Longitude =
                            record.Longitude ?? 0,
                        SpeedKph =
                            record.SpeedKph,
                        IgnitionOn =
                            record.IgnitionOn,
                        IsMoving =
                            record.IsMoving,
                        LastKnownStatus =
                            record.Status,

                        // Critical:
                        // retain live Falcon driver identity
                        // for the fleet-status response.
                        CurrentDriverName =
                            CleanDriverName(
                                record.DriverName),
                        CurrentDriverCardNumber =
                            record.DriverCardNumber
                    })
                .ToList();
        }
        catch (Exception ex)
            when (ex is
                InvalidOperationException or
                HttpRequestException or
                TaskCanceledException)
        {
            logger.LogWarning(
                ex,
                "RoadTech Falcon live refresh failed; using stored fleet status.");

            return [];
        }
    }

    private async Task<Dictionary<Guid, FleetDriverIdentity>>
        LoadDriverIdentities(
            IReadOnlyCollection<Guid> allocatedDriverIds,
            CancellationToken ct)
    {
        try
        {
            // Driver identity is persisted on dbo.Drivers. Keep the staged
            // master-detail enrichment as a compatibility fallback for records written
            // before the identity columns were introduced, then project the complete
            // identity used by live TachoMaster correlation.
            var driverRows = await db.Drivers
                .AsNoTracking()
                .Where(driver =>
                    driver.Active ||
                    allocatedDriverIds.Contains(
                        driver.Id))
                .Select(driver => new Driver
                {
                    Id = driver.Id,
                    EmployeeNumber = driver.EmployeeNumber,
                    DisplayName = driver.DisplayName,
                    TachoName = driver.TachoName,
                    TachoMasterDriverId = driver.TachoMasterDriverId,
                    TachoCardNumber = driver.TachoCardNumber,
                    Active = driver.Active
                })
                .ToListAsync(ct);

            await MasterDetailStore.EnrichDriversAsync(db, driverRows, ct);

            return driverRows
                .Select(driver =>
                    new FleetDriverIdentity(
                        driver.Id,
                        driver.EmployeeNumber,
                        driver.DisplayName,
                        driver.TachoName,
                        driver.TachoMasterDriverId,
                        driver.TachoCardNumber))
                .ToDictionary(driver => driver.Id);
        }
        catch (Exception exception)
            when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(
                exception,
                "Driver TachoName column is unavailable; live TachoMaster names will be shown without master-data correlation.");

            try
            {
                return await db.Drivers
                    .AsNoTracking()
                    .Where(driver =>
                        driver.Active ||
                        allocatedDriverIds.Contains(
                            driver.Id))
                    .Select(driver =>
                        new FleetDriverIdentity(
                            driver.Id,
                            driver.EmployeeNumber,
                            driver.DisplayName,
                            null,
                            null,
                            null))
                    .ToDictionaryAsync(
                        driver => driver.Id,
                        ct);
            }
            catch (Exception fallbackException)
                when (IsSchemaUnavailable(
                    fallbackException))
            {
                logger.LogWarning(
                    fallbackException,
                    "Driver master data is unavailable; continuing with raw TachoMaster and Falcon driver names.");

                return [];
            }
        }
    }

    private async Task<List<Load>> LoadLiveAssignmentsAsync(
        DateOnly today,
        CancellationToken ct)
    {
        var firstDate =
            today.AddDays(-1);

        var lastDate =
            today;

        var assignments =
            new List<Load>();

        try
        {
            assignments = await db.Loads
                .AsNoTracking()
                .Include(load => load.Stops)
                .Where(load =>
                    load.VehicleId != null &&
                    load.Status != LoadStatus.Cancelled &&
                    load.Status != LoadStatus.Completed &&
                    (load.PlanningDate == lastDate ||
                     (load.PlanningDate == firstDate &&
                      (load.Status == LoadStatus.Dispatched ||
                       load.Status == LoadStatus.InProgress))))
                .ToListAsync(ct);
        }
        catch (Exception exception)
            when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(
                exception,
                "Live tracking legacy load assignments are unavailable; using planning-register assignments.");
            db.ChangeTracker.Clear();
        }

        try
        {
            var registerLoads =
                (await PlanningRegisterStore.ReadLoadsAsync(
                    db,
                    null,
                    ct))
                .Where(load =>
                    load.VehicleId != null &&
                    load.Status != LoadStatus.Cancelled &&
                    load.Status != LoadStatus.Completed &&
                    (load.PlanningDate == lastDate ||
                     (load.PlanningDate == firstDate &&
                      (load.Status == LoadStatus.Dispatched ||
                       load.Status == LoadStatus.InProgress))))
                .ToList();

            foreach (var registerLoad in registerLoads)
            {
                var index =
                    assignments.FindIndex(load =>
                        load.Id == registerLoad.Id);

                if (index >= 0)
                {
                    assignments[index] =
                        registerLoad;
                }
                else
                {
                    assignments.Add(
                        registerLoad);
                }
            }
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "Live tracking planning-register assignments are unavailable; continuing with legacy assignments only.");
            db.ChangeTracker.Clear();
        }

        return assignments;
    }

    private static FleetStatusResponse MasterFleetFallback(
        IReadOnlyList<FleetVehicleMaster> vehicles,
        string provider)
    {
        var now =
            DateTimeOffset.UtcNow;

        var records = vehicles
            .Select(vehicle =>
                new FleetVehicleStatus(
                    vehicle.Id,
                    vehicle.Registration,
                    vehicle.FleetNumber,
                    null,
                    "NotSignedOn",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    null,
                    null,
                    null,
                    null,
                    vehicle.FleetioStatus,
                    vehicle.FleetioVor,
                    vehicle.FleetioPmiDueUtc,
                    vehicle.FleetioMotDueUtc,
                    vehicle.FleetioServiceStatus,
                    null))
            .ToList();

        return new FleetStatusResponse(
            provider,
            now,
            records.Count,
            0,
            records.Count,
            records);
    }

    private static bool IsSchemaUnavailable(
        Exception exception)
    {
        var message =
            exception.GetBaseException().Message;

        return
            exception is
                InvalidOperationException or
                DbUpdateException ||
            message.Contains(
                "Invalid object name",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "Cannot find the object",
                StringComparison.OrdinalIgnoreCase) ||
            message.Contains(
                "Invalid column name",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseIdentifier(
        string value) =>
        new(
            value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToUpperInvariant)
                .ToArray());

    private static DateOnly UkOperatingDate(
        DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(
                TimeZoneInfo.ConvertTime(
                    value,
                    TimeZoneInfo.FindSystemTimeZoneById(
                        "Europe/London"))
                .DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(
                value.UtcDateTime);
        }
    }

    private static IReadOnlyList<string>
        IdentifierAliases(string value)
    {
        var normalised =
            NormaliseIdentifier(value);

        if (string.IsNullOrWhiteSpace(
            normalised))
        {
            return [];
        }

        var aliases =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                normalised
            };

        for (
            var length = 3;
            length <= Math.Min(6, normalised.Length);
            length++)
        {
            aliases.Add(
                normalised[^length..]);
        }

        if (
            normalised.Length > 3 &&
            char.IsLetter(normalised[^1]) &&
            normalised[^3..].All(char.IsLetter))
        {
            aliases.Add(
                normalised[^3..]);
        }

        if (
            normalised.Length == 7 &&
            char.IsLetter(normalised[0]) &&
            char.IsLetter(normalised[1]) &&
            char.IsDigit(normalised[2]) &&
            char.IsDigit(normalised[3]))
        {
            aliases.Add(
                normalised[2..]);
        }

        if (
            normalised.EndsWith(
                "H",
                StringComparison.OrdinalIgnoreCase) &&
            normalised.Length > 4)
        {
            aliases.Add(
                normalised[..^1]);
        }

        return aliases
            .Where(alias =>
                !string.IsNullOrWhiteSpace(alias))
            .ToList();
    }

    private static int LoadPriority(
        LoadStatus status) =>
        status switch
        {
            LoadStatus.InProgress => 4,
            LoadStatus.Dispatched => 3,
            LoadStatus.Planned => 2,
            LoadStatus.Draft => 1,
            _ => 0
        };

    private static DateTimeOffset ObservedAt(
        VehicleLiveStatus live,
        DateTimeOffset now) =>
        live.LastEventTimeUtc;

    private static decimal? LiveLatitude(
        VehicleLiveStatus? live) =>
        live is null ||
        (live.Latitude == 0 &&
         live.Longitude == 0)
            ? null
            : live.Latitude;

    private static decimal? LiveLongitude(
        VehicleLiveStatus? live) =>
        live is null ||
        (live.Latitude == 0 &&
         live.Longitude == 0)
            ? null
            : live.Longitude;

    public static string DetermineCondition(
        VehicleLiveStatus? live,
        bool hasDriverCard,
        DateTimeOffset now)
    {
        if (live is null)
        {
            return "NotSignedOn";
        }

        var observedAt =
            ObservedAt(live, now);

        if (
            observedAt.UtcDateTime.Date <
            now.UtcDateTime.Date)
        {
            return "NotSignedOn";
        }

        if (
            now - observedAt >
            TimeSpan.FromMinutes(30))
        {
            return "Stale";
        }

        if (
            live.IsMoving == true ||
            live.SpeedKph.GetValueOrDefault() > 3)
        {
            return "Moving";
        }

        if (live.IgnitionOn == true)
        {
            return "Started";
        }

        if (hasDriverCard)
        {
            return "SignedOn";
        }

        return "NotSignedOn";
    }

    private static TachoVehicleDriverStatus?
        TachoDriverStatus(
            IEnumerable<string> identifiers,
            IReadOnlyDictionary<
                string,
                TachoVehicleDriverStatus> drivers)
    {
        var aliases = identifiers
            .SelectMany(IdentifierAliases)
            .Where(alias => alias.Length >= 3)
            .Distinct(
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var alias in aliases)
        {
            if (drivers.TryGetValue(
                alias,
                out var exact))
            {
                return exact;
            }
        }

        return drivers
            .SelectMany(item =>
                IdentifierAliases(item.Key)
                    .Select(alias => new
                    {
                        Alias = alias,
                        Status = item.Value
                    }))
            .Where(item =>
                aliases.Contains(
                    item.Alias,
                    StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(item =>
                item.Alias.Length)
            .Select(item =>
                item.Status)
            .FirstOrDefault();
    }

    private static Guid DeterministicGuid(
        string value)
    {
        var bytes =
            System.Security.Cryptography.MD5
                .HashData(
                    System.Text.Encoding.UTF8
                        .GetBytes(value));

        return new Guid(bytes);
    }

    public static string NormalisePersonName(
        string? value) =>
        string.Join(
            ' ',
            (value ?? string.Empty)
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(word =>
                    new string(
                        word
                            .Where(char.IsLetterOrDigit)
                            .Select(char.ToUpperInvariant)
                            .ToArray()))
                .Where(word =>
                    word.Length > 0)
                .OrderBy(
                    word => word,
                    StringComparer.Ordinal));

    private static string? CleanDriverName(
        string? value)
    {
        var cleaned =
            (value ?? string.Empty).Trim();

        if (
            cleaned.Length == 0 ||
            cleaned == "0" ||
            cleaned.Equals(
                "unknown",
                StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals(
                "not known",
                StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals(
                "n/a",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return cleaned;
    }

    private async Task<Dictionary<string, Guid>>
        LoadIntegrationMappingsAsync(
            string provider,
            string entityType,
            CancellationToken ct)
    {
        try
        {
            return await db.IntegrationMappings
                .AsNoTracking()
                .Where(mapping =>
                    mapping.Provider == provider &&
                    mapping.TmsEntityType == entityType &&
                    mapping.Active)
                .ToDictionaryAsync(
                    mapping =>
                        mapping.ExternalKey
                            .ToUpperInvariant(),
                    mapping =>
                        mapping.TmsEntityId,
                    ct);
        }
        catch (Exception exception)
            when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(
                exception,
                "IntegrationMappings table is unavailable; proceeding without explicit mappings.");

            return new Dictionary<string, Guid>();
        }
    }

    internal static (
        FleetDriverIdentity? Driver,
        string? Reason)
        MatchTachoDriverWithReason(
            TachoVehicleDriverStatus? tacho,
            IEnumerable<FleetDriverIdentity> drivers,
            IReadOnlyDictionary<string, Guid>
                mappings)
    {
        if (
            tacho is null ||
            string.IsNullOrWhiteSpace(
                tacho.DriverName))
        {
            return (null, null);
        }

        // 1. Explicit mapping by member code
        if (
            tacho.MemberCode > 0 &&
            mappings.TryGetValue(
                tacho.MemberCode.ToString(),
                out var mappedId))
        {
            var driver =
                drivers.FirstOrDefault(
                    driver =>
                        driver.Id == mappedId);

            if (driver is not null)
            {
                return (driver, "Mapped");
            }
        }

        // 2. Master-data Tacho member ID
        if (tacho.MemberCode > 0)
        {
            var memberMatch =
                drivers.FirstOrDefault(
                    driver =>
                        !string.IsNullOrWhiteSpace(
                            driver.TachoMasterDriverId) &&
                        string.Equals(
                            NormaliseIdentifier(
                                driver.TachoMasterDriverId!),
                            tacho.MemberCode.ToString(),
                            StringComparison.OrdinalIgnoreCase));

            if (memberMatch is not null)
            {
                return (
                    memberMatch,
                    "TachoMember");
            }
        }

        // 3. Master-data Tacho card number
        var tachoCard =
            NormaliseIdentifier(
                tacho.CardNumber ?? string.Empty);

        if (tachoCard.Length >= 8)
        {
            var cardMatch =
                drivers.FirstOrDefault(driver =>
                {
                    var driverCard =
                        NormaliseIdentifier(
                            driver.TachoCardNumber ??
                            string.Empty);

                    return
                        driverCard.Length >= 8 &&
                        (string.Equals(
                            driverCard,
                            tachoCard,
                            StringComparison.OrdinalIgnoreCase) ||
                         driverCard.EndsWith(
                            tachoCard,
                            StringComparison.OrdinalIgnoreCase) ||
                         tachoCard.EndsWith(
                            driverCard,
                            StringComparison.OrdinalIgnoreCase));
                });

            if (cardMatch is not null)
            {
                return (
                    cardMatch,
                    "TachoCard");
            }
        }

        // 4. Explicit mapping by employee number
        if (
            !string.IsNullOrWhiteSpace(
                tacho.EmployeeNumber) &&
            mappings.TryGetValue(
                tacho.EmployeeNumber!
                    .ToUpperInvariant(),
                out var mappedEmpId))
        {
            var driver =
                drivers.FirstOrDefault(
                    driver =>
                        driver.Id == mappedEmpId);

            if (driver is not null)
            {
                return (driver, "Mapped");
            }
        }

        // 5. Explicit mapping by driver name
        var nameKey =
            NormalisePersonName(
                tacho.DriverName);

        if (
            nameKey.Length > 0 &&
            mappings.TryGetValue(
                nameKey,
                out var mappedNameId))
        {
            var driver =
                drivers.FirstOrDefault(
                    driver =>
                        driver.Id == mappedNameId);

            if (driver is not null)
            {
                return (driver, "Mapped");
            }
        }

        // 6. Employee-number match
        if (
            !string.IsNullOrWhiteSpace(
                tacho.EmployeeNumber))
        {
            var employeeMatch =
                drivers.FirstOrDefault(
                    driver =>
                        !string.IsNullOrWhiteSpace(
                            driver.EmployeeNumber) &&
                        string.Equals(
                            driver.EmployeeNumber,
                            tacho.EmployeeNumber,
                            StringComparison.OrdinalIgnoreCase));

            if (employeeMatch is not null)
            {
                return (
                    employeeMatch,
                    "EmployeeNumber");
            }
        }

        // 7. Tacho name match
        if (nameKey.Length > 0)
        {
            var tachoNameMatch =
                drivers.FirstOrDefault(
                    driver =>
                        NormalisePersonName(
                            driver.TachoName) ==
                        nameKey);

            if (tachoNameMatch is not null)
            {
                return (
                    tachoNameMatch,
                    "TachoName");
            }
        }

        // 8. Display-name match
        if (nameKey.Length > 0)
        {
            var displayMatch =
                drivers.FirstOrDefault(
                    driver =>
                        NormalisePersonName(
                            driver.DisplayName) ==
                        nameKey);

            if (displayMatch is not null)
            {
                return (
                    displayMatch,
                    "DisplayName");
            }
        }

        return (null, "Unmatched");
    }

    private static bool SameDriverName(
        FleetDriverIdentity? liveMatchedDriver,
        string liveDriverName,
        FleetDriverIdentity allocatedDriver)
    {
        if (liveMatchedDriver is not null)
        {
            return
                liveMatchedDriver.Id ==
                allocatedDriver.Id;
        }

        var liveName =
            NormalisePersonName(
                liveDriverName);

        return
            liveName ==
                NormalisePersonName(
                    allocatedDriver.TachoName) ||
            liveName ==
                NormalisePersonName(
                    allocatedDriver.DisplayName);
    }
}

public sealed record DotTelemetryResponse(
    string Provider,
    DateTimeOffset RetrievedAtUtc,
    int RecordCount,
    IReadOnlyList<DotTelemetryRecord> Records);

public sealed record FleetStatusResponse(
    string Provider,
    DateTimeOffset RetrievedAtUtc,
    int VehicleCount,
    int ReadyCount,
    int AttentionCount,
    IReadOnlyList<FleetVehicleStatus> Vehicles);

public sealed record FleetVehicleStatus(
    Guid VehicleId,
    string Registration,
    string? FleetNumber,
    string? TrackingIdentifier,
    string Condition,
    DateTimeOffset? LastEventTimeUtc,
    bool? IgnitionOn,
    bool? IsMoving,
    decimal? SpeedKph,
    decimal? Latitude,
    decimal? Longitude,
    int? AgeMinutes,
    Guid? LoadId,
    string? LoadReference,
    string? LoadStatus,
    Guid? DriverId,
    string? DriverName,
    string? TachoName,
    string? DriverSource,
    string? AllocatedDriverName,
    bool DriverMismatch,
    DateTimeOffset? PlannedDutyUtc,
    TachoVehicleDriverStatus? Tacho,
    string? FleetioId,
    string? FleetioName,
    string? FleetioStatus,
    bool? FleetioVor,
    DateTimeOffset? FleetioPmiDueUtc,
    DateTimeOffset? FleetioMotDueUtc,
    string? FleetioServiceStatus,
    string? DriverMatchReason);

public sealed record FleetVehicleMaster(
    Guid Id,
    string Registration,
    string? FleetNumber,
    string? Abbreviation,
    string? FleetioStatus,
    bool? FleetioVor,
    DateTimeOffset? FleetioPmiDueUtc,
    DateTimeOffset? FleetioMotDueUtc,
    string? FleetioServiceStatus);

public sealed record FleetDriverIdentity(
    Guid Id,
    string EmployeeNumber,
    string DisplayName,
    string? TachoName,
    string? TachoMasterDriverId,
    string? TachoCardNumber);
