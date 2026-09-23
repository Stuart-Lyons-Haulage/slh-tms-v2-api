using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Models.Assistant;
using Slh.Tms.Api.Services;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/integrations")]
[Authorize]
public sealed class IntegrationsController(SageHrClient sageHr, DotTrackingOptions tracking, DotTrackingClient dotTracking, TachoMasterClient tachoMaster, DriverSmsDispatchService sms, AzureSmsDispatchService azureSms, TextBeeOptions textBee, FleetioOptions fleetio, FleetioClient fleetioClient, AssistantOptions assistant, IConfiguration configuration, TmsDbContext db, IntegrationSyncCoordinator coordinator, ILogger<IntegrationsController> logger) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var latestTracking = await db.VehicleLiveStatuses.AsNoTracking().MaxAsync(status => (DateTimeOffset?)status.LastEventTimeUtc, ct);
        var latestEmailIntake = await db.StagedImports.AsNoTracking().Where(item => item.Source != null && (item.Source.Contains("Power Automate") || item.Source.Contains("Mailbox"))).MaxAsync(item => (DateTimeOffset?)item.ReceivedAtUtc, ct);
        return Ok(new
        {
            roadTech = new { configured = tracking.IsConfigured, latestEventUtc = latestTracking, connected = tracking.IsConfigured && latestTracking is not null && DateTimeOffset.UtcNow - latestTracking < TimeSpan.FromMinutes(30) },
            azureMaps = new { configured = !string.IsNullOrWhiteSpace(configuration["Maps:Endpoint"]) },
            azureSms = new { configured = azureSms.IsConfigured },
            textBee = new { configured = textBee.IsConfigured, dutyPhoneLabel = textBee.DutyPhoneLabel, missingSettings = textBee.MissingSettings },
            driverSms = new { configured = sms.IsConfigured, provider = textBee.IsConfigured ? "TextBee" : azureSms.IsConfigured ? "Azure SMS" : "MightyText copy" },
            fleetio = new { configured = fleetio.IsConfigured, missingSettings = fleetio.MissingSettings },
            tachoMaster = new { configured = tachoMaster.IsConfigured, missingSettings = tachoMaster.MissingSettings },
            sageHr = new { configured = sageHr.IsConfigured },
            emailIntake = new { configured = latestEmailIntake is not null, lastReceivedUtc = latestEmailIntake },
            assistant = new { configured = assistant.IsConfigured, model = assistant.Model, safeRulesAvailable = true },
            batchIntake = new { configured = true, endpoint = "/api/v1/staging/batch" }
        });
    }

    [HttpGet("tachomaster/status")]
    public async Task<IActionResult> TachoMasterStatus(CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return Ok(new { configured = false, connected = false, matchedVehicleCount = 0, missingSettings = tachoMaster.MissingSettings, message = $"TachoMaster runtime settings are incomplete: {string.Join(", ", tachoMaster.MissingSettings)}." });

        try
        {
            var names = await tachoMaster.GetCurrentDriverNamesByVehicleAsync(DateOnly.FromDateTime(DateTime.UtcNow), ct);
            return Ok(new { configured = true, connected = true, sharedRoadTechCredentials = tachoMaster.UsesSharedRoadTechCredentials, matchedVehicleCount = names.Count, missingSettings = Array.Empty<string>(), message = $"TachoMaster is connected and returned {names.Count} current driver duty assignment(s)." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster status check failed.");
            return Ok(new { configured = true, connected = false, matchedVehicleCount = 0, missingSettings = Array.Empty<string>(), message = $"TachoMaster could not return current driver cards: {exception.GetBaseException().Message}" });
        }
    }

    [HttpGet("sage-hr/status")]
    public async Task<IActionResult> SageHrStatus(CancellationToken ct)
    {
        if (!sageHr.IsConfigured) return Ok(new { configured = false, connected = false, employeeCount = 0, driverCandidateCount = 0, missingSettings = sageHr.MissingSettings, message = $"Sage HR runtime settings are incomplete: {string.Join(", ", sageHr.MissingSettings)}." });
        try
        {
            var employees = await sageHr.GetActiveEmployeesAsync(ct);
            var candidates = employees.Count(IsDriver);
            return Ok(new { configured = true, connected = true, employeeCount = employees.Count, driverCandidateCount = candidates, missingSettings = Array.Empty<string>(), message = "Sage HR is connected." });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Sage HR status check failed.");
            return Ok(new { configured = true, connected = false, employeeCount = 0, driverCandidateCount = 0, missingSettings = Array.Empty<string>(), message = $"Sage HR could not be reached or rejected the API key: {exception.GetBaseException().Message}" });
        }
    }

    [HttpPost("tachomaster/sync-drivers"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncTachoMasterDrivers(CancellationToken ct)
    {
        // This historical endpoint used to write Driver Master outside the integration lease.
        // Route it through the single, Member-Code-first coordinator so manual and scheduled
        // requests obey exactly the same owner-qualified lease.
        return Ok(await coordinator.SyncTachoMasterAsync(User.Identity?.Name ?? "admin:manual", ct));
#pragma warning disable CS0162 // retained below temporarily for a low-risk endpoint-contract transition
        if (!tachoMaster.IsConfigured)
            return BadRequest(new { configured = false, matched = 0, missingSettings = tachoMaster.MissingSettings, message = "TachoMaster is not configured." });
        try
        {
            var profiles = await tachoMaster.GetDriverProfilesAsync(ct);
            var drivers = await db.Drivers.Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
            await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
            var byName = profiles.GroupBy(profile => NormalisePersonName(profile.DriverName)).ToDictionary(group => group.Key, group => group.First());
            var byEmployee = profiles.Where(profile => !string.IsNullOrWhiteSpace(profile.EmployeeNumber))
                .GroupBy(profile => NormalisePersonName(profile.EmployeeNumber)).ToDictionary(group => group.Key, group => group.First());
            var matched = 0;
            foreach (var driver in drivers)
            {
                TachoDriverProfile? profile = null;
                if (!string.IsNullOrWhiteSpace(driver.TachoName)) byName.TryGetValue(NormalisePersonName(driver.TachoName), out profile);
                if (profile is null && !string.IsNullOrWhiteSpace(driver.EmployeeNumber)) byEmployee.TryGetValue(NormalisePersonName(driver.EmployeeNumber), out profile);
                if (profile is null) continue;
                driver.TachoMasterDriverId = profile.MemberCode.ToString();
                driver.TachoCardNumber = profile.CardNumber;
                driver.TachoDriveAvailableTodayMinutes = profile.DriveAvailableTodayMinutes;
                driver.TachoDriveAvailableWeekMinutes = profile.DriveAvailableWeekMinutes;
                driver.TachoWorkAvailableWeekMinutes = profile.WorkAvailableWeekMinutes;
                driver.LastTachoSyncUtc = DateTimeOffset.UtcNow;
                await MasterDetailStore.SaveAsync(db, "driver", driver.EmployeeNumber, JsonSerializer.Serialize(driver), "TachoMaster driver directory", User.Identity?.Name, ct);
                matched++;
            }
            return Ok(new { configured = true, connected = true, sourceDrivers = profiles.Count, matched, unmatched = Math.Max(drivers.Count - matched, 0), syncedAtUtc = DateTimeOffset.UtcNow,
                message = $"TachoMaster matched {matched} driver(s), using the Tacho Name column first." });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster driver directory sync failed.");
            return Ok(new { configured = true, connected = false, sourceDrivers = 0, matched = 0, unmatched = 0, syncedAtUtc = DateTimeOffset.UtcNow,
                message = $"TachoMaster driver sync failed: {exception.GetBaseException().Message}. No master driver records were changed." });
        }
#pragma warning restore CS0162
    }

    [HttpGet("roadtech/status")]
    public async Task<IActionResult> RoadTechStatus(CancellationToken ct)
    {
        if (!tracking.IsConfigured)
        {
            var missing = new List<string>();
            if (!tracking.Enabled) missing.Add("RoadTech enabled flag");
            if (string.IsNullOrWhiteSpace(tracking.BaseUrl)) missing.Add("RoadTech base URL");
            if (string.IsNullOrWhiteSpace(tracking.ApiKey)) missing.Add("RoadTech access token");
            if (string.IsNullOrWhiteSpace(tracking.Username)) missing.Add("RoadTech username");
            if (string.IsNullOrWhiteSpace(tracking.Password)) missing.Add("RoadTech password");
            if (string.IsNullOrWhiteSpace(tracking.CompanyCode)) missing.Add("RoadTech company code");
            return Ok(new { configured = false, connected = false, recordCount = 0, latestEventUtc = (DateTimeOffset?)null, missingSettings = missing, message = $"RoadTech runtime settings are incomplete: {string.Join(", ", missing)}." });
        }

        try
        {
            var items = await dotTracking.GetLatestVehicleEventsAsync(ct);
            var records = items.Select(DotTelemetryRecord.FromProvider).ToList();
            var latest = records.Count == 0 ? (DateTimeOffset?)null : records.Max(record => record.EventTimeUtc);
            return Ok(new { configured = true, connected = records.Count > 0, recordCount = records.Count, latestEventUtc = latest, missingSettings = Array.Empty<string>(), message = records.Count > 0 ? $"RoadTech connected and returned {records.Count} vehicle record(s)." : "RoadTech connected but returned zero live vehicle records." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or JsonException)
        {
            logger.LogWarning(exception, "RoadTech status check failed.");
            return Ok(new { configured = true, connected = false, recordCount = 0, latestEventUtc = (DateTimeOffset?)null, missingSettings = Array.Empty<string>(), message = $"RoadTech could not be reached or rejected the credentials: {exception.GetBaseException().Message}" });
        }
    }

    [HttpPost("sage-hr/sync-drivers"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncDrivers(CancellationToken ct)
    {
        if (!sageHr.IsConfigured)
        {
            return BadRequest(new { configured = false, missingSettings = sageHr.MissingSettings, message = $"Sage HR cannot sync until these settings are complete: {string.Join(", ", sageHr.MissingSettings)}." });
        }

        try
        {
            var employees = await sageHr.GetActiveEmployeesAsync(ct);
            var rawCandidates = employees.Where(IsDriver).ToList();
            // Sage can return the same employee more than once when historical
            // team/position records are expanded. De-duplicate before touching
            // the unique EmployeeNumber index in Azure SQL.
            var candidates = rawCandidates
                .GroupBy(employee => string.IsNullOrWhiteSpace(employee.EmployeeNumber)
                    ? $"SAGE-{employee.Id}"
                    : employee.EmployeeNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var created = 0; var updated = 0; var skipped = rawCandidates.Count - candidates.Count;
            var existingNumbers = (await db.Drivers.AsNoTracking().Select(driver => driver.EmployeeNumber).ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var employee in candidates)
            {
                var employeeNumber = ClipRequired(string.IsNullOrWhiteSpace(employee.EmployeeNumber) ? $"SAGE-{employee.Id}" : employee.EmployeeNumber.Trim(), 40);
                var displayName = ClipRequired($"{employee.FirstName} {employee.LastName}".Trim(), 160);
                if (string.IsNullOrWhiteSpace(displayName)) { skipped++; continue; }
                var mobileNumber = Clip(employee.MobilePhone, 40);
                var driverType = Clip(employee.Position, 80);
                var driverGroup = Clip(employee.Team, 80);
                string? tachoName = null;
                string? skills = null;
                if (!existingNumbers.Contains(employeeNumber))
                {
                    var id = Guid.NewGuid();
                    await db.Database.ExecuteSqlInterpolatedAsync($@"
                        INSERT INTO dbo.Drivers (Id, EmployeeNumber, DisplayName, TachoName, MobileNumber, DriverType, DriverGroup, Skills, Active)
                        VALUES ({id}, {employeeNumber}, {displayName}, {tachoName}, {mobileNumber}, {driverType}, {driverGroup}, {skills}, {true})", ct);
                    existingNumbers.Add(employeeNumber);
                    created++;
                }
                else
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($@"
                        UPDATE dbo.Drivers SET DisplayName = {displayName}, MobileNumber = {mobileNumber}, DriverType = {driverType}, DriverGroup = {driverGroup}, Active = {true}
                        WHERE EmployeeNumber = {employeeNumber}", ct);
                    updated++;
                }
            }
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "sagehrsync",
                IdempotencyKey = $"sagehrsync:{Guid.NewGuid():N}",
                PayloadJson = JsonSerializer.Serialize(new { sourceEmployeeCount = employees.Count, driverCandidateCount = candidates.Count, created, updated, skipped }),
                Source = "Sage HR driver synchronisation",
                Status = StagingStatus.Promoted,
                ReviewedAtUtc = DateTimeOffset.UtcNow,
                ReviewedBy = User.Identity?.Name,
                ReviewNote = "Transactional Sage HR sync using the production-compatible driver columns."
            });
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return Ok(new { sourceEmployeeCount = employees.Count, driverCandidateCount = candidates.Count, created, updated, skipped, syncedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Sage HR driver sync failed.");
            return Ok(new { configured = true, connected = false, sourceEmployeeCount = 0, driverCandidateCount = 0, created = 0, updated = 0, skipped = 0, syncedAtUtc = DateTimeOffset.UtcNow, message = $"Sage HR driver sync failed: {exception.GetBaseException().Message}. No driver records were changed." });
        }
    }

    private bool IsDriver(SageHrEmployee employee) =>
        DriverPopulationRules.IsSageDriver(employee, sageHr.DriverTeamName, sageHr.DriverPositionKeyword);

    private static string NormalisePersonName(string? value) => string.Join(' ', (value ?? string.Empty)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
        .Select(word => new string(word.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()))
        .Where(word => word.Length > 0).OrderBy(word => word, StringComparer.Ordinal));

    [HttpGet("fleetio/status")]
    public async Task<IActionResult> FleetioStatus(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured) return Ok(new { configured = false, connected = false, sampleVehicleCount = 0, missingSettings = fleetioClient.MissingSettings, message = $"Fleetio runtime settings are incomplete: {string.Join(", ", fleetioClient.MissingSettings)}." });
        try
        {
            var summary = await fleetioClient.GetVehicleSummaryAsync(ct);
            return Ok(new { configured = true, connected = summary.Connected, sampleVehicleCount = summary.SampleVehicleCount, missingSettings = Array.Empty<string>(), message = "Fleetio is connected for vehicle service and VOR data." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Fleetio status check failed.");
            return Ok(new { configured = true, connected = false, sampleVehicleCount = 0, missingSettings = Array.Empty<string>(), message = $"Fleetio could not be reached or rejected the credentials: {exception.GetBaseException().Message}" });
        }
    }
    [HttpGet("fleetio/vehicle-alignment")]
    public async Task<IActionResult> FleetioVehicleAlignment(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured) return Ok(new { configured = false, connected = false, matched = 0, unmatchedFleetio = 0, missingInFleetio = 0, missingSettings = fleetioClient.MissingSettings, records = Array.Empty<object>(), message = $"Fleetio runtime settings are incomplete: {string.Join(", ", fleetioClient.MissingSettings)}." });
        try
        {
            var fleetioVehicles = await fleetioClient.GetVehiclesAsync(100, ct);
            var tmsVehicles = (await db.Vehicles.AsNoTracking().Where(vehicle => vehicle.Active).OrderBy(vehicle => vehicle.Registration).ToListAsync(ct))
                .Where(vehicle => !Regex.IsMatch(vehicle.Registration, "^C\\d{5,}$", RegexOptions.IgnoreCase)).ToList();
            var fleetioLookup = BuildFleetioLookup(fleetioVehicles);
            var matchedFleetioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var records = tmsVehicles.Select(vehicle =>
            {
                var match = VehicleKeys(vehicle.Registration).Select(key => fleetioLookup.GetValueOrDefault(key)).FirstOrDefault(item => item is not null);
                if (match is not null && !string.IsNullOrWhiteSpace(match.Id)) matchedFleetioIds.Add(match.Id);
                return new FleetioVehicleAlignmentRecord(vehicle.Id, vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation, match?.Id, match?.Registration, match?.Name, match?.FleetNumber, match?.Status, match?.Vor, match?.PmiDueUtc, match?.MotDueUtc, match?.ServiceStatus, match is not null ? "Matched" : "MissingInFleetio");
            }).ToList();
            var unmatched = fleetioVehicles.Where(vehicle => string.IsNullOrWhiteSpace(vehicle.Id) || !matchedFleetioIds.Contains(vehicle.Id))
                .Select(vehicle => new FleetioVehicleAlignmentRecord(null, null, null, null, vehicle.Id, vehicle.Registration, vehicle.Name, vehicle.FleetNumber, vehicle.Status, vehicle.Vor, vehicle.PmiDueUtc, vehicle.MotDueUtc, vehicle.ServiceStatus, "UnmatchedFleetio"));
            records.AddRange(unmatched);
            return Ok(new { configured = true, connected = true, matched = records.Count(item => item.Status == "Matched"), unmatchedFleetio = records.Count(item => item.Status == "UnmatchedFleetio"), missingInFleetio = records.Count(item => item.Status == "MissingInFleetio"), missingSettings = Array.Empty<string>(), records, message = $"Fleetio returned {fleetioVehicles.Count} vehicle record(s) for alignment." });
        }
        catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(exception, "Fleetio vehicle alignment failed.");
            return Ok(new { configured = true, connected = false, matched = 0, unmatchedFleetio = 0, missingInFleetio = 0, missingSettings = Array.Empty<string>(), records = Array.Empty<object>(), message = $"Fleetio could not be reached or rejected the credentials: {exception.GetBaseException().Message}" });
        }
    }


    [HttpPost("fleetio/sync-vehicles"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncFleetioVehicles(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured) return BadRequest(new { configured = false, missingSettings = fleetioClient.MissingSettings, message = $"Fleetio cannot sync until these settings are complete: {string.Join(", ", fleetioClient.MissingSettings)}." });
        try
        {
            var fleetioVehicles = await fleetioClient.GetVehiclesAsync(100, ct);
            var tmsVehicles = await db.Vehicles.Where(vehicle => vehicle.Active).ToListAsync(ct);
            var fleetioLookup = BuildFleetioLookup(fleetioVehicles);
            var updated = 0;
            var created = 0;
            var missingInFleetio = 0;
            var quarantinedPlaceholders = 0;
            foreach (var vehicle in tmsVehicles)
            {
                // Fleetio can expose unregistered assets as C###### identifiers.
                // They are not TMS vehicles and must not be promoted into the
                // master register when the workbook is the source of truth.
                if (Regex.IsMatch(vehicle.Registration, "^C\\d{5,}$", RegexOptions.IgnoreCase))
                {
                    vehicle.Active = false;
                    quarantinedPlaceholders++;
                    continue;
                }
                var match = VehicleKeys(vehicle.Registration).Select(key => fleetioLookup.GetValueOrDefault(key)).FirstOrDefault(item => item is not null);
                if (match is null) { missingInFleetio++; continue; }
                vehicle.FleetioId = match.Id;
                vehicle.FleetioName = Clip(match.Name, 160);
                vehicle.FleetioStatus = Clip(match.Status, 80);
                vehicle.FleetioVor = match.Vor;
                vehicle.FleetioPmiDueUtc = match.PmiDueUtc;
                vehicle.FleetioMotDueUtc = match.MotDueUtc;
                vehicle.FleetioServiceStatus = Clip(match.ServiceStatus, 160);
                vehicle.FleetioLastSyncedUtc = DateTimeOffset.UtcNow;
                if (string.IsNullOrWhiteSpace(vehicle.FleetNumber)) vehicle.FleetNumber = Clip(match.FleetNumber, 40);
                updated++;
            }
            // Do not create Fleetio-only vehicles. The workbook/master register
            // is authoritative; Fleetio is an enrichment source only.
            await db.SaveChangesAsync(ct);
            return Ok(new { sourceVehicleCount = fleetioVehicles.Count, tmsVehicleCount = tmsVehicles.Count - quarantinedPlaceholders, updated, created, missingInFleetio, quarantinedPlaceholders, syncedAtUtc = DateTimeOffset.UtcNow });
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Fleetio vehicle sync failed.");
            return Ok(new { configured = true, connected = false, sourceVehicleCount = 0, tmsVehicleCount = 0, updated = 0, missingInFleetio = 0, syncedAtUtc = DateTimeOffset.UtcNow, message = $"Fleetio vehicle sync failed: {exception.GetBaseException().Message}. No vehicle records were changed." });
        }
    }

    private static Dictionary<string, FleetioVehicle> BuildFleetioLookup(IReadOnlyList<FleetioVehicle> fleetioVehicles)
    {
        var lookup = new Dictionary<string, FleetioVehicle>(StringComparer.OrdinalIgnoreCase);
        foreach (var vehicle in fleetioVehicles)
        {
            foreach (var key in VehicleKeys(vehicle.Registration))
            {
                lookup.TryAdd(key, vehicle);
            }
        }
        return lookup;
    }

    private static IReadOnlyList<string> VehicleKeys(params string?[] values)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            var key = NormaliseVehicleKey(value!);
            if (key.Length == 0) continue;
            keys.Add(key);
            if (key.Length > 3) keys.Add(key[^3..]);
            if (key.EndsWith("H", StringComparison.OrdinalIgnoreCase) && key.Length > 4) keys.Add(key[..^1]);
        }
        return keys.ToList();
    }

    private static string? Clip(string? value, int maxLength) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string ClipRequired(string value, int maxLength) => value.Trim().Length <= maxLength ? value.Trim() : value.Trim()[..maxLength];
    private static string NormaliseVehicleKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record FleetioVehicleAlignmentRecord(Guid? TmsVehicleId, string? TmsRegistration, string? TmsFleetNumber, string? TmsAbbreviation, string? FleetioId, string? FleetioRegistration, string? FleetioName, string? FleetioFleetNumber, string? FleetioStatus, bool? FleetioVor, DateTimeOffset? PmiDueUtc, DateTimeOffset? MotDueUtc, string? ServiceStatus, string Status);
