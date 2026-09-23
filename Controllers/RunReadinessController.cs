using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/runs"), Authorize]
public sealed class RunReadinessController(TmsDbContext db) : ControllerBase
{
    [HttpGet("readiness")]
    public async Task<IActionResult> Readiness([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .Take(1000)
            .ToList();
        await RunOperationalStore.EnrichAsync(db, loads, ct);

        Dictionary<Guid, Vehicle> vehicles;
        Dictionary<Guid, Driver> drivers;
        try { vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToDictionaryAsync(x => x.Id, ct); }
        catch { db.ChangeTracker.Clear(); vehicles = []; }
        try { drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToDictionaryAsync(x => x.Id, ct); }
        catch { db.ChangeTracker.Clear(); drivers = []; }

        var pending = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.Status == StagingStatus.PendingReview)
            .OrderByDescending(x => x.ReceivedAtUtc).Take(2000).ToListAsync(ct);
        var pendingForDay = pending.Count(x => PayloadMatchesDate(x.PayloadJson, day));

        var assignedDriverIds = loads.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).Distinct().ToArray();
        var assignedVehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToArray();
        var assignedDrivers = assignedDriverIds.Length;
        var assignedVehicles = assignedVehicleIds.Length;
        var missingAllocations = loads.Count(x => x.DriverId is null || x.VehicleId is null);
        var vorConflicts = loads.Count(x => x.VehicleId is Guid id && vehicles.TryGetValue(id, out var vehicle) && IsVor(vehicle));
        var tachoConcerns = loads.Count(x => x.DriverId is Guid id && drivers.TryGetValue(id, out var driver) && string.IsNullOrWhiteSpace(driver.TachoName));
        var geofenceGaps = loads.Sum(x => x.Stops.Count(stop => stop.Latitude is null || stop.Longitude is null));
        var ready = loads.Count > 0 && missingAllocations == 0 && vorConflicts == 0 && tachoConcerns == 0 && geofenceGaps == 0 && pendingForDay == 0;

        PlanLockInfo? planLock = null;
        try { planLock = await PlanLockStore.GetAsync(db, day, ct); } catch { db.ChangeTracker.Clear(); }

        return Ok(new
        {
            planningDate = day,
            generatedAtUtc = DateTimeOffset.UtcNow,
            source = "Reconciled Operational Runs",
            ready,
            runs = loads.Count,
            assignedDrivers,
            activeDrivers = drivers.Count,
            assignedVehicles,
            assignedVehicleIds,
            activeVehicles = vehicles.Count,
            missingAllocations,
            vorConflicts,
            tachoConcerns,
            geofenceGaps,
            unreviewedOrders = pendingForDay,
            planLock
        });
    }

    private static bool PayloadMatchesDate(string json, DateOnly day)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("collectionDate", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.Equals("deliveryDate", StringComparison.OrdinalIgnoreCase)) continue;
                var text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                if (DateOnly.TryParse(text, out var parsed) && parsed == day) return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static bool IsVor(Vehicle vehicle) => vehicle.FleetioVor == true ||
        (vehicle.FleetioStatus?.Contains("VOR", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (vehicle.FleetioStatus?.Contains("out of service", StringComparison.OrdinalIgnoreCase) ?? false);
}
