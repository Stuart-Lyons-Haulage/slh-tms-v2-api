using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Live operational coverage for Ops Control.
/// DOT/RoadTech is the source of vehicle location and live driver/card evidence.
/// TachoMaster is the source of tachograph identity and legal-hours data.
/// A planned TMS allocation is reconciliation context only: it must never be treated
/// as proof of the driver actually occupying a vehicle or as authority to attach legal hours.
/// </summary>
[ApiController]
[Route("api/v1/operations")]
[Authorize]
public sealed class OperationsLiveCoverageController(
    TmsDbContext db,
    DotTrackingClient dotTracking,
    TachoMasterClient tachoMaster,
    DotTrackingOptions tracking,
    ILogger<OperationsLiveCoverageController> logger) : ControllerBase
{
    [HttpGet("live-coverage")]
    public async Task<IActionResult> LiveCoverage(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var operatingDate = UkOperatingDate(now);

        var dotRecords = await GetDotRecords(ct);
        var dotProvider = dotRecords.Provider;
        var latestDotEvent = dotRecords.Records.Count == 0 ? (DateTimeOffset?)null : dotRecords.Records.Max(record => record.EventTimeUtc);

        var movingRecords = dotRecords.Records
            .Where(IsMoving)
            .GroupBy(record => NormaliseIdentifier(record.VehicleIdentifier))
            .Select(group => group.OrderByDescending(record => record.EventTimeUtc).First())
            .ToList();

        IReadOnlyDictionary<string, TachoVehicleDriverStatus> tachoStatuses = new Dictionary<string, TachoVehicleDriverStatus>();
        IReadOnlyList<TachoDriverProfile> tachoProfiles = [];
        string? tachoError = null;
        if (tachoMaster.IsConfigured)
        {
            try { tachoStatuses = await tachoMaster.GetCurrentDriverStatusesByVehicleAsync(operatingDate, ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                tachoError = exception.GetBaseException().Message;
                logger.LogWarning(exception, "Live operations coverage could not read TachoMaster vehicle/card identities.");
            }

            try { tachoProfiles = await tachoMaster.GetDriverProfilesAsync(ct); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Live operations coverage could not read the TachoMaster driver profile directory.");
            }
        }

        var vehicles = await LoadVehicles(ct);
        var vehicleAliases = BuildVehicleAliasLookup(vehicles);
        var allocations = await LoadAllocations(operatingDate, ct);
        var allocationsByVehicle = allocations
            .GroupBy(allocation => allocation.VehicleId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(allocation => LoadPriority(allocation.LoadStatus)).First());

        var tachoAliases = BuildTachoAliasLookup(tachoStatuses);
        var movingWithLiveTacho = 0;
        var movingWithLiveTachoMember = 0;
        var movingWithTachoProfile = 0;
        var movingWithPlannedAllocation = 0;
        var movingWithLiveCardOrName = 0;
        var movingWithoutTacho = new List<UnmatchedMovingVehicle>();
        var movingVehicleRows = new List<LiveVehicleCoverageRow>();

        foreach (var record in movingRecords.OrderBy(record => record.VehicleIdentifier))
        {
            var aliases = IdentifierAliases(record.VehicleIdentifier);
            var vehicle = aliases
                .Select(alias => vehicleAliases.GetValueOrDefault(alias))
                .Where(value => value is not null)
                .OrderByDescending(value => value!.Registration.Length)
                .FirstOrDefault();

            var tachoMatchAliases = VehicleTachoMatchAliases(record.VehicleIdentifier, vehicle?.Registration, vehicle?.FleetNumber, vehicle?.Abbreviation);
            var liveTachoStatus = tachoMatchAliases
                .Select(alias => tachoAliases.GetValueOrDefault(alias))
                .Where(value => value is not null)
                .OrderByDescending(value => value!.VehicleCode.Length)
                .FirstOrDefault();

            AllocationLite? allocation = null;
            if (vehicle is not null) allocationsByVehicle.TryGetValue(vehicle.Id, out allocation);
            if (allocation is not null) movingWithPlannedAllocation++;

            // Legal-hours enrichment is permitted only when the live source proves identity.
            // Planned allocation matching is retained below solely to explain reconciliation gaps.
            var liveDotProfile = liveTachoStatus is null ? MatchLiveDriverProfile(record, tachoProfiles) : null;
            var plannedProfile = allocation is null ? null : MatchDriverProfile(allocation.Driver, tachoProfiles);

            if (liveTachoStatus is not null)
            {
                movingWithLiveTacho++;
                if (liveTachoStatus.MemberCode > 0) movingWithLiveTachoMember++;
            }
            else if (liveDotProfile is not null)
            {
                movingWithTachoProfile++;
            }
            else
            {
                movingWithoutTacho.Add(new UnmatchedMovingVehicle(
                    record.VehicleIdentifier,
                    record.EventTimeUtc,
                    record.SpeedKph,
                    record.DriverName,
                    !string.IsNullOrWhiteSpace(record.DriverCardNumber),
                    allocation?.Driver.DisplayName,
                    allocation?.LoadReference,
                    !string.IsNullOrWhiteSpace(record.DriverCardNumber) || !string.IsNullOrWhiteSpace(record.DriverName)
                        ? "DOT reports a live driver/card, but it did not match the TachoMaster member directory. Legal hours were not attached."
                        : plannedProfile is not null
                            ? "A planned driver has a TachoMaster profile, but no live Tacho/DOT identity confirms that driver is actually in this vehicle. Legal hours were not attached."
                            : allocation is null
                                ? "No live Tacho/DOT driver identity is available for this moving vehicle."
                                : "A driver is planned on this vehicle, but the planned name is not accepted as live proof of identity."));
            }

            if (!string.IsNullOrWhiteSpace(record.DriverName) || !string.IsNullOrWhiteSpace(record.DriverCardNumber)) movingWithLiveCardOrName++;

            var status = liveTachoStatus is not null
                ? liveTachoStatus.MemberCode > 0 ? "LiveTachoCardConfirmed" : "LiveIdentityOnly"
                : liveDotProfile is not null ? "LiveDotTachoProfile"
                : allocation is not null ? "PlannedOnlyUnconfirmed"
                : "Attention";

            var source = liveTachoStatus is not null ? "LiveTachoCard"
                : liveDotProfile is not null ? "LiveDOT→TachoMasterProfile"
                : null;

            movingVehicleRows.Add(new LiveVehicleCoverageRow(
                record.VehicleIdentifier,
                record.EventTimeUtc,
                record.SpeedKph,
                record.Latitude,
                record.Longitude,
                liveTachoStatus?.DriverName ?? liveDotProfile?.DriverName,
                liveTachoStatus?.CardNumber ?? liveDotProfile?.CardNumber,
                liveTachoStatus?.MemberCode > 0 || liveDotProfile is not null,
                allocation?.Driver.DisplayName,
                allocation?.LoadReference,
                source,
                status));
        }

        var tachoDutyLike = tachoStatuses.Values.Count(status => status.DriveMinutes > 0 || status.WorkMinutes > 0 || status.AvailableMinutes > 0 || status.RestMinutes > 0);
        var tachoMemberIdentities = tachoStatuses.Values.Count(status => status.MemberCode > 0);
        var liveOnlyIdentities = Math.Max(0, tachoStatuses.Count - tachoMemberIdentities);
        var actualTachoCoverage = movingWithLiveTacho + movingWithTachoProfile;

        return Ok(new LiveCoverageResponse(
            now,
            operatingDate,
            new DotCoverage(tracking.IsConfigured, dotProvider, dotRecords.Records.Count, movingRecords.Count, latestDotEvent),
            new TachoCoverage(tachoMaster.IsConfigured, tachoError is null && tachoMaster.IsConfigured, tachoStatuses.Count, tachoDutyLike, tachoMemberIdentities, liveOnlyIdentities, tachoProfiles.Count, tachoError),
            new LiveCoverageSummary(movingRecords.Count, actualTachoCoverage, movingWithLiveTachoMember + movingWithTachoProfile, movingWithLiveCardOrName, Math.Max(0, movingRecords.Count - actualTachoCoverage), movingWithoutTacho.Count, movingWithLiveTacho, movingWithLiveTachoMember, movingWithTachoProfile, movingWithPlannedAllocation),
            movingVehicleRows,
            movingWithoutTacho));
    }

    private async Task<DotCoverageRecords> GetDotRecords(CancellationToken ct)
    {
        try
        {
            var items = await dotTracking.GetLatestVehicleEventsAsync(ct);
            var records = items.Select(DotTelemetryRecord.FromProvider).ToList();
            if (records.Count > 0) return new DotCoverageRecords("RoadTech Falcon live", records);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(exception, "Live DOT/RoadTech coverage check failed; using stored live-status fallback.");
        }

        try
        {
            var stored = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
            var records = stored.Select(status => new DotTelemetryRecord(
                $"stored-{status.Id}", status.VehicleIdentifier, status.LastEventTimeUtc, status.Latitude, status.Longitude,
                status.SpeedKph, status.IgnitionOn, status.IsMoving, status.LastKnownStatus ?? "Stored DOT position", "{}", status.CurrentDriverName, null)).ToList();
            return new DotCoverageRecords("RoadTech Falcon stored fallback", records);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Stored DOT/RoadTech live status is unavailable.");
            return new DotCoverageRecords("RoadTech Falcon unavailable", []);
        }
    }

    private async Task<List<VehicleLite>> LoadVehicles(CancellationToken ct)
    {
        try
        {
            return await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active)
                .Select(vehicle => new VehicleLite(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation)).ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Vehicle master data is unavailable for live TachoMaster coverage.");
            return [];
        }
    }

    private async Task<List<AllocationLite>> LoadAllocations(DateOnly operatingDate, CancellationToken ct)
    {
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking()
                .Where(load => load.PlanningDate == operatingDate && load.VehicleId != null && load.DriverId != null && load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Dedicated Loads table unavailable; using planning register for live TachoMaster allocations.");
            try { loads = (await PlanningRegisterStore.ReadLoadsAsync(db, operatingDate, ct)).Where(load => load.VehicleId != null && load.DriverId != null && load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed).ToList(); }
            catch (Exception fallbackException) when (fallbackException is not OperationCanceledException)
            {
                logger.LogWarning(fallbackException, "Planning register allocation fallback unavailable.");
                return [];
            }
        }

        try
        {
            var driverIds = loads.Where(load => load.DriverId != null).Select(load => load.DriverId!.Value).Distinct().ToList();
            var drivers = await db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id))
                .Select(driver => new DriverLite(driver.Id, driver.EmployeeNumber, driver.DisplayName, driver.TachoName))
                .ToDictionaryAsync(driver => driver.Id, ct);

            return loads.Where(load => load.VehicleId != null && load.DriverId != null && drivers.ContainsKey(load.DriverId.Value))
                .Select(load => new AllocationLite(load.VehicleId!.Value, load.Reference, load.Status.ToString(), drivers[load.DriverId!.Value])).ToList();
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            logger.LogWarning(exception, "Driver master data is unavailable for allocation reconciliation context.");
            return [];
        }
    }

    private static Dictionary<string, VehicleLite> BuildVehicleAliasLookup(IEnumerable<VehicleLite> vehicles)
    {
        var lookup = new Dictionary<string, VehicleLite>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in vehicles)
            foreach (var identifier in new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation })
            {
                if (string.IsNullOrWhiteSpace(identifier)) continue;
                foreach (var alias in IdentifierAliases(identifier)) if (!lookup.ContainsKey(alias)) lookup[alias] = vehicle;
            }
        return lookup;
    }

    private static Dictionary<string, TachoVehicleDriverStatus> BuildTachoAliasLookup(IReadOnlyDictionary<string, TachoVehicleDriverStatus> statuses)
    {
        var lookup = new Dictionary<string, TachoVehicleDriverStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var status in statuses.Values)
            foreach (var alias in IdentifierAliases(status.VehicleCode)) if (!lookup.ContainsKey(alias)) lookup[alias] = status;
        return lookup;
    }

    private static TachoDriverProfile? MatchLiveDriverProfile(DotTelemetryRecord record, IReadOnlyList<TachoDriverProfile> profiles)
    {
        if (profiles.Count == 0) return null;
        var card = NormaliseIdentifier(record.DriverCardNumber ?? string.Empty);
        if (card.Length >= 8)
        {
            var byCard = profiles.FirstOrDefault(profile =>
            {
                var profileCard = NormaliseIdentifier(profile.CardNumber ?? string.Empty);
                return profileCard.Length >= 8 && (profileCard == card || profileCard.EndsWith(card, StringComparison.OrdinalIgnoreCase) || card.EndsWith(profileCard, StringComparison.OrdinalIgnoreCase));
            });
            if (byCard is not null) return byCard;
        }

        var name = NormalisePersonName(record.DriverName);
        return name.Length == 0 ? null : profiles.FirstOrDefault(profile => NormalisePersonName(profile.DriverName) == name);
    }

    private static TachoDriverProfile? MatchDriverProfile(DriverLite? driver, IReadOnlyList<TachoDriverProfile> profiles)
    {
        if (driver is null || profiles.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber))
        {
            var employee = profiles.FirstOrDefault(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber) && string.Equals(profile.EmployeeNumber.Trim(), driver.EmployeeNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (employee is not null) return employee;
        }
        var tachoName = NormalisePersonName(driver.TachoName);
        if (tachoName.Length > 0)
        {
            var byTachoName = profiles.FirstOrDefault(profile => NormalisePersonName(profile.DriverName) == tachoName);
            if (byTachoName is not null) return byTachoName;
        }
        var displayName = NormalisePersonName(driver.DisplayName);
        return displayName.Length == 0 ? null : profiles.FirstOrDefault(profile => NormalisePersonName(profile.DriverName) == displayName);
    }

    private static bool IsMoving(DotTelemetryRecord record) => record.IsMoving == true || record.SpeedKph.GetValueOrDefault() > 3;
    private static string NormaliseIdentifier(string value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string NormalisePersonName(string? value) => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray())).Where(word => word.Length > 0).OrderBy(word => word, StringComparer.Ordinal));

    internal static IReadOnlyList<string> VehicleTachoMatchAliases(string dotIdentifier, string? registration, string? fleetNumber, string? abbreviation)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVehicleAliases(aliases, dotIdentifier);
        AddVehicleAliases(aliases, registration);
        AddVehicleAliases(aliases, fleetNumber, allowShortIdentifier: true);
        AddVehicleAliases(aliases, abbreviation);
        return aliases.ToList();
    }

    private static void AddVehicleAliases(HashSet<string> aliases, string? value, bool allowShortIdentifier = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var alias in IdentifierAliases(value)) aliases.Add(alias);
        var normalised = NormaliseIdentifier(value);
        if (allowShortIdentifier && normalised.Length >= 2) aliases.Add(normalised);
    }

    private static IReadOnlyList<string> IdentifierAliases(string value)
    {
        var normalised = NormaliseIdentifier(value);
        if (string.IsNullOrWhiteSpace(normalised)) return [];
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { normalised };
        for (var length = 3; length <= Math.Min(6, normalised.Length); length++) aliases.Add(normalised[^length..]);
        if (normalised.Length == 7 && char.IsLetter(normalised[0]) && char.IsLetter(normalised[1]) && char.IsDigit(normalised[2]) && char.IsDigit(normalised[3])) aliases.Add(normalised[2..]);
        if (normalised.EndsWith("H", StringComparison.OrdinalIgnoreCase) && normalised.Length > 4) aliases.Add(normalised[..^1]);
        return aliases.Where(alias => alias.Length >= 3).ToList();
    }

    private static int LoadPriority(string status) => status switch { nameof(LoadStatus.InProgress) => 4, nameof(LoadStatus.Dispatched) => 3, nameof(LoadStatus.Planned) => 2, nameof(LoadStatus.Draft) => 1, _ => 0 };

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }

    private sealed record DotCoverageRecords(string Provider, IReadOnlyList<DotTelemetryRecord> Records);
    private sealed record VehicleLite(Guid Id, string Registration, string? FleetNumber, string? Abbreviation);
    private sealed record DriverLite(Guid Id, string EmployeeNumber, string DisplayName, string? TachoName);
    private sealed record AllocationLite(Guid VehicleId, string LoadReference, string LoadStatus, DriverLite Driver);
}

public sealed record LiveCoverageResponse(DateTimeOffset GeneratedAtUtc, DateOnly OperatingDate, DotCoverage Dot, TachoCoverage TachoMaster, LiveCoverageSummary Summary, IReadOnlyList<LiveVehicleCoverageRow> Vehicles, IReadOnlyList<UnmatchedMovingVehicle> UnmatchedMovingVehicles);
public sealed record DotCoverage(bool Configured, string Provider, int LiveVehicleCount, int MovingVehicleCount, DateTimeOffset? LatestEventUtc);
public sealed record TachoCoverage(bool Configured, bool Connected, int VehicleIdentityCount, int DutyRecordCount, int TachoMemberIdentityCount, int LiveOnlyIdentityCount, int DriverProfileCount, string? Error);
public sealed record LiveCoverageSummary(int MovingVehicles, int MovingWithTachoIdentity, int MovingWithTachoMemberMatch, int MovingWithLiveCardOrNameFromDot, int MovingWithoutTachoIdentity, int AttentionCount, int MovingWithLiveTachoIdentity, int MovingWithLiveTachoMemberMatch, int MovingWithTachoDirectoryProfile, int MovingWithPlannedAllocation);
public sealed record LiveVehicleCoverageRow(string VehicleIdentifier, DateTimeOffset LastEventUtc, decimal? SpeedKph, decimal? Latitude, decimal? Longitude, string? TachoDriverName, string? TachoCardNumber, bool TachoMemberMatched, string? PlannedDriverName, string? PlannedLoadReference, string? DriverSource, string Status);
public sealed record UnmatchedMovingVehicle(string VehicleIdentifier, DateTimeOffset LastEventUtc, decimal? SpeedKph, string? DotDriverName, bool DotDriverCardDetected, string? PlannedDriverName, string? PlannedLoadReference, string Reason);
