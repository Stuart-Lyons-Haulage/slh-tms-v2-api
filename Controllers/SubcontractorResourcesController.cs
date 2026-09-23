using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/subcontractors")]
[Authorize]
public sealed class SubcontractorResourcesController(TmsDbContext db) : ControllerBase
{
    private const string SubcontractorMarker = "Subcontractor:";

    [HttpPost("resources")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CreateResources(SubcontractorResourcesRequest request, CancellationToken ct)
    {
        var company = Clean(request.Company);
        if (string.IsNullOrWhiteSpace(company))
            return BadRequest(new { message = "Subcontractor company is required." });

        Driver? driver = null;
        Vehicle? vehicle = null;
        var createdDriver = false;
        var createdVehicle = false;
        var trackingLinked = false;

        if (!string.IsNullOrWhiteSpace(request.DriverName))
        {
            var driverName = Clean(request.DriverName)!;
            var employeeNumber = !string.IsNullOrWhiteSpace(request.DriverReference)
                ? Clip(request.DriverReference, 40)!.ToUpperInvariant()
                : BuildDriverReference(company, driverName);

            driver = await db.Drivers.FirstOrDefaultAsync(item => item.EmployeeNumber == employeeNumber, ct);
            if (driver is null)
            {
                driver = new Driver
                {
                    EmployeeNumber = employeeNumber,
                    DisplayName = Clip(driverName, 160)!,
                    MobileNumber = Clip(request.DriverMobile, 40),
                    TachoName = Clip(request.TachoName, 160),
                    DriverType = "Subcontractor",
                    DriverGroup = Clip(company, 80),
                    Skills = Clip(request.DriverSkills, 160),
                    Active = true
                };
                db.Drivers.Add(driver);
                createdDriver = true;
            }
            else
            {
                if (!string.Equals(driver.DriverType, "Subcontractor", StringComparison.OrdinalIgnoreCase))
                    return Conflict(new { message = $"Driver reference {employeeNumber} already belongs to a non-subcontractor driver." });

                driver.DisplayName = Clip(driverName, 160)!;
                driver.MobileNumber = Clip(request.DriverMobile, 40) ?? driver.MobileNumber;
                driver.TachoName = Clip(request.TachoName, 160) ?? driver.TachoName;
                driver.DriverGroup = Clip(company, 80);
                driver.Skills = Clip(request.DriverSkills, 160) ?? driver.Skills;
                driver.Active = true;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.VehicleRegistration))
        {
            var registration = NormaliseRegistration(request.VehicleRegistration!);
            if (registration.Length < 3)
                return BadRequest(new { message = "A valid subcontractor vehicle registration is required." });

            vehicle = await db.Vehicles.FirstOrDefaultAsync(item => item.Registration.Replace(" ", "").ToUpper() == registration, ct);
            if (vehicle is null)
            {
                vehicle = new Vehicle
                {
                    Registration = registration,
                    Abbreviation = Clip(request.VehicleAbbreviation, 20)?.ToUpperInvariant(),
                    Notes = BuildVehicleNotes(company, request.TrackingProvider, request.TrackingKey),
                    Active = true
                };
                db.Vehicles.Add(vehicle);
                createdVehicle = true;
            }
            else
            {
                var isSubcontractorVehicle = IsSubcontractorVehicle(vehicle);
                if (!isSubcontractorVehicle)
                    return Conflict(new { message = $"Vehicle {registration} already exists in the SLH vehicle master and is not marked as a subcontractor." });

                // Never assign every subcontractor the same fleet number. FleetNumber is one of
                // the live Falcon correlation aliases, so a shared value such as SUB could cause
                // one external vehicle's telemetry to be attached to another external run.
                if (string.Equals(vehicle.FleetNumber, "SUB", StringComparison.OrdinalIgnoreCase))
                    vehicle.FleetNumber = null;
                vehicle.Abbreviation = Clip(request.VehicleAbbreviation, 20)?.ToUpperInvariant() ?? vehicle.Abbreviation;
                vehicle.Notes = BuildVehicleNotes(company, request.TrackingProvider, request.TrackingKey);
                vehicle.Active = true;
            }
        }

        if (driver is null && vehicle is null)
            return BadRequest(new { message = "Enter a subcontractor driver, vehicle, or both." });

        string? dotTrackingKey = null;
        if (vehicle is not null && IsDotProvider(request.TrackingProvider))
        {
            dotTrackingKey = Clip(Clean(request.TrackingKey) ?? vehicle.Registration, 200)!;
            var existingForKey = await db.IntegrationMappings.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Provider == "DotTracking" && item.ExternalKey == dotTrackingKey && item.TmsEntityType == "Vehicle" && item.Active, ct);
            if (existingForKey is not null && existingForKey.TmsEntityId != vehicle.Id)
                return Conflict(new { message = $"DOT/Falcon tracking identifier {dotTrackingKey} is already linked to another vehicle." });
        }

        // Validate all identity conflicts before this first durable write. A bad tracking alias
        // must never leave behind a half-created subcontractor vehicle or driver.
        await db.SaveChangesAsync(ct);

        if (vehicle is not null && dotTrackingKey is not null)
            trackingLinked = await UpsertVehicleTrackingMapping(vehicle.Id, dotTrackingKey, company, ct);

        if (driver is not null)
        {
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Driver",
                EntityId = driver.Id,
                Action = createdDriver ? "SubcontractorCreated" : "SubcontractorUpdated",
                ChangedBy = User.Identity?.Name,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    company,
                    driver.DisplayName,
                    driver.EmployeeNumber,
                    driver.MobileNumber,
                    driver.TachoName,
                    driver.DriverType,
                    driver.DriverGroup
                })
            });
        }

        if (vehicle is not null)
        {
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Vehicle",
                EntityId = vehicle.Id,
                Action = createdVehicle ? "SubcontractorCreated" : "SubcontractorUpdated",
                ChangedBy = User.Identity?.Name,
                ChangesJson = JsonSerializer.Serialize(new
                {
                    company,
                    vehicle.Registration,
                    vehicle.Abbreviation,
                    trackingProvider = Clean(request.TrackingProvider),
                    trackingKey = dotTrackingKey,
                    trackingLinked
                })
            });
        }

        await db.SaveChangesAsync(ct);

        return Ok(new
        {
            company,
            driver = driver is null ? null : new
            {
                driver.Id,
                driver.EmployeeNumber,
                driver.DisplayName,
                driver.MobileNumber,
                driver.TachoName,
                driver.DriverType,
                driver.DriverGroup,
                driver.Active,
                created = createdDriver
            },
            vehicle = vehicle is null ? null : new
            {
                vehicle.Id,
                vehicle.Registration,
                vehicle.Abbreviation,
                vehicle.Active,
                created = createdVehicle,
                trackingProvider = Clean(request.TrackingProvider),
                trackingKey = dotTrackingKey,
                trackingLinked
            },
            message = TrackingMessage(vehicle, request.TrackingProvider, trackingLinked)
        });
    }

    [HttpGet("resources")]
    public async Task<IActionResult> ListResources(CancellationToken ct)
    {
        var drivers = await db.Drivers.AsNoTracking()
            .Where(item => item.Active && item.DriverType == "Subcontractor")
            .OrderBy(item => item.DriverGroup).ThenBy(item => item.DisplayName)
            .Select(item => new
            {
                item.Id,
                company = item.DriverGroup,
                item.EmployeeNumber,
                item.DisplayName,
                item.MobileNumber,
                item.TachoName
            })
            .ToListAsync(ct);

        var vehicles = await db.Vehicles.AsNoTracking()
            .Where(item => item.Active && ((item.Notes != null && item.Notes.Contains(SubcontractorMarker)) || item.FleetNumber == "SUB"))
            .OrderBy(item => item.Registration)
            .Select(item => new
            {
                item.Id,
                item.Registration,
                item.Abbreviation,
                item.Notes
            })
            .ToListAsync(ct);

        var vehicleIds = vehicles.Select(item => item.Id).ToList();
        var mappings = await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Active && item.Provider == "DotTracking" && item.TmsEntityType == "Vehicle" && vehicleIds.Contains(item.TmsEntityId))
            .OrderByDescending(item => item.UpdatedAtUtc)
            .ToListAsync(ct);

        return Ok(new
        {
            drivers,
            vehicles = vehicles.Select(item => new
            {
                item.Id,
                item.Registration,
                item.Abbreviation,
                item.Notes,
                dotTrackingKey = mappings.FirstOrDefault(mapping => mapping.TmsEntityId == item.Id)?.ExternalKey
            })
        });
    }

    private async Task<bool> UpsertVehicleTrackingMapping(Guid vehicleId, string externalKey, string company, CancellationToken ct)
    {
        var mapping = await db.IntegrationMappings
            .FirstOrDefaultAsync(item => item.Provider == "DotTracking" && item.ExternalKey == externalKey && item.TmsEntityType == "Vehicle", ct)
            ?? await db.IntegrationMappings
                .FirstOrDefaultAsync(item => item.Provider == "DotTracking" && item.TmsEntityType == "Vehicle" && item.TmsEntityId == vehicleId, ct);

        if (mapping is null)
        {
            mapping = new IntegrationMapping
            {
                Provider = "DotTracking",
                ExternalKey = externalKey,
                ExternalLabel = $"Subcontractor · {company}",
                TmsEntityType = "Vehicle",
                TmsEntityId = vehicleId,
                Active = true,
                Notes = "Subcontractor vehicle tracking alias",
                UpdatedBy = User.Identity?.Name
            };
            db.IntegrationMappings.Add(mapping);
        }
        else
        {
            mapping.ExternalKey = externalKey;
            mapping.ExternalLabel = $"Subcontractor · {company}";
            mapping.TmsEntityId = vehicleId;
            mapping.Active = true;
            mapping.Notes = "Subcontractor vehicle tracking alias";
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = User.Identity?.Name;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private static bool IsSubcontractorVehicle(Vehicle vehicle) =>
        string.Equals(vehicle.FleetNumber, "SUB", StringComparison.OrdinalIgnoreCase)
        || (vehicle.Notes?.Contains(SubcontractorMarker, StringComparison.OrdinalIgnoreCase) ?? false);

    private static string TrackingMessage(Vehicle? vehicle, string? provider, bool linked)
    {
        if (vehicle is null) return "Subcontractor driver added to the TMS master.";
        if (linked) return $"Subcontractor vehicle {vehicle.Registration} is linked to DOT/Falcon tracking and is available for live run correlation.";
        if (string.IsNullOrWhiteSpace(provider)) return $"Subcontractor vehicle {vehicle.Registration} was added. Live tracking will match automatically if Falcon reports the registration; otherwise add a DOT/Falcon tracking alias.";
        return $"Subcontractor vehicle {vehicle.Registration} was added. {provider.Trim()} is recorded as an external provider but is not integrated, so the TMS will not invent a live location.";
    }

    private static string BuildDriverReference(string company, string driverName)
    {
        var companyCode = Slug(company, 8);
        var driverCode = Slug(driverName, 12);
        var reference = $"SUB-{companyCode}-{driverCode}";
        return reference[..Math.Min(40, reference.Length)];
    }

    private static string BuildVehicleNotes(string company, string? provider, string? trackingKey)
    {
        var parts = new List<string> { $"{SubcontractorMarker} {company}" };
        if (!string.IsNullOrWhiteSpace(provider)) parts.Add($"Tracking provider: {provider.Trim()}");
        if (!string.IsNullOrWhiteSpace(trackingKey)) parts.Add($"Tracking key: {trackingKey.Trim()}");
        var notes = string.Join(" | ", parts);
        return notes[..Math.Min(500, notes.Length)];
    }

    private static bool IsDotProvider(string? value)
    {
        var normal = Slug(value ?? string.Empty, 80);
        return normal.Contains("DOT") || normal.Contains("FALCON") || normal.Contains("ROADTECH");
    }

    private static string NormaliseRegistration(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string Slug(string value, int max) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).Take(max).ToArray());
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Clip(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, max)];
}

public sealed record SubcontractorResourcesRequest(
    string? Company,
    string? DriverName,
    string? DriverReference,
    string? DriverMobile,
    string? TachoName,
    string? DriverSkills,
    string? VehicleRegistration,
    string? VehicleAbbreviation,
    string? TrackingProvider,
    string? TrackingKey);
