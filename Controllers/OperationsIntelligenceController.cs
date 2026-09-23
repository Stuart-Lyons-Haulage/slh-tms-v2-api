using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/intelligence")]
[Authorize]
public sealed class OperationsIntelligenceController(TmsDbContext db) : ControllerBase
{
    [HttpGet("attention")]
    public async Task<IActionResult> Attention([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops).Where(x => x.PlanningDate == day && x.Status != LoadStatus.Cancelled).ToListAsync(ct);
        var pending = await db.StagedImports.AsNoTracking().Where(x => x.Status == StagingStatus.PendingReview).OrderBy(x => x.ReceivedAtUtc).Take(100).ToListAsync(ct);
        var activeVehicleIds = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var vehicles = new Dictionary<Guid, Vehicle>();
        try
        {
            vehicles = await db.Vehicles.AsNoTracking().Where(x => activeVehicleIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        }
        catch
        {
            // Vehicle enrichment must never take down the attention queue if an optional master-data column is unavailable.
        }
        var activeDriverIds = loads.Where(x => x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().ToList();
        var drivers = new Dictionary<Guid, Driver>();
        try
        {
            drivers = await db.Drivers.AsNoTracking().Where(x => activeDriverIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        }
        catch
        {
            // Tacho enrichment is advisory here; core planning exceptions should still render.
        }
        var items = new List<object>();
        foreach (var staged in pending)
            items.Add(new { id = $"staging-{staged.Id}", severity = "High", type = "OrderReview", title = "Order awaiting review", detail = staged.Source ?? staged.EntityType, entityId = staged.Id, entityType = "staging", href = "/staging" });
        foreach (var load in loads)
        {
            if (load.DriverId is null) items.Add(Item(load, "High", "UnallocatedDriver", "Run has no driver", "Allocate a driver before dispatch."));
            if (load.VehicleId is null) items.Add(Item(load, "High", "UnallocatedVehicle", "Run has no vehicle", "Allocate a vehicle before dispatch."));
            if (load.Stops.Any(x => x.Latitude is null || x.Longitude is null)) items.Add(Item(load, "Medium", "MissingGeocode", "Run contains an unmapped stop", "Map all operational stops so routing and geofence matching are reliable."));
            if (load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) && IsVor(vehicle))
                items.Add(Item(load, "High", "VorVehicle", $"VOR vehicle allocated: {vehicle.Registration}", vehicle.FleetioStatus ?? "Vehicle is marked out of service."));
            if (load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) && string.IsNullOrWhiteSpace(driver.TachoName))
                items.Add(Item(load, "Medium", "TachoMapping", $"Driver missing Tacho mapping: {driver.DisplayName}", "Tacho-aware planning cannot be fully validated."));
        }
        try
        {
            var loadIds = loads.Select(x => x.Id).ToList();
            if (loadIds.Count > 0)
            {
                var visits = await db.GeofenceVisits.AsNoTracking().Where(x => x.LoadId != null && loadIds.Contains(x.LoadId.Value) && (x.Status == "SiteDelay" || x.Status == "PassThrough")).OrderByDescending(x => x.UpdatedAtUtc).Take(100).ToListAsync(ct);
                foreach (var visit in visits)
                {
                    var load = loads.First(x => x.Id == visit.LoadId);
                    items.Add(Item(load, visit.Status == "SiteDelay" ? "High" : "Medium", visit.Status, visit.Status == "SiteDelay" ? "Site dwell exceeded limit" : "Possible missed/short site visit", visit.StatusReason ?? $"Dwell {visit.DwellMinutes} min"));
                }
            }
        }
        catch
        {
            // Geofence intelligence is additive; core attention items remain useful without it.
        }
        return Ok(new { planningDate = day, generatedAtUtc = DateTimeOffset.UtcNow, count = items.Count, items });
    }

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] string q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return Ok(Array.Empty<object>());
        var term = $"%{q}%";
        var results = new List<object>();
        results.AddRange((await db.TransportOrders.AsNoTracking().Where(x => EF.Functions.Like(x.Reference, term) || EF.Functions.Like(x.CustomerCode, term)).OrderByDescending(x => x.CreatedAtUtc).Take(8).ToListAsync(ct)).Select(x => new { type = "Order", id = x.Id, label = x.Reference, detail = $"{x.CustomerCode} · {x.CollectionDate:dd MMM yyyy}", href = $"/timeline/order/{x.Id}" }));
        results.AddRange((await db.Loads.AsNoTracking().Where(x => EF.Functions.Like(x.Reference, term)).OrderByDescending(x => x.PlanningDate).Take(8).ToListAsync(ct)).Select(x => new { type = "Run", id = x.Id, label = x.Reference, detail = $"{x.PlanningDate:dd MMM yyyy} · {x.Status}", href = $"/timeline/run/{x.Id}" }));
        results.AddRange((await db.Vehicles.AsNoTracking().Where(x => EF.Functions.Like(x.Registration, term) || (x.FleetNumber != null && EF.Functions.Like(x.FleetNumber, term))).Take(6).ToListAsync(ct)).Select(x => new { type = "Vehicle", id = x.Id, label = x.Registration, detail = x.FleetNumber ?? "Vehicle", href = "/fleet-assets" }));
        results.AddRange((await db.Drivers.AsNoTracking().Where(x => EF.Functions.Like(x.DisplayName, term) || EF.Functions.Like(x.EmployeeNumber, term)).Take(6).ToListAsync(ct)).Select(x => new { type = "Driver", id = x.Id, label = x.DisplayName, detail = x.EmployeeNumber, href = "/drivers" }));
        results.AddRange((await db.Customers.AsNoTracking().Where(x => EF.Functions.Like(x.Name, term) || EF.Functions.Like(x.Code, term)).Take(6).ToListAsync(ct)).Select(x => new { type = "Customer", id = x.Id, label = x.Name, detail = x.Code, href = "/customers" }));
        results.AddRange((await db.Sites.AsNoTracking().Where(x => EF.Functions.Like(x.Name, term) || EF.Functions.Like(x.ExternalCode, term)).Take(6).ToListAsync(ct)).Select(x => new { type = "Site", id = x.Id, label = x.Name, detail = x.ExternalCode, href = "/sites" }));
        return Ok(results.Take(30));
    }

    [HttpGet("freshness")]
    public async Task<IActionResult> Freshness(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var tracking = await db.VehicleLiveStatuses.AsNoTracking().MaxAsync(x => (DateTimeOffset?)x.LastEventTimeUtc, ct);
        // LastTachoSyncUtc is runtime-only / NotMapped on Driver and cannot be translated by EF. Use persisted Tacho-related intake/status evidence instead.
        var tacho = await db.StagedImports.AsNoTracking().Where(x => x.Source != null && x.Source.Contains("Tacho")).MaxAsync(x => (DateTimeOffset?)x.ReceivedAtUtc, ct);
        var email = await db.StagedImports.AsNoTracking().Where(x => x.Source != null).MaxAsync(x => (DateTimeOffset?)x.ReceivedAtUtc, ct);
        var sage = await db.StagedImports.AsNoTracking().Where(x => x.Source != null && x.Source.Contains("Sage")).MaxAsync(x => (DateTimeOffset?)x.ReceivedAtUtc, ct);
        return Ok(new { generatedAtUtc = now, sources = new[] { Fresh("Tracking", tracking, now, 10, 30), Fresh("Tacho", tacho, now, 30, 120), Fresh("Info mailbox", email, now, 15, 60), Fresh("Sage HR", sage, now, 180, 720) } });
    }

    [HttpGet("timeline/run/{id:guid}")]
    public async Task<IActionResult> RunTimeline(Guid id, CancellationToken ct)
    {
        var load = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);
        if (load is null) return NotFound();
        var events = new List<TimelineEvent> { new(load.CreatedAtUtc, "Run created", load.Reference, "Planning", null) };
        try
        {
            var logs = await db.DriverStatusLogs.AsNoTracking().Where(x => x.LoadId == id).OrderBy(x => x.CapturedAtUtc).ToListAsync(ct);
            events.AddRange(logs.Select(x => new TimelineEvent(x.CapturedAtUtc, x.Status, x.Notes ?? "Operational status updated", "Operations", x.CapturedBy)));
        }
        catch { }
        try
        {
            var visits = await db.GeofenceVisits.AsNoTracking().Where(x => x.LoadId == id).ToListAsync(ct);
            foreach (var v in visits)
            {
                events.Add(new(v.EnteredAtUtc, "Geofence arrival", v.StatusReason ?? v.VehicleIdentifier, "Tracking", null));
                if (v.ConfirmedAtUtc != null) events.Add(new(v.ConfirmedAtUtc.Value, "Site visit confirmed", $"Dwell threshold confirmed · {v.DwellMinutes} min", "Tracking", null));
                if (v.ExitedAtUtc != null) events.Add(new(v.ExitedAtUtc.Value, "Geofence departure", v.Status, "Tracking", null));
            }
        }
        catch { }
        try
        {
            var changes = await PlanLockStore.ChangesAsync(db, load.PlanningDate, load.PlanningDate, ct);
            events.AddRange(changes.Where(x => x.LoadId == id).Select(x => new TimelineEvent(x.ChangedAtUtc, x.ChangeType, x.Reason, "Plan change", x.ChangedBy)));
        }
        catch { }
        return Ok(new { entityType = "Run", id = load.Id, reference = load.Reference, planningDate = load.PlanningDate, status = load.Status.ToString(), events = events.OrderBy(x => x.AtUtc) });
    }

    [HttpGet("timeline/order/{id:guid}")]
    public async Task<IActionResult> OrderTimeline(Guid id, CancellationToken ct)
    {
        var order = await db.TransportOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (order is null) return NotFound();
        var events = new List<TimelineEvent> { new(order.CreatedAtUtc, "Order created", $"{order.CustomerCode} · {order.Reference}", "Order intake", null) };
        var staging = await db.StagedImports.AsNoTracking().Where(x => x.PayloadJson.Contains(order.Reference)).OrderBy(x => x.ReceivedAtUtc).Take(20).ToListAsync(ct);
        events.AddRange(staging.Select(x => new TimelineEvent(x.ReceivedAtUtc, "Source received", x.Source ?? x.EntityType, "Order intake", null)));
        events.AddRange(staging.Where(x => x.ReviewedAtUtc != null).Select(x => new TimelineEvent(x.ReviewedAtUtc!.Value, $"Review: {x.Status}", x.ReviewNote ?? "Order review completed", "Order review", x.ReviewedBy)));
        var stop = await db.LoadStops.AsNoTracking().FirstOrDefaultAsync(x => x.OrderId == id, ct);
        if (stop is not null)
        {
            var load = await db.Loads.AsNoTracking().SingleOrDefaultAsync(x => x.Id == stop.LoadId, ct);
            if (load is not null) events.Add(new(load.CreatedAtUtc, "Planned to run", load.Reference, "Planning", null));
        }
        return Ok(new { entityType = "Order", id = order.Id, reference = order.Reference, status = order.Status.ToString(), events = events.OrderBy(x => x.AtUtc) });
    }

    [HttpGet("plan-lock/{date}")]
    public async Task<IActionResult> PlanLock(DateOnly date, CancellationToken ct)
    {
        try { return Ok(await PlanLockStore.GetAsync(db, date, ct)); }
        catch { return Ok(null); }
    }

    [HttpPost("plan-lock/{date}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> LockPlan(DateOnly date, CancellationToken ct)
    {
        await PlanLockStore.LockAsync(db, date, User.Identity?.Name, ct);
        return Ok(await PlanLockStore.GetAsync(db, date, ct));
    }

    [HttpGet("plan-stability")]
    public async Task<IActionResult> PlanStability([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct)
    {
        if (from > to) return BadRequest("'from' must be on or before 'to'.");
        try
        {
            var changes = await PlanLockStore.ChangesAsync(db, from, to, ct);
            var lockedDays = 0; var baselineRuns = 0;
            for (var d = from; d <= to; d = d.AddDays(1))
            {
                var info = await PlanLockStore.GetAsync(db, d, ct);
                if (info != null) { lockedDays++; baselineRuns += info.BaselineRuns; }
            }
            var changedRuns = changes.Where(x => x.LoadId != null).Select(x => x.LoadId).Distinct().Count();
            var stability = baselineRuns == 0 ? (decimal?)null : Math.Round(Math.Max(0, baselineRuns - changedRuns) / (decimal)baselineRuns * 100m, 1);
            return Ok(new { from, to, lockedDays, baselineRuns, changedRuns, stabilityPercent = stability, driverSwaps = changes.Count(x => x.ChangeType == "Driver swap"), vehicleSwaps = changes.Count(x => x.ChangeType == "Vehicle swap"), routeAmendments = changes.Count(x => x.ChangeType == "Route amendment"), runChanges = changes.Count, changes, dataAvailable = true });
        }
        catch
        {
            return Ok(new { from, to, lockedDays = 0, baselineRuns = 0, changedRuns = 0, stabilityPercent = (decimal?)null, driverSwaps = 0, vehicleSwaps = 0, routeAmendments = 0, runChanges = 0, changes = Array.Empty<object>(), dataAvailable = false });
        }
    }

    [HttpGet("readiness")]
    public async Task<IActionResult> Readiness([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1));
        var loads = await db.Loads.AsNoTracking().Include(x => x.Stops).Where(x => x.PlanningDate == day && x.Status != LoadStatus.Cancelled).ToListAsync(ct);
        var vehicles = new List<Vehicle>();
        var drivers = new List<Driver>();
        try { vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).ToListAsync(ct); } catch { }
        try { drivers = await db.Drivers.AsNoTracking().Where(x => x.Active).ToListAsync(ct); } catch { }
        var assignedVehicleIds = loads.Where(x => x.VehicleId != null).Select(x => x.VehicleId!.Value).Distinct().ToHashSet();
        var assignedDriverIds = loads.Where(x => x.DriverId != null).Select(x => x.DriverId!.Value).Distinct().ToHashSet();
        var pendingReview = await db.StagedImports.AsNoTracking().CountAsync(x => x.Status == StagingStatus.PendingReview, ct);
        var vorConflicts = loads.Count(x => x.VehicleId is Guid id && vehicles.Any(v => v.Id == id && IsVor(v)));
        var tachoConcerns = loads.Count(x => x.DriverId is Guid id && drivers.Any(d => d.Id == id && string.IsNullOrWhiteSpace(d.TachoName)));
        var geofenceGaps = loads.Sum(x => x.Stops.Count(s => s.Latitude is null || s.Longitude is null));
        var missingAllocations = loads.Count(x => x.DriverId is null || x.VehicleId is null);
        var ready = missingAllocations == 0 && vorConflicts == 0 && tachoConcerns == 0 && geofenceGaps == 0 && pendingReview == 0;
        PlanLockInfo? planLock = null;
        try { planLock = await PlanLockStore.GetAsync(db, day, ct); } catch { }
        return Ok(new { planningDate = day, generatedAtUtc = DateTimeOffset.UtcNow, ready, runs = loads.Count, assignedDrivers = assignedDriverIds.Count, activeDrivers = drivers.Count, assignedVehicles = assignedVehicleIds.Count, activeVehicles = vehicles.Count, missingAllocations, vorConflicts, tachoConcerns, geofenceGaps, unreviewedOrders = pendingReview, planLock });
    }

    private static object Item(Load load, string severity, string type, string title, string detail) => new { id = $"{type}-{load.Id}", severity, type, title, detail, entityId = load.Id, entityType = "run", href = $"/timeline/run/{load.Id}" };
    private static bool IsVor(Vehicle v) => v.FleetioVor == true || string.Equals(v.FleetioStatus, "VOR", StringComparison.OrdinalIgnoreCase) || string.Equals(v.FleetioStatus, "Out of Service", StringComparison.OrdinalIgnoreCase) || string.Equals(v.FleetioStatus, "Vehicle Off Road", StringComparison.OrdinalIgnoreCase);
    private static object Fresh(string name, DateTimeOffset? last, DateTimeOffset now, int amberAfter, int redAfter)
    {
        double? age = last is null ? null : Math.Max(0, (now - last.Value).TotalMinutes);
        var state = age is null ? "red" : age <= amberAfter ? "green" : age <= redAfter ? "amber" : "red";
        return new { name, lastUpdatedUtc = last, ageMinutes = age is null ? (double?)null : Math.Round(age.Value, 1), state };
    }
    private sealed record TimelineEvent(DateTimeOffset AtUtc, string Title, string Detail, string Source, string? By);
}
