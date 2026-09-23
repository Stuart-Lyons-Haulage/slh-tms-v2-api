using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/operations")]
[Authorize]
public sealed class OperationalSnapshotController(TmsDbContext db, ILogger<OperationalSnapshotController> logger) : ControllerBase
{
    private static string SafeForLog(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty);

    [HttpGet("readiness-snapshot")]
    public async Task<IActionResult> Readiness([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var (loads, source) = await ReadLoads(day, ct);
        var vehicles = await ReadVehicles(ct);
        var drivers = await ReadDrivers(ct);

        var assignedVehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToHashSet();
        var assignedDriverIds = loads.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).Distinct().ToHashSet();
        var pendingReview = await PendingOrdersForDate(day, ct);
        var vorConflicts = loads.Count(x => x.VehicleId is Guid id && vehicles.TryGetValue(id, out var vehicle) && IsVor(vehicle));
        var tachoConcerns = loads.Count(x => x.DriverId is Guid id && drivers.TryGetValue(id, out var driver) && string.IsNullOrWhiteSpace(driver.TachoName));
        var geofenceGaps = loads.Sum(x => x.Stops.Count(stop => stop.Latitude is null || stop.Longitude is null));
        var missingAllocations = loads.Count(x => x.DriverId is null || x.VehicleId is null);
        var ready = loads.Count > 0 && missingAllocations == 0 && vorConflicts == 0 && tachoConcerns == 0 && geofenceGaps == 0 && pendingReview.Count == 0;

        PlanLockInfo? planLock = null;
        try { planLock = await PlanLockStore.GetAsync(db, day, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Plan-lock state is unavailable for {PlanningDate}.", day);
            db.ChangeTracker.Clear();
        }

        return Ok(new
        {
            planningDate = day,
            generatedAtUtc = DateTimeOffset.UtcNow,
            source,
            ready,
            runs = loads.Count,
            assignedDrivers = assignedDriverIds.Count,
            activeDrivers = drivers.Values.Count(x => x.Active),
            assignedVehicles = assignedVehicleIds.Count,
            activeVehicles = vehicles.Values.Count(x => x.Active),
            missingAllocations,
            vorConflicts,
            tachoConcerns,
            geofenceGaps,
            unreviewedOrders = pendingReview.Count,
            planLock
        });
    }

    [HttpGet("attention-snapshot")]
    public async Task<IActionResult> Attention([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var (loads, source) = await ReadLoads(day, ct);
        var vehicles = await ReadVehicles(ct);
        var drivers = await ReadDrivers(ct);
        var items = new List<object>();

        foreach (var staged in await PendingOrdersForDate(day, ct))
        {
            items.Add(new
            {
                id = $"staging-{staged.Id}", severity = "High", type = "OrderReview",
                title = "Order awaiting review", detail = staged.Source ?? "Customer order intake",
                entityId = staged.Id, entityType = "staging", href = "/staging"
            });
        }

        foreach (var load in loads)
        {
            if (load.DriverId is null)
                items.Add(Item(load, "High", "UnallocatedDriver", "Run has no driver", "Allocate a driver before dispatch."));
            if (load.VehicleId is null)
                items.Add(Item(load, "High", "UnallocatedVehicle", "Run has no vehicle", "Allocate a vehicle before dispatch."));
            if (load.Stops.Count == 0)
                items.Add(Item(load, "High", "NoStops", "Run has no operational stops", "Add the collection and delivery stops before dispatch."));

            if (load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) && IsVor(vehicle))
                items.Add(Item(load, "High", "VorVehicle", $"VOR vehicle allocated: {vehicle.Registration}", vehicle.FleetioStatus ?? "Vehicle is marked out of service."));
            if (load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) && string.IsNullOrWhiteSpace(driver.TachoName))
                items.Add(Item(load, "Medium", "TachoMapping", $"Driver missing Tacho mapping: {driver.DisplayName}", "Tacho-aware planning cannot be fully validated."));
        }

        // Needs Attention is an operational queue. A missing latitude/longitude is a mapping
        // diagnostic, not proof that the route cannot be geofenced. Use the same Site Master
        // and geofence resolver as the wallboards so this queue reflects actual geofence gaps.
        try
        {
            var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
            foreach (var load in loads.Where(load => load.Stops.Count > 0))
            {
                var gaps = OperationalStopOrdering.Order(load.Stops)
                    .Select((stop, index) => new { Stop = stop, Sequence = index + 1, Resolution = resolver.Resolve(stop.Name) })
                    .Where(item => !item.Resolution.SiteMatched || !item.Resolution.GeofenceLinked)
                    .ToList();
                if (gaps.Count == 0) continue;

                var unresolved = gaps.Count(item => !item.Resolution.SiteMatched);
                var unlinked = gaps.Count(item => item.Resolution.SiteMatched && !item.Resolution.GeofenceLinked);
                var examples = string.Join(", ", gaps.Take(3).Select(item => $"{item.Sequence}. {item.Stop.Name}"));
                if (gaps.Count > 3) examples += $" + {gaps.Count - 3} more";
                var breakdown = string.Join(" · ", new[]
                {
                    unresolved > 0 ? $"{unresolved} site name unresolved" : null,
                    unlinked > 0 ? $"{unlinked} site recognised but geofence unlinked" : null
                }.Where(value => value is not null));

                items.Add(Item(load, "Medium", "GeofenceCoverage",
                    $"Run has {gaps.Count} geofence gap{(gaps.Count == 1 ? "" : "s")}",
                    $"{breakdown}. {examples}. Resolve in Site Master / Geofence Integrity."));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Geofence linkage attention enrichment unavailable for {PlanningDate}.", SafeForLog(day));
            db.ChangeTracker.Clear();
        }

        try
        {
            var loadIds = loads.Select(x => x.Id).ToList();
            if (loadIds.Count > 0)
            {
                var visits = await db.GeofenceVisits.AsNoTracking()
                    .Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value) && (x.Status == "SiteDelay" || x.Status == "PassThrough"))
                    .OrderByDescending(x => x.UpdatedAtUtc).Take(100).ToListAsync(ct);
                foreach (var visit in visits)
                {
                    var load = loads.FirstOrDefault(x => x.Id == visit.LoadId);
                    if (load is null) continue;
                    items.Add(Item(load, visit.Status == "SiteDelay" ? "High" : "Medium", visit.Status,
                        visit.Status == "SiteDelay" ? "Site dwell exceeded limit" : "Possible missed/short site visit",
                        visit.StatusReason ?? $"Dwell {visit.DwellMinutes} min"));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Geofence attention enrichment unavailable for {PlanningDate}.", SafeForLog(day));
            db.ChangeTracker.Clear();
        }

        return Ok(new { planningDate = day, generatedAtUtc = DateTimeOffset.UtcNow, source, count = items.Count, items });
    }

    private async Task<(List<Load> Loads, string Source)> ReadLoads(DateOnly day, CancellationToken ct)
    {
        try
        {
            var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
                .Where(x => x.PlanningDate == day && x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference)
                .Take(2000)
                .ToList();
            return (loads, "TMS planning sources (merged)");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Merged planning source unavailable for {PlanningDate}; using audited planning register.", day);
            db.ChangeTracker.Clear();
            var loads = await PlanningRegisterStore.ReadLoadsAsync(db, day, ct);
            return (loads.Where(x => x.Status != LoadStatus.Cancelled).ToList(), "TMS planning register");
        }
    }

    private async Task<Dictionary<Guid, Vehicle>> ReadVehicles(CancellationToken ct)
    {
        try { return await db.Vehicles.AsNoTracking().Where(x => x.Active).ToDictionaryAsync(x => x.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Vehicle enrichment unavailable for operational snapshot.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private async Task<Dictionary<Guid, Driver>> ReadDrivers(CancellationToken ct)
    {
        try { return await db.Drivers.AsNoTracking().Where(x => x.Active).ToDictionaryAsync(x => x.Id, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Driver enrichment unavailable for operational snapshot.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private async Task<List<StagedImport>> PendingOrdersForDate(DateOnly day, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.Status == StagingStatus.PendingReview)
            .OrderByDescending(x => x.ReceivedAtUtc).Take(2000).ToListAsync(ct);
        // Keep Dashboard readiness/attention on exactly the same planning-date
        // projection as Planning Review. In particular, a cross-date overnight order
        // belongs to its collection-day review queue, not both collection and delivery.
        return rows.Where(row => StagingQueueProjection.MatchesPlanningDate(row.PayloadJson, day)).ToList();
    }

    private static object Item(Load load, string severity, string type, string title, string detail) => new
    {
        id = $"{type}-{load.Id}", severity, type, title, detail,
        entityId = load.Id, entityType = "run", href = $"/timeline/run/{load.Id}"
    };

    private static bool IsVor(Vehicle vehicle) => vehicle.FleetioVor == true
        || (vehicle.FleetioStatus?.Contains("VOR", StringComparison.OrdinalIgnoreCase) ?? false)
        || (vehicle.FleetioStatus?.Contains("out of service", StringComparison.OrdinalIgnoreCase) ?? false);

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
