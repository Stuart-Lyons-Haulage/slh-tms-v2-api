using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/daily-compliance")]
[Authorize]
public sealed class DailyComplianceController(
    TmsDbContext db,
    TachoMasterClient tachoMaster,
    TachoMasterOptions tachoOptions,
    FleetioOptions fleetioOptions,
    FleetioClient fleetioClient,
    IHttpClientFactory httpClientFactory,
    ILogger<DailyComplianceController> logger) : ControllerBase
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private int fleetioReadAttempts;
    private int fleetioReadFailures;
    private string? fleetioLastError;

    [HttpGet("report")]
    public async Task<IActionResult> Report([FromQuery] DateOnly date, CancellationToken ct)
        => Ok(await BuildReport(date, ct));

    [HttpGet("export.csv")]
    public async Task<IActionResult> Export([FromQuery] DateOnly date, CancellationToken ct)
    {
        var report = await BuildReport(date, ct);
        var csv = new StringBuilder();
        csv.AppendLine("Date,Asset Type,Asset,Run,Driver,Employment Type,Tacho Duty Start,Tacho First Drive,Other Work Before First Drive Minutes,Tacho Work Minutes,Tacho Drive Minutes,Tacho Rest Minutes,Tacho Available Minutes,Tacho Break Minutes,First Movement,Fleetio Form,Fleetio Submitted,Fleetio User,Fleetio Driver Match,Failed Items,Status,Reason");
        foreach (var row in report.Rows)
        {
            csv.AppendLine(string.Join(',', new[]
            {
                report.Date.ToString("yyyy-MM-dd"), row.AssetType, row.AssetName, row.RunReference, row.DriverName,
                row.EmploymentType, row.TachoDutyStartUtc?.ToString("O") ?? string.Empty,
                row.TachoFirstDriveUtc?.ToString("O") ?? string.Empty,
                row.TachoPreUseOtherWorkMinutes?.ToString() ?? string.Empty,
                row.TachoWorkMinutes?.ToString() ?? string.Empty,
                row.TachoDriveMinutes?.ToString() ?? string.Empty,
                row.TachoRestMinutes?.ToString() ?? string.Empty,
                row.TachoAvailableMinutes?.ToString() ?? string.Empty,
                row.TachoBreakMinutes?.ToString() ?? string.Empty,
                row.FirstMovementUtc?.ToString("O") ?? string.Empty,
                row.FleetioForm ?? string.Empty, row.FleetioSubmittedAtUtc?.ToString("O") ?? string.Empty,
                row.FleetioUser ?? string.Empty, row.FleetioDriverMatched?.ToString() ?? string.Empty,
                row.FleetioFailedItems?.ToString() ?? string.Empty, row.Status, row.Reason
            }.Select(Csv)));
        }
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"daily-compliance-{date:yyyy-MM-dd}.csv");
    }

    private async Task<ComplianceReport> BuildReport(DateOnly date, CancellationToken ct)
    {
        fleetioReadAttempts = 0;
        fleetioReadFailures = 0;
        fleetioLastError = null;

        var (dayStartUtc, dayEndUtc) = OperatingWindowUtc(date);
        var loads = await ReadLoads(date, ct);
        var drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Registration).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(x => x.Active).OrderBy(x => x.TrailerNumber).ToListAsync(ct);
        var mappings = await SafeMappings(ct);

        IReadOnlyList<TachoDriverDutyStatus> duties = [];
        string? tachoError = null;
        if (!tachoOptions.IsConfigured)
        {
            tachoError = "Not configured";
        }
        else
        {
            try
            {
                // A vehicle moving just after midnight commonly belongs to a duty that started
                // on the previous calendar day. Read both dates and retain duties overlapping
                // the selected UK operating day so midnight work is not shown as driver unknown.
                var today = await tachoMaster.GetDriverDutyStatusesAsync(date, ct);
                var previous = await tachoMaster.GetDriverDutyStatusesAsync(date.AddDays(-1), ct);
                duties = today.Concat(previous)
                    .Where(duty => duty.DutyStartUtc < dayEndUtc && (duty.DutyEndUtc is null || duty.DutyEndUtc >= dayStartUtc))
                    .GroupBy(DutyIdentity, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(item => item.DutyEndUtc ?? DateTimeOffset.MaxValue).First())
                    .OrderBy(item => item.DutyStartUtc)
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tachoError = ex.GetBaseException().Message;
                logger.LogWarning(ex, "Daily compliance TachoMaster duty read failed for {Date}", date);
            }
        }

        var tracking = await db.VehicleTrackingEvents.AsNoTracking()
            .Where(x => x.EventTimeUtc >= dayStartUtc && x.EventTimeUtc < dayEndUtc)
            .OrderBy(x => x.EventTimeUtc)
            .ToListAsync(ct);

        var preUse = new List<PreUseEvidence>();
        if (tachoOptions.IsConfigured)
        {
            preUse.AddRange(await ReadTachoPreUseAsync(date, ct));
            preUse.AddRange(await ReadTachoPreUseAsync(date.AddDays(-1), ct));
            preUse = preUse
                .Where(item => item.DutyStartUtc < dayEndUtc && item.DutyStartUtc >= dayStartUtc.AddDays(-1))
                .GroupBy(item => $"{item.MemberCode}|{item.VehicleCode}|{item.DutyStartUtc:O}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        var fleetioVehicleIds = await ResolveFleetioVehicleIds(vehicles, mappings, ct);
        var fleetioCache = new Dictionary<string, IReadOnlyList<FleetioInspectionEvidence>>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ComplianceRow>();
        var representedVehicleDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var representedTrailerDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var duty in duties.OrderBy(x => x.DutyStartUtc))
        {
            var vehicle = vehicles.FirstOrDefault(v => VehicleMatches(v, duty.VehicleCode));
            if (vehicle is null)
            {
                logger.LogWarning("Tacho duty {MemberCode} at {DutyStart} supplied vehicle code {VehicleCode}, but no active TMS vehicle matched it.", duty.MemberCode, duty.DutyStartUtc, duty.VehicleCode);
                continue;
            }
            var driver = drivers.FirstOrDefault(d => DriverMatches(d, duty));
            if (driver is null)
            {
                logger.LogWarning("Tacho duty member {MemberCode} ({DriverName}) could not be matched to an active TMS driver.", duty.MemberCode, duty.DriverName);
                continue;
            }

            var firstMovement = FirstMovement(tracking, vehicle, duty.DutyStartUtc, dayEndUtc);
            var preUseEvidence = FindPreUse(preUse, duty, vehicle);
            var fleetioId = FleetioIdFor(vehicle, mappings, fleetioVehicleIds);
            var inspection = await InspectionFor(fleetioId, driver, duty.DutyStartUtc, firstMovement, dayStartUtc, dayEndUtc, fleetioCache, ct);
            var runReference = RunReferenceFor(loads, driver.Id, vehicle.Id);
            rows.Add(BuildRow("Vehicle", vehicle.Id, vehicle.Registration, runReference, driver, EmploymentType(driver), duty, firstMovement,
                preUseEvidence?.FirstDriveUtc, preUseEvidence?.PreDriveOtherWorkMinutes, inspection));
            representedVehicleDrivers.Add($"{vehicle.Id}|{driver.Id}");
        }

        // Fleetio is a first-class evidence source. This loop also finds checks where Tacho is
        // missing and identifies the driver from the inspection submitter.
        foreach (var vehicle in vehicles)
        {
            var fleetioId = FleetioIdFor(vehicle, mappings, fleetioVehicleIds);
            if (string.IsNullOrWhiteSpace(fleetioId) || !fleetioOptions.IsConfigured) continue;
            if (!fleetioCache.TryGetValue(fleetioId, out var inspections))
            {
                inspections = await ReadFleetioInspections(fleetioId, dayStartUtc, dayEndUtc, ct);
                fleetioCache[fleetioId] = inspections;
            }

            foreach (var inspection in inspections.OrderBy(x => x.SubmittedAtUtc))
            {
                if (rows.Any(row => string.Equals(row.FleetioInspectionId, inspection.Id, StringComparison.OrdinalIgnoreCase))) continue;
                var driver = drivers.FirstOrDefault(candidate => UserMatches(inspection.User, inspection.UserEmployeeNumber, candidate));
                if (driver is null)
                {
                    rows.Add(BuildUnmatchedInspectionRow("Vehicle", vehicle.Id, vehicle.Registration, inspection,
                        FirstMovement(tracking, vehicle, inspection.SubmittedAtUtc, dayEndUtc)));
                    continue;
                }

                var duty = FindDuty(duties, driver, vehicle, inspection.SubmittedAtUtc);
                var firstMovement = FirstMovement(tracking, vehicle, duty?.DutyStartUtc ?? inspection.SubmittedAtUtc, dayEndUtc);
                var preUseEvidence = duty is null ? null : FindPreUse(preUse, duty, vehicle);
                var runReference = RunReferenceFor(loads, driver.Id, vehicle.Id);
                rows.Add(BuildRow("Vehicle", vehicle.Id, vehicle.Registration, runReference, driver, EmploymentType(driver), duty, firstMovement,
                    preUseEvidence?.FirstDriveUtc, preUseEvidence?.PreDriveOtherWorkMinutes, new FleetioInspectionMatch(inspection, true)));
                representedVehicleDrivers.Add($"{vehicle.Id}|{driver.Id}");
            }
        }

        foreach (var vehicle in vehicles)
        {
            var firstMovement = FirstMovement(tracking, vehicle, null, dayEndUtc);
            if (firstMovement is null) continue;
            var hasAnyVehicleRow = rows.Any(row => row.AssetType == "Vehicle" && row.AssetId == vehicle.Id);
            if (!hasAnyVehicleRow)
            {
                var fleetioId = FleetioIdFor(vehicle, mappings, fleetioVehicleIds);
                rows.Add(BuildTrackerOnlyRow(vehicle, firstMovement.Value, tachoError, fleetioId, fleetioReadFailures > 0 ? fleetioLastError : null));
            }
        }

        foreach (var trailer in trailers)
        {
            var trailerFleetioId = MappingExternalKey(mappings, "Trailer", trailer.Id);
            if (string.IsNullOrWhiteSpace(trailerFleetioId) || !fleetioOptions.IsConfigured) continue;
            if (!fleetioCache.TryGetValue(trailerFleetioId, out var inspections))
            {
                inspections = await ReadFleetioInspections(trailerFleetioId, dayStartUtc, dayEndUtc, ct);
                fleetioCache[trailerFleetioId] = inspections;
            }

            foreach (var inspection in inspections.OrderBy(x => x.SubmittedAtUtc))
            {
                if (rows.Any(row => string.Equals(row.FleetioInspectionId, inspection.Id, StringComparison.OrdinalIgnoreCase))) continue;
                var driver = drivers.FirstOrDefault(candidate => UserMatches(inspection.User, inspection.UserEmployeeNumber, candidate));
                if (driver is null)
                {
                    rows.Add(BuildUnmatchedInspectionRow("Trailer", trailer.Id, trailer.TrailerNumber, inspection, null));
                    continue;
                }

                var load = loads.FirstOrDefault(candidate => candidate.DriverId == driver.Id && candidate.TrailerId == trailer.Id);
                var vehicle = load?.VehicleId is Guid vehicleId ? vehicles.FirstOrDefault(v => v.Id == vehicleId) : null;
                var duty = vehicle is null ? null : FindDuty(duties, driver, vehicle, inspection.SubmittedAtUtc);
                var firstMovement = vehicle is null ? null : FirstMovement(tracking, vehicle, duty?.DutyStartUtc ?? inspection.SubmittedAtUtc, dayEndUtc);
                var preUseEvidence = duty is null || vehicle is null ? null : FindPreUse(preUse, duty, vehicle);
                var runReference = RunReferenceForTrailer(loads, driver.Id, trailer.Id);
                rows.Add(BuildRow("Trailer", trailer.Id, trailer.TrailerNumber, runReference, driver, EmploymentType(driver), duty, firstMovement,
                    preUseEvidence?.FirstDriveUtc, preUseEvidence?.PreDriveOtherWorkMinutes, new FleetioInspectionMatch(inspection, true)));
                representedTrailerDrivers.Add($"{trailer.Id}|{driver.Id}");
            }
        }

        foreach (var load in loads.Where(x => x.DriverId != null && x.VehicleId != null).OrderBy(x => x.Reference))
        {
            var driver = drivers.FirstOrDefault(x => x.Id == load.DriverId);
            var vehicle = vehicles.FirstOrDefault(x => x.Id == load.VehicleId);
            if (driver is null || vehicle is null) continue;

            var vehicleKey = $"{vehicle.Id}|{driver.Id}";
            if (!representedVehicleDrivers.Contains(vehicleKey))
            {
                var duty = FindDuty(duties, driver, vehicle, null);
                var firstMovement = FirstMovement(tracking, vehicle, duty?.DutyStartUtc, dayEndUtc);
                var preUseEvidence = duty is null ? null : FindPreUse(preUse, duty, vehicle);
                var vehicleFleetioId = FleetioIdFor(vehicle, mappings, fleetioVehicleIds);
                var vehicleInspection = await InspectionFor(vehicleFleetioId, driver, duty?.DutyStartUtc, firstMovement, dayStartUtc, dayEndUtc, fleetioCache, ct);
                rows.Add(BuildRow("Vehicle", vehicle.Id, vehicle.Registration, load.Reference, driver, EmploymentType(driver), duty, firstMovement,
                    preUseEvidence?.FirstDriveUtc, preUseEvidence?.PreDriveOtherWorkMinutes, vehicleInspection));
                representedVehicleDrivers.Add(vehicleKey);
            }

            if (load.TrailerId is Guid trailerId)
            {
                var trailer = trailers.FirstOrDefault(x => x.Id == trailerId);
                if (trailer is null) continue;
                var trailerKey = $"{trailer.Id}|{driver.Id}";
                if (representedTrailerDrivers.Contains(trailerKey)) continue;
                var duty = FindDuty(duties, driver, vehicle, null);
                var firstMovement = FirstMovement(tracking, vehicle, duty?.DutyStartUtc, dayEndUtc);
                var preUseEvidence = duty is null ? null : FindPreUse(preUse, duty, vehicle);
                var trailerFleetioId = MappingExternalKey(mappings, "Trailer", trailer.Id);
                var trailerInspection = await InspectionFor(trailerFleetioId, driver, duty?.DutyStartUtc, firstMovement, dayStartUtc, dayEndUtc, fleetioCache, ct);
                rows.Add(BuildRow("Trailer", trailer.Id, trailer.TrailerNumber, load.Reference, driver, EmploymentType(driver), duty, firstMovement,
                    preUseEvidence?.FirstDriveUtc, preUseEvidence?.PreDriveOtherWorkMinutes, trailerInspection));
                representedTrailerDrivers.Add(trailerKey);
            }
        }

        rows = rows
            .GroupBy(x => $"{x.AssetType}|{x.AssetId}|{x.DriverId}|{x.TachoDutyStartUtc:O}|{x.FleetioInspectionId}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => !string.IsNullOrWhiteSpace(row.RunReference)).ThenBy(row => row.RunReference).First())
            .OrderBy(row => row.Status == "Non-compliant" ? 0 : row.Status == "Review" ? 1 : row.Status == "Paper evidence required" ? 2 : 3)
            .ThenBy(row => row.AssetType)
            .ThenBy(row => row.AssetName)
            .ThenBy(row => row.DriverName)
            .ToList();

        var fleetioChecks = fleetioCache.Values.SelectMany(items => items).Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var fleetioMappedVehicles = vehicles.Count(vehicle => !string.IsNullOrWhiteSpace(FleetioIdFor(vehicle, mappings, fleetioVehicleIds)));
        var tachoStatus = tachoError is null
            ? $"Available · {duties.Count} duty record{(duties.Count == 1 ? "" : "s")} overlap this UK operating day"
            : tachoError == "Not configured" ? "Not configured" : $"Partial: {tachoError}";
        var fleetioStatus = !fleetioOptions.IsConfigured
            ? "Not configured"
            : fleetioReadFailures > 0
                ? $"Partial · {fleetioChecks} inspection{(fleetioChecks == 1 ? "" : "s")} read; {fleetioReadFailures}/{fleetioReadAttempts} asset request{(fleetioReadAttempts == 1 ? "" : "s")} failed. {fleetioLastError}"
                : $"Available · {fleetioChecks} inspection{(fleetioChecks == 1 ? "" : "s")} read; {fleetioMappedVehicles}/{vehicles.Count} active vehicles linked to Fleetio";

        return new ComplianceReport(
            date,
            DateTimeOffset.UtcNow,
            new CompliancePolicy(null, true, true, true,
                "SLH policy requires a fresh Fleetio pre-use walkround whenever a new driver takes control of a vehicle or trailer. TachoMaster other-work before the first drive is recorded as evidence and does not decide the pass/fail result."),
            new SourceStatus(tachoStatus, fleetioStatus,
                $"Available from stored DOT/Falcon movement · {tracking.Count} tracking event{(tracking.Count == 1 ? "" : "s")} in day window",
                "Optional run enrichment from TMS planning register"),
            new ComplianceSummary(
                rows.Count,
                rows.Count(x => x.Status == "Compliant"),
                rows.Count(x => x.Status is "Paper evidence required" or "Review"),
                rows.Count(x => x.Status == "Non-compliant"),
                rows.Count(x => x.AssetType == "Vehicle"),
                rows.Count(x => x.AssetType == "Trailer"),
                rows.Count(x => x.FleetioInspectionId is not null),
                rows.Count(x => x.TachoPreUseOtherWorkMinutes is not null)),
            rows);
    }

    private ComplianceRow BuildRow(string assetType, Guid assetId, string assetName, string runReference, Driver driver, string employment,
        TachoDriverDutyStatus? duty, DateTimeOffset? firstMovement, DateTimeOffset? firstDrive, int? preUseMinutes, FleetioInspectionMatch inspection)
    {
        var tachoRecorded = preUseMinutes is not null;
        var fleetioPresent = inspection.Evidence is not null;
        var fleetioDriverOk = fleetioPresent && inspection.DriverMatched;
        var failedItems = inspection.Evidence?.FailedItems ?? 0;
        var fleetioPassed = fleetioDriverOk && failedItems == 0;
        var status = fleetioPassed
            ? "Compliant"
            : fleetioDriverOk && failedItems > 0
                ? "Non-compliant"
                : employment == "Agency" && !fleetioDriverOk
                    ? "Paper evidence required"
                    : fleetioPresent ? "Review" : "Non-compliant";

        var reason = status switch
        {
            "Compliant" when tachoRecorded => $"Fleetio pre-use walkround passed. TachoMaster records {preUseMinutes} minutes other-work before the first drive.",
            "Compliant" => "Fleetio pre-use walkround passed. TachoMaster did not return a measurable pre-drive other-work segment.",
            "Non-compliant" when fleetioDriverOk && failedItems > 0 => $"Fleetio pre-use walkround was submitted by the matched driver but contains {failedItems} failed item{(failedItems == 1 ? "" : "s")}.",
            "Paper evidence required" => "Agency driver has no matching Fleetio walkround. Verify the paper check and keep the driver visible for Fleetio adoption.",
            "Review" when fleetioPresent => "A Fleetio walkround exists in the pre-use window, but its submitter did not match the TMS/Tacho driver identity.",
            _ when duty is null => "Fleetio walkround is missing and no matching Tacho duty was found for this driver and vehicle.",
            _ when tachoRecorded => $"Fleetio walkround is missing. TachoMaster records {preUseMinutes} minutes other-work before the first drive.",
            _ => "Fleetio walkround is missing. TachoMaster did not return a measurable pre-drive other-work segment."
        };

        return new ComplianceRow(assetType, assetId, assetName, runReference, driver.Id, driver.DisplayName, employment,
            duty?.DutyStartUtc, firstDrive, preUseMinutes,
            duty?.WorkMinutes, duty?.DriveMinutes, duty?.RestMinutes, duty?.AvailableMinutes, duty?.BreakMinutes,
            firstMovement, inspection.Evidence?.Id, inspection.Evidence?.Form,
            inspection.Evidence?.SubmittedAtUtc, inspection.Evidence?.User, inspection.DriverMatched,
            inspection.Evidence?.FailedItems, status, reason);
    }

    private static ComplianceRow BuildUnmatchedInspectionRow(string assetType, Guid assetId, string assetName,
        FleetioInspectionEvidence inspection, DateTimeOffset? firstMovement)
        => new(assetType, assetId, assetName, string.Empty, Guid.Empty,
            string.IsNullOrWhiteSpace(inspection.User) ? "Unmatched Fleetio user" : inspection.User!, "Unknown",
            null, null, null, null, null, null, null, null, firstMovement, inspection.Id, inspection.Form, inspection.SubmittedAtUtc, inspection.User,
            false, inspection.FailedItems, "Review",
            "Fleetio walkround is present, but its submitter could not be matched to an active TMS driver. Run allocation is intentionally not inferred.");

    private static ComplianceRow BuildTrackerOnlyRow(Vehicle vehicle, DateTimeOffset firstMovement, string? tachoError, string? fleetioId, string? fleetioError)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(tachoError)) details.Add($"Tacho source: {tachoError}");
        if (string.IsNullOrWhiteSpace(fleetioId)) details.Add("Fleetio asset link is missing");
        if (!string.IsNullOrWhiteSpace(fleetioError)) details.Add($"Fleetio source: {fleetioError}");
        var suffix = details.Count == 0 ? string.Empty : $" ({string.Join("; ", details)})";
        return new("Vehicle", vehicle.Id, vehicle.Registration, string.Empty, Guid.Empty,
            "Driver not identified", "Unknown", null, null, null, null, null, null, null, null, firstMovement, null, null, null, null, null, null,
            "Review", $"DOT/Falcon movement proves this vehicle was used, but no matching Tacho duty or Fleetio walkround identified the driver. Investigate the missing pre-use evidence.{suffix}");
    }

    private static string RunReferenceFor(IEnumerable<Load> loads, Guid driverId, Guid vehicleId)
        => string.Join(", ", loads.Where(load => load.DriverId == driverId && load.VehicleId == vehicleId)
            .OrderBy(load => load.Reference).Select(load => load.Reference).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

    private static string RunReferenceForTrailer(IEnumerable<Load> loads, Guid driverId, Guid trailerId)
        => string.Join(", ", loads.Where(load => load.DriverId == driverId && load.TrailerId == trailerId)
            .OrderBy(load => load.Reference).Select(load => load.Reference).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase));

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        try { return await db.Loads.AsNoTracking().Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled).ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Dedicated Loads table unavailable for daily compliance; using planning register.");
            db.ChangeTracker.Clear();
            return (await PlanningRegisterStore.ReadLoadsAsync(db, date, ct)).Where(x => x.Status != LoadStatus.Cancelled).ToList();
        }
    }

    private async Task<List<IntegrationMapping>> SafeMappings(CancellationToken ct)
    {
        try { return await db.IntegrationMappings.AsNoTracking().Where(x => x.Active && x.Provider == "Fleetio").ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleetio mappings unavailable for daily compliance.");
            return [];
        }
    }

    private async Task<Dictionary<Guid, string>> ResolveFleetioVehicleIds(IReadOnlyCollection<Vehicle> vehicles, IReadOnlyCollection<IntegrationMapping> mappings, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        foreach (var vehicle in vehicles)
        {
            var explicitId = !string.IsNullOrWhiteSpace(vehicle.FleetioId) ? vehicle.FleetioId : MappingExternalKey(mappings, "Vehicle", vehicle.Id);
            if (!string.IsNullOrWhiteSpace(explicitId)) result[vehicle.Id] = explicitId!;
        }
        if (!fleetioOptions.IsConfigured) return result;

        try
        {
            var assets = await fleetioClient.GetVehiclesAsync(100, ct);
            var powered = assets.Where(asset => asset.Type?.Contains("trailer", StringComparison.OrdinalIgnoreCase) != true).ToList();
            foreach (var vehicle in vehicles.Where(vehicle => !result.ContainsKey(vehicle.Id)))
            {
                var aliases = VehicleAliases(vehicle).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var matches = powered.Where(asset => FleetioAssetAliases(asset).Any(aliases.Contains)).Select(asset => asset.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (matches.Count == 1)
                {
                    result[vehicle.Id] = matches[0];
                    logger.LogInformation("Daily compliance resolved Fleetio vehicle {FleetioId} from registration/fleet aliases for {Registration}.", matches[0], vehicle.Registration);
                }
                else if (matches.Count > 1)
                {
                    logger.LogWarning("Daily compliance found {Count} Fleetio asset candidates for TMS vehicle {Registration}; no automatic link was selected.", matches.Count, vehicle.Registration);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fleetioLastError = ex.GetBaseException().Message;
            logger.LogWarning(ex, "Fleetio asset inventory could not be read for daily compliance fallback matching.");
        }
        return result;
    }

    private static string? FleetioIdFor(Vehicle vehicle, IEnumerable<IntegrationMapping> mappings, IReadOnlyDictionary<Guid, string> resolved)
    {
        if (!string.IsNullOrWhiteSpace(vehicle.FleetioId)) return vehicle.FleetioId;
        var mapped = MappingExternalKey(mappings, "Vehicle", vehicle.Id);
        if (!string.IsNullOrWhiteSpace(mapped)) return mapped;
        return resolved.TryGetValue(vehicle.Id, out var value) ? value : null;
    }

    private async Task<FleetioInspectionMatch> InspectionFor(string? fleetioId, Driver driver, DateTimeOffset? dutyStartUtc,
        DateTimeOffset? firstMovementUtc, DateTimeOffset dayStartUtc, DateTimeOffset dayEndUtc,
        Dictionary<string, IReadOnlyList<FleetioInspectionEvidence>> cache, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(fleetioId) || !fleetioOptions.IsConfigured) return new(null, false);
        if (!cache.TryGetValue(fleetioId, out var inspections))
        {
            inspections = await ReadFleetioInspections(fleetioId, dayStartUtc, dayEndUtc, ct);
            cache[fleetioId] = inspections;
        }

        var windowStart = dutyStartUtc?.AddMinutes(-45) ?? dayStartUtc;
        var windowEnd = firstMovementUtc?.AddMinutes(20) ?? dutyStartUtc?.AddHours(3) ?? dayEndUtc;
        var candidates = inspections.Where(x => x.SubmittedAtUtc >= windowStart && x.SubmittedAtUtc <= windowEnd).OrderBy(x => x.SubmittedAtUtc).ToList();
        var matched = candidates.FirstOrDefault(x => UserMatches(x.User, x.UserEmployeeNumber, driver));
        return matched is not null ? new(matched, true) : new(candidates.FirstOrDefault(), false);
    }

    private async Task<IReadOnlyList<FleetioInspectionEvidence>> ReadFleetioInspections(string fleetioId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        fleetioReadAttempts++;
        try
        {
            var http = httpClientFactory.CreateClient();
            var result = new List<FleetioInspectionEvidence>();
            string? cursor = null;
            var seenCursors = new HashSet<string>(StringComparer.Ordinal);

            for (var page = 0; page < 100; page++)
            {
                var uri = $"{fleetioOptions.BaseUrl.TrimEnd('/')}/submitted_inspection_forms?filter[vehicle_id][eq]={Uri.EscapeDataString(fleetioId)}&filter[submitted_at][gte]={Uri.EscapeDataString(fromUtc.ToString("O"))}&filter[submitted_at][lt]={Uri.EscapeDataString(toUtc.ToString("O"))}&sort[submitted_at]=desc&per_page=100";
                if (!string.IsNullOrWhiteSpace(cursor)) uri += $"&start_cursor={Uri.EscapeDataString(cursor)}";

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Authorization", $"Token {fleetioOptions.ApiKey}");
                request.Headers.TryAddWithoutValidation("Account-Token", fleetioOptions.AccountToken);
                request.Headers.TryAddWithoutValidation("X-Api-Version", fleetioOptions.ApiVersion);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    fleetioReadFailures++;
                    fleetioLastError = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                    logger.LogWarning("Fleetio submitted inspections returned {StatusCode} for asset {FleetioId}.", response.StatusCode, fleetioId);
                    break;
                }

                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                IEnumerable<JsonElement> records = root.ValueKind == JsonValueKind.Array
                    ? root.EnumerateArray()
                    : Try(root, "records", out var recordsElement) && recordsElement.ValueKind == JsonValueKind.Array
                        ? recordsElement.EnumerateArray()
                        : Try(root, "data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array
                            ? dataElement.EnumerateArray()
                            : Try(root, "results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array
                                ? resultsElement.EnumerateArray()
                                : [];

                result.AddRange(records.Select(ParseInspection).Where(item => item.SubmittedAtUtc >= fromUtc && item.SubmittedAtUtc < toUtc));

                var nextCursor = root.ValueKind == JsonValueKind.Object ? Text(root, "next_cursor", "nextCursor") : null;
                if (string.IsNullOrWhiteSpace(nextCursor) || !seenCursors.Add(nextCursor)) break;
                cursor = nextCursor;
            }

            return result
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.SubmittedAtUtc)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fleetioReadFailures++;
            fleetioLastError = ex.GetBaseException().Message;
            logger.LogWarning(ex, "Fleetio inspection evidence unavailable for asset {FleetioId}.", fleetioId);
            return [];
        }
    }

    private static FleetioInspectionEvidence ParseInspection(JsonElement item)
    {
        var user = NestedText(item, "user", "name")
            ?? NestedText(item, "submitted_by", "name")
            ?? NestedText(item, "submitted_by_user", "name")
            ?? Text(item, "user_name", "submitted_by_name", "user");
        var employee = NestedText(item, "user", "employee_number")
            ?? NestedText(item, "submitted_by", "employee_number")
            ?? NestedText(item, "submitted_by_user", "employee_number")
            ?? Text(item, "employee_number", "user_employee_number");
        var failed = Int(item, "failed_items", "failed_items_count", "failed_count");
        return new FleetioInspectionEvidence(
            Text(item, "id") ?? string.Empty,
            NestedText(item, "inspection_form", "title") ?? Text(item, "inspection_form_title", "name") ?? "Pre-use inspection",
            Date(item, "submitted_at", "created_at", "completed_at") ?? DateTimeOffset.MinValue,
            user,
            employee,
            failed);
    }

    private async Task<List<PreUseEvidence>> ReadTachoPreUseAsync(DateOnly date, CancellationToken ct)
    {
        var result = new List<PreUseEvidence>();
        try
        {
            var http = httpClientFactory.CreateClient();
            http.BaseAddress = new Uri(NormaliseBaseUrl(tachoOptions.BaseUrl));
            var sid = await LoginTacho(http, ct);
            var offset = 0;
            for (var page = 0; page < tachoOptions.MaxPages; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "Duty/GetDutyTransactions");
                request.Headers.Add("APIKEY", tachoOptions.ApiKey);
                request.Headers.Add("SID", sid);
                request.Content = JsonContent.Create(new
                {
                    From = OperatingWindowUtc(date).StartUtc.ToString("O"),
                    To = OperatingWindowUtc(date).EndUtc.ToString("O"),
                    Offset = offset,
                    WithWtd = true
                });
                using var response = await http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var root = document.RootElement;
                var dutyPage = Try(root, "DutyNew", out var dutyNew) ? dutyNew : root;
                var records = Try(dutyPage, "Data", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToList() : [];

                foreach (var duty in records)
                {
                    var vehicleCode = Normalise(Text(duty, "VehCode") ?? string.Empty);
                    var memberCode = Int(duty, "MemCode") ?? 0;
                    var dutyStart = Date(duty, "DutyStart") ?? DateTimeOffset.MinValue;
                    if (vehicleCode.Length == 0 || memberCode == 0 || dutyStart == DateTimeOffset.MinValue) continue;
                    var segments = Try(duty, "Wtd", out var wtd) && wtd.ValueKind == JsonValueKind.Array
                        ? wtd.EnumerateArray().Select(item => new ActivitySegment(Text(item, "WtdEvent") ?? string.Empty, Date(item, "TimeStart"), Date(item, "TimeEnd")))
                            .Where(item => item.StartUtc is not null && item.EndUtc is not null && item.EndUtc >= item.StartUtc).OrderBy(item => item.StartUtc).ToList()
                        : [];
                    var firstDrive = segments.Where(item => item.Kind.Contains("drive", StringComparison.OrdinalIgnoreCase)).Select(item => item.StartUtc).FirstOrDefault();
                    var cutOff = firstDrive ?? segments.Select(item => item.EndUtc).Where(item => item is not null).Max();
                    var otherWorkMinutes = cutOff is null ? 0 : segments
                        .Where(item => item.StartUtc < cutOff && item.Kind.Contains("work", StringComparison.OrdinalIgnoreCase))
                        .Sum(item => (int)Math.Max(0, Math.Round((item.EndUtc!.Value - item.StartUtc!.Value).TotalMinutes)));
                    result.Add(new PreUseEvidence(vehicleCode, memberCode, dutyStart, firstDrive, otherWorkMinutes));
                }

                var moreData = Try(dutyPage, "MoreData", out var more) && more.ValueKind == JsonValueKind.True;
                var recordCount = Try(dutyPage, "RecordCount", out var countElement) && countElement.TryGetInt32(out var count) ? count : records.Count;
                if (!moreData || recordCount == 0) break;
                offset += recordCount;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Detailed Tacho pre-use activity unavailable for {Date}.", date);
        }
        return result;
    }

    private async Task<string> LoginTacho(HttpClient http, CancellationToken ct)
    {
        foreach (var password in PasswordAttempts(tachoOptions.Password))
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "auth/login");
            request.Headers.Add("APIKEY", tachoOptions.ApiKey);
            request.Content = JsonContent.Create(new { User = tachoOptions.Username, Pass = password, OsVersion = "1.0", OsName = "Azure Container App", PcName = Environment.MachineName, AuthType = "password" });
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) continue;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = document.RootElement;
            if (root.ValueKind is JsonValueKind.String or JsonValueKind.Number) return root.ToString();
            foreach (var name in new[] { "sid", "SID", "token", "Token" })
                if (Try(root, name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        }
        throw new HttpRequestException("TachoMaster login failed while reading pre-use activity.");
    }

    private static TachoDriverDutyStatus? FindDuty(IEnumerable<TachoDriverDutyStatus> duties, Driver driver, Vehicle vehicle, DateTimeOffset? near)
    {
        var matches = duties.Where(duty => DriverMatches(driver, duty) && VehicleMatches(vehicle, duty.VehicleCode));
        return near is null
            ? matches.OrderBy(duty => duty.DutyStartUtc).FirstOrDefault()
            : matches.OrderBy(duty => Math.Abs((duty.DutyStartUtc - near.Value).TotalMinutes)).FirstOrDefault();
    }

    private static PreUseEvidence? FindPreUse(IEnumerable<PreUseEvidence> items, TachoDriverDutyStatus duty, Vehicle vehicle)
        => items.Where(item => item.MemberCode == duty.MemberCode)
            .Where(item => VehicleMatches(vehicle, item.VehicleCode))
            .OrderBy(item => Math.Abs((item.DutyStartUtc - duty.DutyStartUtc).TotalMinutes))
            .FirstOrDefault(item => Math.Abs((item.DutyStartUtc - duty.DutyStartUtc).TotalMinutes) <= 10);

    private static DateTimeOffset? FirstMovement(IEnumerable<VehicleTrackingEvent> tracking, Vehicle vehicle, DateTimeOffset? dutyStart, DateTimeOffset dayEnd)
    {
        var ids = VehicleAliases(vehicle).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var from = dutyStart ?? dayEnd.AddDays(-1);
        return tracking.Where(item => item.EventTimeUtc >= from && item.EventTimeUtc < dayEnd)
            .Where(item => ids.Contains(Normalise(item.VehicleIdentifier)))
            .Where(item => item.IsMoving == true || item.SpeedKph > 0)
            .Select(item => (DateTimeOffset?)item.EventTimeUtc).FirstOrDefault();
    }

    internal static bool DriverMatches(Driver driver, TachoDriverDutyStatus duty)
    {
        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber) && !string.IsNullOrWhiteSpace(duty.CardNumber) &&
            string.Equals(Normalise(driver.TachoCardNumber), Normalise(duty.CardNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode > 0 && memberCode == duty.MemberCode) return true;
        if (!string.IsNullOrWhiteSpace(driver.EmployeeNumber) && !string.IsNullOrWhiteSpace(duty.EmployeeNumber) &&
            string.Equals(Normalise(driver.EmployeeNumber), Normalise(duty.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        return new[] { driver.DisplayName, driver.TachoName }.Where(value => !string.IsNullOrWhiteSpace(value)).Any(value => NamesEquivalent(value!, duty.DriverName));
    }

    internal static bool UserMatches(string? fleetioUser, string? fleetioEmployeeNumber, Driver driver)
    {
        if (!string.IsNullOrWhiteSpace(fleetioEmployeeNumber) && !string.IsNullOrWhiteSpace(driver.EmployeeNumber) &&
            string.Equals(Normalise(fleetioEmployeeNumber), Normalise(driver.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(fleetioUser)) return false;
        return new[] { driver.DisplayName, driver.TachoName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Any(value => NamesEquivalent(value!, fleetioUser));
    }

    internal static bool NamesEquivalent(string left, string right)
    {
        var a = NameTokens(left);
        var b = NameTokens(right);
        if (a.Count == 0 || b.Count == 0) return false;
        if (a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase)) return true;
        var sortedA = a.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        var sortedB = b.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (sortedA.SequenceEqual(sortedB, StringComparer.OrdinalIgnoreCase)) return true;
        var normalA = Normalise(left);
        var normalB = Normalise(right);
        return normalA.Length >= 6 && normalB.Length >= 6 && (normalA.Contains(normalB, StringComparison.OrdinalIgnoreCase) || normalB.Contains(normalA, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> NameTokens(string value)
        => value.Split(new[] { ' ', ',', '.', '-', '_', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalise).Where(token => token.Length > 0).ToList();

    private static string EmploymentType(Driver driver)
    {
        if (new[] { driver.DriverType, driver.DriverGroup, driver.AgencyName }.Any(value => value?.Contains("agency", StringComparison.OrdinalIgnoreCase) == true)) return "Agency";
        if (driver.DriverType?.Contains("subcontract", StringComparison.OrdinalIgnoreCase) == true) return "Subcontractor";
        return "Employed";
    }

    internal static IReadOnlyList<string> VehicleAliases(Vehicle vehicle)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVehicleAliases(aliases, vehicle.Registration, true);
        AddVehicleAliases(aliases, vehicle.Abbreviation, true);
        AddVehicleAliases(aliases, vehicle.FleetNumber, false);
        return aliases.Where(value => value.Length > 0).ToList();
    }

    internal static bool VehicleMatches(Vehicle vehicle, string? externalIdentifier)
    {
        var key = Normalise(externalIdentifier ?? string.Empty);
        return key.Length > 0 && VehicleAliases(vehicle).Contains(key, StringComparer.OrdinalIgnoreCase);
    }

    private static void AddVehicleAliases(ISet<string> aliases, string? raw, bool registrationLike)
    {
        var key = Normalise(raw ?? string.Empty);
        if (key.Length == 0) return;
        aliases.Add(key);
        if (registrationLike)
        {
            for (var length = 4; length <= Math.Min(7, key.Length); length++) aliases.Add(key[^length..]);
            if (key.EndsWith("H", StringComparison.OrdinalIgnoreCase) && key.Length > 4) aliases.Add(key[..^1]);
        }
    }

    private static IEnumerable<string> FleetioAssetAliases(FleetioVehicle asset)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddVehicleAliases(aliases, asset.Registration, true);
        AddVehicleAliases(aliases, asset.Name, true);
        AddVehicleAliases(aliases, asset.FleetNumber, false);
        return aliases;
    }

    private static string DutyIdentity(TachoDriverDutyStatus duty)
        => $"{duty.MemberCode}|{Normalise(duty.VehicleCode)}|{duty.DutyStartUtc:O}";

    private static string? MappingExternalKey(IEnumerable<IntegrationMapping> mappings, string entityType, Guid entityId)
        => mappings.FirstOrDefault(mapping => mapping.TmsEntityId == entityId && mapping.TmsEntityType.Equals(entityType, StringComparison.OrdinalIgnoreCase))?.ExternalKey;
    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) OperatingWindowUtc(DateOnly date)
    {
        var startLocal = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var endLocal = DateTime.SpecifyKind(date.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return (new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, London)), new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, London)));
    }

    private static string NormaliseBaseUrl(string value) { var trimmed = value.Trim().TrimEnd('/'); return trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? $"{trimmed}/" : $"{trimmed}/api/"; }
    private static IReadOnlyList<string> PasswordAttempts(string password) { var trimmed = password.Trim(); return trimmed.All(Uri.IsHexDigit) && trimmed.Length is 32 or 40 or 64 ? [trimmed] : [trimmed, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(trimmed))).ToLowerInvariant()]; }
    private static bool Try(JsonElement element, string name, out JsonElement value) { if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; } value = default; return false; }
    private static string? Text(JsonElement element, params string[] names) { foreach (var name in names) if (Try(element, name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined) return value.ToString().Trim(); return null; }
    private static string? NestedText(JsonElement element, string parent, string child) => Try(element, parent, out var nested) && nested.ValueKind == JsonValueKind.Object ? Text(nested, child) : null;
    private static DateTimeOffset? Date(JsonElement element, params string[] names) => DateTimeOffset.TryParse(Text(element, names), out var value) ? value : null;
    private static int? Int(JsonElement element, params string[] names) => int.TryParse(Text(element, names), out var value) ? value : null;
    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private sealed record FleetioInspectionEvidence(string Id, string Form, DateTimeOffset SubmittedAtUtc, string? User, string? UserEmployeeNumber, int? FailedItems);
    private sealed record FleetioInspectionMatch(FleetioInspectionEvidence? Evidence, bool DriverMatched);
    private sealed record ActivitySegment(string Kind, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc);
    private sealed record PreUseEvidence(string VehicleCode, int MemberCode, DateTimeOffset DutyStartUtc, DateTimeOffset? FirstDriveUtc, int PreDriveOtherWorkMinutes);
    public sealed record CompliancePolicy(int? MinimumPreUseOtherWorkMinutes, bool EmployedFleetioMandatory, bool AgencyPaperException, bool DriverChangeRequiresNewCheck, string Note);
    public sealed record SourceStatus(string TachoMaster, string Fleetio, string DotFalcon, string Tms);
    public sealed record ComplianceSummary(int AssetsOperated, int Green, int Amber, int Red, int Vehicles, int Trailers, int FleetioChecks, int TachoPreUseConfirmed);
    public sealed record ComplianceRow(string AssetType, Guid AssetId, string AssetName, string RunReference, Guid DriverId, string DriverName, string EmploymentType,
        DateTimeOffset? TachoDutyStartUtc, DateTimeOffset? TachoFirstDriveUtc, int? TachoPreUseOtherWorkMinutes, int? TachoWorkMinutes, int? TachoDriveMinutes, int? TachoRestMinutes, int? TachoAvailableMinutes, int? TachoBreakMinutes, DateTimeOffset? FirstMovementUtc, string? FleetioInspectionId, string? FleetioForm,
        DateTimeOffset? FleetioSubmittedAtUtc, string? FleetioUser, bool? FleetioDriverMatched, int? FleetioFailedItems, string Status, string Reason);
    public sealed record ComplianceReport(DateOnly Date, DateTimeOffset GeneratedAtUtc, CompliancePolicy Policy, SourceStatus SourceStatus, ComplianceSummary Summary, IReadOnlyList<ComplianceRow> Rows);
}
