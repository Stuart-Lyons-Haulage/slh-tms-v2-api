using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/live/vehicles")]
[Authorize(Policy = "TmsWrite")]
public sealed class LiveVehicleDetailsController(
    TmsDbContext db,
    TachoMasterClient tachoMasterClient,
    ILogger<LiveVehicleDetailsController> logger) : ControllerBase
{
    [HttpGet("{vehicleId}/details")]
    [ProducesResponseType(typeof(LiveVehicleDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LiveVehicleDetailResponse>> Get(
        string vehicleId,
        CancellationToken cancellationToken)
    {
        var requested = ExecutionIdentityResolver.NormaliseVehicle(vehicleId);
        if (requested.Length == 0) return NotFound();

        var vehicles = await db.Vehicles
            .AsNoTracking()
            .Where(vehicle => vehicle.Active)
            .ToListAsync(cancellationToken);

        var vehicle = vehicles.FirstOrDefault(item =>
            item.Id.ToString().Equals(vehicleId, StringComparison.OrdinalIgnoreCase) ||
            new[] { item.Registration, item.FleetNumber, item.Abbreviation }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Any(value => ExecutionIdentityResolver.VehicleAliasVariants(value)
                    .Contains(requested, StringComparer.OrdinalIgnoreCase)));

        if (vehicle is null) return NotFound();

        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(
            db,
            new[] { vehicle },
            cancellationToken);
        var aliases = aliasesByVehicle[vehicle.Id];

        var liveStatuses = await db.VehicleLiveStatuses
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var live = ExecutionIdentityResolver.MatchLive(aliases, liveStatuses);

        var today = UkOperatingDate(DateTimeOffset.UtcNow);
        TachoVehicleDriverStatus? tacho = null;
        try
        {
            var tachoStatuses = await tachoMasterClient.GetCurrentDriverStatusesByVehicleAsync(today, cancellationToken);
            tacho = tachoStatuses.Values
                .Where(status => ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, status.VehicleCode))
                .OrderByDescending(status => status.DutyStartUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster live detail enrichment failed for {Vehicle}.", vehicle.Registration);
        }

        var drivers = await db.Drivers
            .AsNoTracking()
            .Where(driver => driver.Active)
            .ToListAsync(cancellationToken);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, cancellationToken);

        var liveCard = NormaliseCard(live?.CurrentDriverCardNumber);
        var tachoCard = NormaliseCard(tacho?.CardNumber);
        var effectiveCard = liveCard.Length > 0 ? liveCard : tachoCard;

        var driver = drivers.FirstOrDefault(item =>
            effectiveCard.Length > 0 && NormaliseCard(item.TachoCardNumber) == effectiveCard)
            ?? drivers.FirstOrDefault(item =>
                tacho is not null && !string.IsNullOrWhiteSpace(item.TachoMasterDriverId) &&
                string.Equals(item.TachoMasterDriverId.Trim(), tacho.MemberCode.ToString(), StringComparison.OrdinalIgnoreCase))
            ?? drivers.FirstOrDefault(item =>
                tacho is not null && !string.IsNullOrWhiteSpace(tacho.EmployeeNumber) &&
                string.Equals(ExecutionIdentityResolver.NormaliseVehicle(item.EmployeeNumber), ExecutionIdentityResolver.NormaliseVehicle(tacho.EmployeeNumber), StringComparison.OrdinalIgnoreCase));

        var currentLoad = await db.Loads
            .AsNoTracking()
            .Include(load => load.Stops)
            .Where(load => load.PlanningDate == today && load.VehicleId == vehicle.Id &&
                           load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed)
            .OrderByDescending(load => load.Status == LoadStatus.InProgress)
            .ThenByDescending(load => load.Status == LoadStatus.Dispatched)
            .FirstOrDefaultAsync(cancellationToken);

        LiveGeofenceSummary? geofence = null;
        if (currentLoad is not null)
        {
            try
            {
                var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, today, new[] { currentLoad }, cancellationToken);
                var activeVisit = snapshot.ActiveVisits
                    .Where(visit => visit.VehicleId == vehicle.Id)
                    .OrderByDescending(visit => visit.LastInsideAtUtc)
                    .FirstOrDefault();
                var latestVisit = snapshot.ConfirmedVisits
                    .Where(visit => visit.VehicleId == vehicle.Id)
                    .OrderByDescending(visit => visit.LastInsideAtUtc)
                    .FirstOrDefault();
                var evidence = activeVisit ?? latestVisit;
                geofence = evidence is null
                    ? new LiveGeofenceSummary("NoVisit", null, null, null, snapshot.LatestTrackingUtc)
                    : new LiveGeofenceSummary(
                        activeVisit is not null ? "Inside" : "LastConfirmedVisit",
                        evidence.Fence.Name,
                        evidence.EnteredAtUtc,
                        evidence.DwellMinutes,
                        snapshot.LatestTrackingUtc);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Geofence live detail enrichment failed for {Vehicle}.", vehicle.Registration);
                geofence = new LiveGeofenceSummary("Unavailable", null, null, null, null);
            }
        }

        var now = DateTimeOffset.UtcNow;
        var ageMinutes = live is null ? (int?)null : Math.Max(0, (int)(now - live.LastEventTimeUtc).TotalMinutes);
        var trackingState = live is null
            ? "Unavailable"
            : ageMinutes > 30
                ? "Stale"
                : live.IsMoving == true || live.SpeedKph.GetValueOrDefault() > 3
                    ? "Moving"
                    : live.IgnitionOn == true ? "EngineOn" : "Stationary";

        var identityState = IdentityState(liveCard, tachoCard, driver);
        var displayName = driver?.DisplayName ?? tacho?.DriverName ?? live?.CurrentDriverName;
        var compliance = ComplianceSummary(identityState, tacho, driver, liveCard);

        return Ok(new LiveVehicleDetailResponse(
            new LiveVehicleSummary(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation),
            new LiveTrackingSummary(
                trackingState,
                live?.LastEventTimeUtc,
                live?.LastReceivedAtUtc,
                live?.Latitude == 0 && live?.Longitude == 0 ? null : live?.Latitude,
                live?.Latitude == 0 && live?.Longitude == 0 ? null : live?.Longitude,
                live?.SpeedKph,
                live?.IgnitionOn,
                live?.IsMoving,
                ageMinutes,
                live?.LastKnownStatus),
            new LiveDriverSummary(
                driver?.Id,
                displayName,
                MaskCard(effectiveCard),
                identityState,
                live?.CurrentDriverName,
                tacho?.DriverName,
                driver?.EmployeeNumber),
            tacho is null ? null : new LiveTachoSummary(
                tacho.MemberCode,
                tacho.DutyStartUtc,
                tacho.DutyEndUtc,
                tacho.DriveMinutes,
                tacho.RestMinutes,
                tacho.DriveAvailableTodayMinutes,
                tacho.WorkAvailableWeekMinutes),
            currentLoad is null ? null : new LiveRunSummary(currentLoad.Id, currentLoad.Reference, currentLoad.Status.ToString()),
            geofence,
            compliance,
            DateTimeOffset.UtcNow));
    }

    private static LiveComplianceSummary ComplianceSummary(
        string identityState,
        TachoVehicleDriverStatus? tacho,
        Driver? driver,
        string liveCard)
    {
        if (identityState == "Mismatch")
            return new LiveComplianceSummary("IdentityMismatch", "Tracking and TachoMaster identify different cards; tacho duty figures are not trusted for this vehicle.");
        if (tacho is null && liveCard.Length > 0)
            return new LiveComplianceSummary("TrackingOnly", "A live driver card is present in tracking, but no matching TachoMaster duty was returned.");
        if (tacho is null)
            return new LiveComplianceSummary("NoTachoDuty", "No TachoMaster duty is available for the current live vehicle identity.");
        if (driver is null)
            return new LiveComplianceSummary("DriverUnlinked", "TachoMaster returned a duty, but its driver identity is not linked to a TMS driver record.");
        return new LiveComplianceSummary("Matched", "Live tracking, TachoMaster duty and the TMS driver identity are aligned.");
    }

    private static string IdentityState(string liveCard, string tachoCard, Driver? driver)
    {
        if (liveCard.Length > 0 && tachoCard.Length > 0 && !string.Equals(liveCard, tachoCard, StringComparison.OrdinalIgnoreCase))
            return "Mismatch";
        if (driver is not null && (liveCard.Length > 0 || tachoCard.Length > 0)) return "Confirmed";
        if (liveCard.Length > 0 || tachoCard.Length > 0) return "CardObserved";
        return "Unconfirmed";
    }

    private static string NormaliseCard(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? MaskCard(string value) =>
        value.Length == 0 ? null : value.Length <= 4 ? value : $"••••{value[^4..]}";

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}

public sealed record LiveVehicleDetailResponse(
    LiveVehicleSummary Vehicle,
    LiveTrackingSummary Tracking,
    LiveDriverSummary Driver,
    LiveTachoSummary? Tacho,
    LiveRunSummary? Run,
    LiveGeofenceSummary? Geofence,
    LiveComplianceSummary Compliance,
    DateTimeOffset RetrievedAtUtc);

public sealed record LiveVehicleSummary(Guid Id, string Registration, string? FleetNumber, string? Abbreviation);
public sealed record LiveTrackingSummary(
    string State,
    DateTimeOffset? LastEventTimeUtc,
    DateTimeOffset? LastReceivedAtUtc,
    decimal? Latitude,
    decimal? Longitude,
    decimal? SpeedKph,
    bool? IgnitionOn,
    bool? IsMoving,
    int? AgeMinutes,
    string? ProviderStatus);
public sealed record LiveDriverSummary(
    Guid? Id,
    string? Name,
    string? MaskedTachoCard,
    string IdentityState,
    string? FalconName,
    string? TachoMasterName,
    string? EmployeeNumber);
public sealed record LiveTachoSummary(
    int MemberCode,
    DateTimeOffset DutyStartUtc,
    DateTimeOffset? DutyEndUtc,
    int DriveMinutes,
    int RestMinutes,
    int? DriveAvailableTodayMinutes,
    int? WorkAvailableWeekMinutes);
public sealed record LiveRunSummary(Guid Id, string Reference, string Status);
public sealed record LiveGeofenceSummary(string State, string? FenceName, DateTimeOffset? EnteredAtUtc, int? DwellMinutes, DateTimeOffset? LatestTrackingUtc);
public sealed record LiveComplianceSummary(string Status, string Message);
