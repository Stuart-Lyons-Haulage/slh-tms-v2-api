using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Operations readiness controller: integration confidence, exceptions,
/// daily reconciliation, manual integration mappings, and driver status feedback.
/// All endpoints read from cached/stored data so the dashboard loads even
/// when live providers are down.
/// </summary>
[ApiController, Route("api/v1/operations")]
[Authorize]
public sealed class OperationsControlController(
    TmsDbContext db,
    SageHrClient sageHr,
    TachoMasterClient tachoMaster,
    DotTrackingOptions tracking,
    FleetioClient fleetioClient) : ControllerBase
{
    /// <summary>
    /// Integration confidence dashboard — shows whether each integration is
    /// configured, connected, and how well data is matching.
    /// Uses cached tables; does not call live providers.
    /// </summary>
    [HttpGet("confidence")]
    public async Task<IActionResult> Confidence(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var activeDrivers = await SafeCount(db.Drivers.Where(d => d.Active), ct);
        var activeVehicles = await SafeCount(db.Vehicles.Where(v => v.Active), ct);
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        var stagingImports = await SafeList(db.StagedImports.AsNoTracking().OrderByDescending(s => s.ReceivedAtUtc).Take(50), ct);

        var latestTracking = liveStatuses.Count > 0 ? liveStatuses.Max(s => s.LastEventTimeUtc) : (DateTimeOffset?)null;
        var trackingAge = latestTracking is not null ? now - latestTracking.Value : (TimeSpan?)null;
        var staleVehicles = liveStatuses.Count(s => latestTracking is not null && now - s.LastEventTimeUtc > TimeSpan.FromMinutes(30));
        var unmatchedTracking = liveStatuses.Count;

        var sageSync = stagingImports.FirstOrDefault(s => s.EntityType == "sagehrsync");
        var tachoSync = stagingImports.FirstOrDefault(s => s.EntityType == "tachomastersync");

        var fleetioVehicles = await SafeCount(db.Vehicles.Where(v => v.Active && v.FleetioId != null), ct);
        var vehiclesWithoutFleetio = await SafeCount(db.Vehicles.Where(v => v.Active && v.FleetioId == null), ct);
        var driversWithoutTacho = await SafeCount(db.Drivers.Where(d => d.Active && string.IsNullOrEmpty(d.TachoName)), ct);

        return Ok(new
        {
            generatedAtUtc = now,
            sageHr = new
            {
                configured = sageHr.IsConfigured,
                activeDrivers,
                driversWithoutTachoName = driversWithoutTacho,
                lastSyncUtc = sageSync?.ReviewedAtUtc,
                lastSyncSummary = sageSync?.PayloadJson,
            },
            tachoMaster = new
            {
                configured = tachoMaster.IsConfigured,
                driversWithTachoSync = activeDrivers - driversWithoutTacho,
                driversWithoutTachoName = driversWithoutTacho,
                lastSyncUtc = tachoSync?.ReviewedAtUtc,
            },
            dotTracking = new
            {
                configured = tracking.IsConfigured,
                liveVehicleCount = liveStatuses.Count,
                staleVehicleCount = staleVehicles,
                latestEventUtc = latestTracking,
                trackingAgeMinutes = trackingAge is not null ? (int)Math.Max(0, trackingAge.Value.TotalMinutes) : (int?)null,
            },
            fleetio = new
            {
                configured = fleetioClient.IsConfigured,
                matchedVehicles = fleetioVehicles,
                unmatchedVehicles = vehiclesWithoutFleetio,
            },
            emailIntake = new
            {
                lastReceivedUtc = stagingImports.FirstOrDefault(s => s.Source != null && (s.Source.Contains("Power Automate") || s.Source.Contains("Mailbox")))?.ReceivedAtUtc,
                pendingReview = stagingImports.Count(s => s.Status == StagingStatus.PendingReview),
            }
        });
    }

    /// <summary>
    /// Exceptions board — surfaces operational issues that need attention.
    /// Reads from cached tables; does not call live providers.
    /// </summary>
    [HttpGet("exceptions")]
    public async Task<IActionResult> Exceptions([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var exceptions = new List<ExceptionRecord>();

        // Late/at-risk ETAs
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(l => l.Stops)
                .Where(l => l.PlanningDate == planningDate && l.Status != LoadStatus.Cancelled)
                .OrderBy(l => l.Reference).Take(200).ToListAsync(ct);
        }
        catch (Exception ex) when (IsSchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
        }

        var orderIds = loads.SelectMany(l => l.Stops).Where(s => s.OrderId != null).Select(s => s.OrderId!.Value).Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(o => orderIds.Contains(o.Id)), o => o.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0)
            orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(o => orderIds.Contains(o.Id)).ToDictionary(o => o.Id);

        foreach (var load in loads)
        {
            // Unallocated loads
            if (load.DriverId is null || load.VehicleId is null)
            {
                exceptions.Add(new ExceptionRecord("UnallocatedLoad", "High", load.Reference, $"Load {load.Reference} is missing {(load.DriverId is null ? "driver" : "")}{(load.DriverId is null && load.VehicleId is null ? " and " : "")}{(load.VehicleId is null ? "vehicle" : "")}.", load.Id));
            }

            // Missing geocodes
            var stopsWithoutGeo = load.Stops.Where(s => s.Latitude is null || s.Longitude is null).ToList();
            if (stopsWithoutGeo.Count > 0)
            {
                exceptions.Add(new ExceptionRecord("MissingGeocode", "Medium", load.Reference, $"Load {load.Reference} has {stopsWithoutGeo.Count} stop(s) without coordinates.", load.Id));
            }

            // Late or at-risk delivery windows
            foreach (var stop in load.Stops.OrderBy(s => s.Sequence))
            {
                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                if (order?.DeliveryWindowEndUtc is DateTimeOffset windowEnd && stop.PlannedArrivalUtc is DateTimeOffset planned)
                {
                    if (planned > windowEnd)
                        exceptions.Add(new ExceptionRecord("LateEta", "High", load.Reference, $"Stop {stop.Sequence} ({stop.Name}) on load {load.Reference} is late — planned {planned:HH:mm} vs window end {windowEnd:HH:mm}.", load.Id));
                    else if (windowEnd - planned <= TimeSpan.FromMinutes(30))
                        exceptions.Add(new ExceptionRecord("AtRiskEta", "Medium", load.Reference, $"Stop {stop.Sequence} ({stop.Name}) on load {load.Reference} is at risk — planned {planned:HH:mm} vs window end {windowEnd:HH:mm}.", load.Id));
                }
            }
        }

        // Stale tracking
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        var staleTracking = liveStatuses.Where(s => now - s.LastEventTimeUtc > TimeSpan.FromMinutes(30)).ToList();
        foreach (var status in staleTracking)
        {
            exceptions.Add(new ExceptionRecord("StaleTelemetry", "Medium", status.VehicleIdentifier, $"No tracking update for {status.VehicleIdentifier} since {status.LastEventTimeUtc:HH:mm}.", null));
        }

        // Staging issues
        var stagingIssues = await SafeList(db.StagedImports.AsNoTracking().Where(s => s.Status == StagingStatus.Rejected || s.Status == StagingStatus.Failed).OrderByDescending(s => s.ReceivedAtUtc).Take(20), ct);
        foreach (var item in stagingIssues)
        {
            exceptions.Add(new ExceptionRecord("ImportIssue", "Medium", item.IdempotencyKey, $"{item.EntityType} import {item.IdempotencyKey} was {item.Status}. {item.ReviewNote ?? ""}", null));
        }

        return Ok(new
        {
            planningDate,
            generatedAtUtc = now,
            summary = new
            {
                total = exceptions.Count,
                high = exceptions.Count(e => e.Severity == "High"),
                medium = exceptions.Count(e => e.Severity == "Medium"),
                low = exceptions.Count(e => e.Severity == "Low"),
            },
            byType = exceptions.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()),
            exceptions
        });
    }

    /// <summary>
    /// Daily reconciliation — compares expected vs actual counts across the system.
    /// </summary>
    [HttpGet("reconciliation")]
    public async Task<IActionResult> Reconciliation([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;

        var ordersTotal = await SafeCount(db.TransportOrders.Where(o => o.CollectionDate == planningDate), ct);
        var ordersReadyToPlan = await SafeCount(db.TransportOrders.Where(o => o.CollectionDate == planningDate && o.Status == OrderStatus.ReadyToPlan), ct);
        var ordersPlanned = await SafeCount(db.TransportOrders.Where(o => o.CollectionDate == planningDate && o.Status == OrderStatus.Planned), ct);
        var ordersInTransit = await SafeCount(db.TransportOrders.Where(o => o.CollectionDate == planningDate && o.Status == OrderStatus.InTransit), ct);
        var ordersDelivered = await SafeCount(db.TransportOrders.Where(o => o.CollectionDate == planningDate && o.Status == OrderStatus.Delivered), ct);

        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Where(l => l.PlanningDate == planningDate && l.Status != LoadStatus.Cancelled).ToListAsync(ct);
        }
        catch (Exception ex) when (IsSchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct)).Where(l => l.Status != LoadStatus.Cancelled).ToList();
        }

        var loadsPlanned = loads.Count(l => l.Status == LoadStatus.Planned || l.Status == LoadStatus.Draft);
        var loadsDispatched = loads.Count(l => l.Status == LoadStatus.Dispatched || l.Status == LoadStatus.InProgress);
        var loadsCompleted = loads.Count(l => l.Status == LoadStatus.Completed);
        var unallocatedLoads = loads.Count(l => l.DriverId is null || l.VehicleId is null);

        var activeDrivers = await SafeCount(db.Drivers.Where(d => d.Active), ct);
        var activeVehicles = await SafeCount(db.Vehicles.Where(v => v.Active), ct);
        var assignedDrivers = loads.Where(l => l.DriverId != null).Select(l => l.DriverId!.Value).Distinct().Count();
        var assignedVehicles = loads.Where(l => l.VehicleId != null).Select(l => l.VehicleId!.Value).Distinct().Count();

        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);
        var vehiclesSeenToday = liveStatuses.Count(s => s.LastEventTimeUtc.Date == now.Date);

        var stagingPending = await SafeCount(db.StagedImports.Where(s => s.Status == StagingStatus.PendingReview), ct);

        return Ok(new
        {
            planningDate,
            generatedAtUtc = now,
            orders = new
            {
                total = ordersTotal,
                readyToPlan = ordersReadyToPlan,
                planned = ordersPlanned,
                inTransit = ordersInTransit,
                delivered = ordersDelivered,
            },
            loads = new
            {
                total = loads.Count,
                planned = loadsPlanned,
                dispatched = loadsDispatched,
                completed = loadsCompleted,
                unallocated = unallocatedLoads,
            },
            fleet = new
            {
                activeDrivers,
                assignedDrivers,
                unassignedDrivers = Math.Max(0, activeDrivers - assignedDrivers),
                activeVehicles,
                assignedVehicles,
                vehiclesSeenToday,
                vehiclesNoSignal = Math.Max(0, activeVehicles - vehiclesSeenToday),
            },
            staging = new
            {
                pendingReview = stagingPending,
            }
        });
    }

    // --- Manual integration mappings ---

    [HttpGet("mappings")]
    public async Task<IActionResult> GetMappings([FromQuery] string? provider, CancellationToken ct)
    {
        var query = db.IntegrationMappings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(provider))
            query = query.Where(m => m.Provider == provider);
        var mappings = await query.OrderByDescending(m => m.UpdatedAtUtc).ToListAsync(ct);
        return Ok(mappings);
    }

    [HttpPost("mappings"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CreateMapping([FromBody] CreateMappingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.ExternalKey) || string.IsNullOrWhiteSpace(request.TmsEntityType))
            return BadRequest(new { message = "Provider, ExternalKey, and TmsEntityType are required." });

        var mapping = new IntegrationMapping
        {
            Provider = request.Provider.Trim(),
            ExternalKey = request.ExternalKey.Trim(),
            ExternalLabel = string.IsNullOrWhiteSpace(request.ExternalLabel) ? null : request.ExternalLabel.Trim(),
            TmsEntityType = request.TmsEntityType.Trim(),
            TmsEntityId = request.TmsEntityId,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            UpdatedBy = User.Identity?.Name,
        };
        db.IntegrationMappings.Add(mapping);
        await db.SaveChangesAsync(ct);
        return Ok(mapping);
    }

    [HttpDelete("mappings/{id}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> DeleteMapping(Guid id, CancellationToken ct)
    {
        var mapping = await db.IntegrationMappings.FindAsync([id], ct);
        if (mapping is null) return NotFound();
        mapping.Active = false;
        mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
        mapping.UpdatedBy = User.Identity?.Name;
        await db.SaveChangesAsync(ct);
        return Ok(new { deleted = true });
    }

    // --- Driver status feedback (phase 1: dispatcher-captured) ---

    [HttpPost("loads/{loadId}/driver-status"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CaptureDriverStatus(Guid loadId, [FromBody] CaptureStatusRequest request, CancellationToken ct)
    {
        var validStatuses = new[] { "Dispatched", "Accepted", "ArrivedCollection", "Loaded", "ArrivedDelivery", "Delivered", "IssueReported" };
        if (string.IsNullOrWhiteSpace(request.Status) || !validStatuses.Contains(request.Status))
            return BadRequest(new { message = $"Status must be one of: {string.Join(", ", validStatuses)}" });

        var log = new DriverStatusLog
        {
            LoadId = loadId,
            DriverId = request.DriverId,
            Status = request.Status,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CapturedBy = User.Identity?.Name,
        };
        db.DriverStatusLogs.Add(log);

        // Update load status to InProgress if status indicates movement
        if (request.Status is "ArrivedCollection" or "Loaded" or "ArrivedDelivery")
        {
            var load = await db.Loads.FindAsync([loadId], ct);
            if (load is not null && load.Status == LoadStatus.Dispatched)
                load.Status = LoadStatus.InProgress;
        }
        if (request.Status == "Delivered")
        {
            var load = await db.Loads.FindAsync([loadId], ct);
            if (load is not null && load.Status != LoadStatus.Completed)
                load.Status = LoadStatus.Completed;
        }

        await db.SaveChangesAsync(ct);
        return Ok(log);
    }

    [HttpGet("loads/{loadId}/driver-status")]
    public async Task<IActionResult> GetDriverStatus(Guid loadId, CancellationToken ct)
    {
        var logs = await db.DriverStatusLogs.AsNoTracking()
            .Where(l => l.LoadId == loadId)
            .OrderByDescending(l => l.CapturedAtUtc)
            .ToListAsync(ct);
        return Ok(logs);
    }

    // --- Helpers ---

    private static async Task<int> SafeCount<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.CountAsync(ct); }
        catch (Exception ex) when (IsSchemaUnavailable(ex)) { return 0; }
    }

    private static async Task<List<T>> SafeList<T>(IQueryable<T> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception ex) when (IsSchemaUnavailable(ex)) { return []; }
    }

    private static async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(IQueryable<T> query, Func<T, TKey> keySelector, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch (Exception ex) when (IsSchemaUnavailable(ex)) { return []; }
    }

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
}

public sealed record ExceptionRecord(string Type, string Severity, string Reference, string Description, Guid? LoadId);
public sealed record CreateMappingRequest(string Provider, string ExternalKey, string? ExternalLabel, string TmsEntityType, Guid TmsEntityId, string? Notes);
public sealed record CaptureStatusRequest(string Status, Guid? DriverId, string? Notes);
