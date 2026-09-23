using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record RunTachoEvidence(
    string Status,
    string? DriverName,
    string? VehicleCode,
    DateTimeOffset? SignOnUtc,
    DateTimeOffset? DutyEndUtc,
    int? DriveAvailableTodayMinutes,
    int? DriveAvailableWeekMinutes,
    int? WorkAvailableWeekMinutes,
    bool CardConfirmed,
    bool LegalHoursAvailable,
    string? EvidenceSource,
    string Explanation);

public sealed record RunTachoEvidenceResult(
    IReadOnlyDictionary<Guid, RunTachoEvidence> ByLoadId,
    bool Available,
    string? Warning,
    int ProviderVehicles = 0,
    int ProviderEvidenceRecords = 0,
    int TachoDutyRecords = 0,
    int FalconCardRecords = 0,
    IReadOnlyDictionary<string, int>? StatusCounts = null);

public static class RunTachoEvidenceResolver
{
    private static readonly TimeSpan RequestPathBudget = TimeSpan.FromSeconds(4);

    public static async Task<RunTachoEvidenceResult> ResolveAsync(
        TmsDbContext db,
        TachoMasterClient tachoMaster,
        IReadOnlyCollection<Load> loads,
        DateOnly planningDate,
        ILogger logger,
        CancellationToken ct)
    {
        if (loads.Count == 0)
            return new RunTachoEvidenceResult(new Dictionary<Guid, RunTachoEvidence>(), tachoMaster.IsConfigured, null);

        var drivers = await LoadDriversAsync(db, loads, ct);
        var vehicles = await LoadVehiclesAsync(db, loads, ct);
        var aliasesByVehicle = await ExecutionIdentityResolver.VehicleAliasesAsync(db, vehicles.Values, ct);

        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> allStatuses =
            new Dictionary<string, IReadOnlyList<TachoVehicleDriverStatus>>();
        var available = tachoMaster.IsConfigured;
        string? warning = null;

        if (!tachoMaster.IsConfigured)
        {
            warning = $"TachoMaster sign-on evidence is not configured: {string.Join(", ", tachoMaster.MissingSettings)}.";
        }
        else
        {
            try
            {
                // TachoMaster is enrichment, not a prerequisite for rendering the
                // operations wallboard. Keep provider latency bounded so the page
                // can use the latest local evidence when the provider is slow.
                using var providerBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                providerBudget.CancelAfter(RequestPathBudget);
                allStatuses = await tachoMaster.GetAllDriverStatusesByVehicleAsync(planningDate, providerBudget.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                available = false;
                warning = $"TachoMaster sign-on evidence exceeded the {RequestPathBudget.TotalSeconds:0}-second refresh budget; using stored evidence.";
                logger.LogWarning("TachoMaster sign-on lookup exceeded the {Seconds}-second request budget for run evidence on {PlanningDate}.", RequestPathBudget.TotalSeconds, planningDate);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                available = false;
                warning = "TachoMaster sign-on evidence is unavailable on this refresh.";
                logger.LogWarning(exception, "TachoMaster sign-on lookup failed for run evidence on {PlanningDate}.", planningDate);
            }
        }

        var currentStatuses = CurrentStatuses(allStatuses);
        var providerEvidence = allStatuses.Values.SelectMany(items => items).ToList();
        var tachoDutyRecords = providerEvidence.Count(item =>
            string.Equals(item.EvidenceSource, "TachoMasterDuty", StringComparison.OrdinalIgnoreCase));
        var falconCardRecords = providerEvidence.Count(item =>
            string.Equals(item.EvidenceSource, "FalconLiveCard", StringComparison.OrdinalIgnoreCase));

        var result = new Dictionary<Guid, RunTachoEvidence>();
        foreach (var load in loads)
        {
            var driver = load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var matchedDriver) ? matchedDriver : null;
            var vehicle = load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var matchedVehicle) ? matchedVehicle : null;
            var aliases = vehicle is not null && aliasesByVehicle.TryGetValue(vehicle.Id, out var knownAliases)
                ? knownAliases
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var current = available && aliases.Count > 0
                ? ExecutionIdentityResolver.MatchLiveDriverIdentityForVehicle(aliases, driver, currentStatuses)
                : null;
            var historical = available && current is null && aliases.Count > 0
                ? ExecutionIdentityResolver.MatchTachoForDriver(aliases, driver, allStatuses)
                : null;

            // TachoMaster can contain the correct driver duty while the provider vehicle key
            // has not yet been reconciled to the TMS registration/alias. Do not tell operations
            // that sign-on evidence is unavailable when exact driver card/member/name evidence
            // exists. This fallback proves driver identity; it does not silently repair the
            // vehicle mapping and its explanation remains explicit.
            var driverFallback = available && current is null && historical is null && driver is not null
                ? MatchDriverAnywhere(driver, allStatuses)
                : null;
            var selected = current ?? historical ?? driverFallback;
            var driverIdentityFallback = driverFallback is not null;
            var historicalOnly = current is null && selected is not null && selected.DutyEndUtc is not null;

            var status = !available
                ? "Unavailable"
                : driver is null
                    ? "NoPlannedDriver"
                    : vehicle is null
                        ? "NoPlannedVehicle"
                        : EvidenceStatus(driver, selected, historicalOnly);

            var driveAvailableToday = historicalOnly ? null : selected?.DriveAvailableTodayMinutes;
            var driveAvailableWeek = historicalOnly ? null : selected?.DriveAvailableWeekMinutes;
            var workAvailableWeek = historicalOnly ? null : selected?.WorkAvailableWeekMinutes;
            var evidenceSource = historicalOnly
                ? driverIdentityFallback ? "TachoMasterDayDutyDriverIdentity" : "TachoMasterDayDuty"
                : driverIdentityFallback && selected is not null ? $"{selected.EvidenceSource}DriverIdentity" : selected?.EvidenceSource;

            result[load.Id] = new RunTachoEvidence(
                status,
                selected?.DriverName,
                selected?.VehicleCode,
                selected?.DutyStartUtc,
                selected?.DutyEndUtc,
                driveAvailableToday,
                driveAvailableWeek,
                workAvailableWeek,
                selected is not null,
                !historicalOnly && driveAvailableToday is not null,
                evidenceSource,
                Explanation(available, driver, vehicle, selected, historicalOnly, driverIdentityFallback));
        }

        var statusCounts = result.Values
            .GroupBy(item => item.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        logger.LogInformation(
            "Run Tacho evidence for {PlanningDate}: providerVehicles={ProviderVehicles}, providerEvidence={ProviderEvidence}, TachoDuties={TachoDutyRecords}, FalconCards={FalconCardRecords}, currentEvidenceVehicles={CurrentEvidenceVehicles}, runStatuses={RunStatuses}.",
            planningDate,
            allStatuses.Count,
            providerEvidence.Count,
            tachoDutyRecords,
            falconCardRecords,
            currentStatuses.Count,
            string.Join(", ", statusCounts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));

        return new RunTachoEvidenceResult(
            result,
            available,
            warning,
            allStatuses.Count,
            providerEvidence.Count,
            tachoDutyRecords,
            falconCardRecords,
            statusCounts);
    }

    private static TachoVehicleDriverStatus? MatchDriverAnywhere(
        Driver driver,
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> allStatuses)
    {
        return allStatuses.Values
            .SelectMany(items => items)
            .Where(status => ExecutionIdentityResolver.DriverMatches(driver, status))
            .OrderByDescending(status => status.DutyEndUtc is null)
            .ThenByDescending(status => string.Equals(status.EvidenceSource, "TachoMasterDuty", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(status => status.DutyStartUtc)
            .FirstOrDefault();
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> CurrentStatuses(
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> allStatuses)
    {
        return allStatuses
            .Select(pair => new
            {
                pair.Key,
                Values = pair.Value.Where(status =>
                    string.Equals(status.EvidenceSource, "FalconLiveCard", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(status.EvidenceSource, "TachoMasterDuty", StringComparison.OrdinalIgnoreCase) && status.DutyEndUtc is null)
                    .ToList()
            })
            .Where(pair => pair.Values.Count > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TachoVehicleDriverStatus>)pair.Values,
                StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<Guid, Driver>> LoadDriversAsync(TmsDbContext db, IReadOnlyCollection<Load> loads, CancellationToken ct)
    {
        var ids = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, Driver>();

        var drivers = await db.Drivers
            .AsNoTracking()
            .Where(driver => ids.Contains(driver.Id))
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
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        return drivers.ToDictionary(driver => driver.Id);
    }

    private static async Task<Dictionary<Guid, Vehicle>> LoadVehiclesAsync(TmsDbContext db, IReadOnlyCollection<Load> loads, CancellationToken ct)
    {
        var ids = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, Vehicle>()
            : await db.Vehicles.AsNoTracking().Where(vehicle => ids.Contains(vehicle.Id)).ToDictionaryAsync(vehicle => vehicle.Id, ct);
    }

    private static string Explanation(
        bool available,
        Driver? driver,
        Vehicle? vehicle,
        TachoVehicleDriverStatus? tacho,
        bool historicalOnly,
        bool driverIdentityFallback)
    {
        if (!available) return "TachoMaster could not be reached for this refresh.";
        if (driver is null) return "No planned driver is allocated to this run.";
        if (vehicle is null) return "No planned vehicle is allocated to this run.";
        if (tacho is null) return "No Falcon current-driver evidence, open TachoMaster duty or same-day TachoMaster duty was matched to the planned driver.";
        if (!ExecutionIdentityResolver.DriverMatches(driver, tacho))
            return $"Driver evidence is present for {tacho.DriverName}, but it does not match the planned driver.";
        if (historicalOnly)
            return driverIdentityFallback
                ? $"{tacho.DriverName} has a same-day TachoMaster duty from {tacho.DutyStartUtc:O} to {tacho.DutyEndUtc:O}. Driver identity is confirmed; the provider vehicle alias still needs reconciliation. Closed-duty legal-hours figures are not used for a live ETA."
                : $"{tacho.DriverName} has a same-day TachoMaster duty on the planned vehicle from {tacho.DutyStartUtc:O} to {tacho.DutyEndUtc:O}. This proves historical sign-on identity only; closed-duty legal-hours figures are not used for a live ETA.";
        if (driverIdentityFallback)
            return $"{tacho.DriverName} is confirmed by live/same-day Tacho evidence. The Tacho provider vehicle key did not match the planned TMS vehicle alias, so driver identity is shown while vehicle mapping remains separate.";
        if (tacho.EvidenceSource == "FalconLiveCard")
            return tacho.DriveAvailableTodayMinutes is null
                ? $"{tacho.DriverName} is confirmed by Falcon live card/driver evidence at {tacho.DutyStartUtc:O}; TachoMaster did not return legal-hours metrics."
                : $"{tacho.DriverName} is confirmed by Falcon live card/driver evidence at {tacho.DutyStartUtc:O}; TachoMaster profile metrics are attached for hours checks.";
        return $"{tacho.DriverName} has an open TachoMaster duty from {tacho.DutyStartUtc:O}.";
    }

    private static string EvidenceStatus(Driver? driver, TachoVehicleDriverStatus? tacho, bool historicalOnly)
    {
        if (driver is null) return "NoPlannedDriver";
        if (tacho is null) return "NoTachoDuty";
        if (!ExecutionIdentityResolver.DriverMatches(driver, tacho)) return "Mismatch";
        if (historicalOnly) return "DutyConfirmed";
        return tacho.EvidenceSource == "FalconLiveCard" ? "CardConfirmed" : "Matched";
    }
}
