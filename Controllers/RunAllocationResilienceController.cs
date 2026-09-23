using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/runs"), Authorize]
public sealed class RunAllocationResilienceController(TmsDbContext db, AzureMapsRouteClient maps, MasterAssignmentComplianceService compliance) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Runs([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();
        try
        {
            var query = db.Loads.AsNoTracking().Include(x => x.Stops).AsQueryable();
            if (date is not null) query = query.Where(x => x.PlanningDate == date.Value);
            foreach (var load in await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(1000).ToListAsync(ct))
                merged[load.Id] = load;
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
            merged[load.Id] = load;

        var rows = merged.Values.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(1000).ToList();
        foreach (var load in rows) load.Stops = RenumberOperationalStops(load.Stops);
        await RunOperationalStore.EnrichAsync(db, rows, ct);
        return Ok(rows);
    }

    [HttpPut("{id:guid}/allocation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, RunAllocationRequest request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: false, tracking: true, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        if (request.VehicleId is Guid vehicleId && !await db.Vehicles.AsNoTracking().AnyAsync(x => x.Id == vehicleId && x.Active, ct))
            return BadRequest(new { message = "Vehicle is not active." });
        if (request.DriverId is Guid driverId && !await db.Drivers.AsNoTracking().AnyAsync(x => x.Id == driverId && x.Active, ct))
            return BadRequest(new { message = "Driver is not active." });
        if (request.TrailerId is Guid trailerId && !await db.Trailers.AsNoTracking().AnyAsync(x => x.Id == trailerId && x.Active, ct))
            return BadRequest(new { message = "Trailer is not active." });
        var masterCompliance = await compliance.CheckAsync(request.DriverId, request.VehicleId, ct);
        if (!masterCompliance.Allowed)
            return Conflict(new { code = "master_compliance_blocked", errors = masterCompliance.Errors, warnings = masterCompliance.Warnings });

        if (request.VehicleId is Guid selectedVehicleId && IsVehicleUnavailable(await db.Vehicles.AsNoTracking().SingleAsync(x => x.Id == selectedVehicleId, ct)))
            return Conflict(new { code = "vehicle_unavailable", message = "The selected vehicle is VOR, out of service or otherwise unavailable." });

        var plannedStart = request.PlannedStartUtc ?? (await DriverDispatchStateStore.ReadAsync(db, [load.Id], ct)).GetValueOrDefault(load.Id)?.PlannedStartUtc;
        if (request.VehicleId is Guid requestedVehicleId && await HasResourceOverlap(requestedVehicleId, null, load, plannedStart, ct))
            return Conflict(new { code = "vehicle_in_use", message = "The selected vehicle is already committed to an overlapping run. Choose another vehicle or a start time after the previous run ends." });
        if (request.TrailerId is Guid requestedTrailerId && await HasResourceOverlap(null, requestedTrailerId, load, plannedStart, ct))
            return Conflict(new { code = "trailer_in_use", message = "The selected trailer is already committed to an overlapping run. Choose another trailer or a start time after the previous run ends." });

        load.VehicleId = request.VehicleId;
        load.DriverId = request.DriverId;
        load.TrailerId = request.TrailerId;
        load.Status = request.VehicleId is not null && request.DriverId is not null ? LoadStatus.Planned : LoadStatus.Draft;
        await SaveCoreLoadAsync(load, register, ct);
        await RunOperationalStore.EnrichAsync(db, [load], ct);
        return Ok(load);
    }

    private async Task<bool> HasResourceOverlap(Guid? vehicleId, Guid? trailerId, Load target, DateTimeOffset? targetStart, CancellationToken ct)
    {
        if (targetStart is null) return false;
        var candidates = await db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.Id != target.Id && x.PlanningDate <= target.PlanningDate && x.Status != LoadStatus.Cancelled &&
                ((vehicleId != null && x.VehicleId == vehicleId) || (trailerId != null && x.TrailerId == trailerId)))
            .ToListAsync(ct);
        var states = await DriverDispatchStateStore.ReadAsync(db, candidates.Select(x => x.Id), ct);
        return candidates.Any(candidate =>
        {
            var start = states.GetValueOrDefault(candidate.Id)?.PlannedStartUtc ?? candidate.Stops.OrderBy(x => x.Sequence).Select(x => x.PlannedArrivalUtc).FirstOrDefault(x => x is not null);
            var end = candidate.Stops.OrderByDescending(x => x.Sequence).Select(x => x.PlannedArrivalUtc).FirstOrDefault(x => x is not null);
            if (start is null || end is null) return true;
            return targetStart < end && start < targetStart;
        });
    }

    private static bool IsVehicleUnavailable(Vehicle vehicle) =>
        !vehicle.Active || (vehicle.FleetioStatus?.Contains("VOR", StringComparison.OrdinalIgnoreCase) == true) ||
        (vehicle.FleetioStatus?.Contains("out of service", StringComparison.OrdinalIgnoreCase) == true) ||
        (vehicle.FleetioStatus?.Contains("off road", StringComparison.OrdinalIgnoreCase) == true) ||
        (vehicle.FleetioStatus?.Contains("inactive", StringComparison.OrdinalIgnoreCase) == true) ||
        (vehicle.FleetioStatus?.Contains("maintenance", StringComparison.OrdinalIgnoreCase) == true);

    [HttpPut("{id:guid}/operational"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateOperational(Guid id, RunOperationalRequest request, CancellationToken ct)
    {
        if (request.PalletSpacesUsed < 0 || request.TotalPalletSpaces < 0)
            return BadRequest(new { message = "Capacity values cannot be negative." });

        var (load, register) = await FindLoadAsync(id, includeStops: false, tracking: true, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        var values = new RunOperationalValues(
            request.PalletSpacesUsed,
            request.TotalPalletSpaces,
            Clip(request.CapacityType, 40) ?? "Standard pallets",
            Clip(request.DepotSplits, 1000),
            request.TemperatureC,
            Clip(request.PlannerNotes, 1000));

        await RunOperationalStore.SaveAsync(db, load, values, User.Identity?.Name, ct);
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        return Ok(load);
    }

    [HttpPut("{id:guid}/status"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStatus(Guid id, RunStatusRequest request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: true, tracking: true, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });
        if (!Enum.TryParse<LoadStatus>(request.Status, true, out var next)) return BadRequest(new { message = "The requested run status is not valid." });
        if (next == LoadStatus.Dispatched)
            return BadRequest(new { message = "Dispatch must use the controlled driver-message dispatch flow so structural readiness and live TachoMaster checks cannot be bypassed." });
        if (!CanTransition(load.Status, next)) return BadRequest(new { message = $"A run cannot move from {load.Status} to {next}." });
        if (next == LoadStatus.InProgress && (load.DriverId is null || load.VehicleId is null))
            return BadRequest(new { message = "Allocate both a driver and vehicle before starting a dispatched run." });

        load.Status = next;
        await SaveCoreLoadAsync(load, register, ct);
        await RunOperationalStore.EnrichAsync(db, [load], ct);
        return Ok(load);
    }

    [HttpPut("{id:guid}/stops"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStops(Guid id, List<RunStopRequest> request, CancellationToken ct)
    {
        if (request.Count == 0 || request.Any(x => string.IsNullOrWhiteSpace(x.Name)))
            return BadRequest(new { message = "At least one named stop is required." });

        var (load, register) = await FindLoadAsync(id, includeStops: true, tracking: true, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        if (!register) db.LoadStops.RemoveRange(load.Stops);
        var orderedRequest = request
            .Select((stop, index) => new { Stop = stop, Index = index })
            .OrderBy(item => OperationalStopOrdering.Phase(item.Stop.Name))
            .ThenBy(item => item.Index)
            .Select(item => item.Stop)
            .ToList();
        load.Stops = orderedRequest.Select((stop, index) => new LoadStop
        {
            Id = Guid.NewGuid(),
            LoadId = load.Id,
            OrderId = stop.OrderId,
            Sequence = index + 1,
            Name = stop.Name.Trim(),
            Address = Clip(stop.Address, 500),
            Latitude = stop.Latitude,
            Longitude = stop.Longitude,
            PlannedArrivalUtc = stop.PlannedArrivalUtc,
            PlannerNote = Clip(stop.PlannerNote, 1000)
        }).ToList();

        await SaveCoreLoadAsync(load, register, ct);
        await RunOperationalStore.EnrichAsync(db, [load], ct);
        return Ok(load);
    }

    [HttpGet("{id:guid}/route")]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct)
    {
        var (load, _) = await FindLoadAsync(id, includeStops: true, tracking: false, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });
        var points = OperationalStopOrdering.Order(load.Stops)
            .Where(x => x.Longitude is not null && x.Latitude is not null)
            .Select(x => (x.Longitude!.Value, x.Latitude!.Value))
            .ToList();
        if (points.Count < 2) return BadRequest(new { message = "At least two mapped stops are required before calculating a route." });
        return Ok(await maps.Directions(points, ct));
    }

    [HttpGet("{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: true, tracking: false, ct);
        if (load is null) return NotFound(new { message = "The run could not be found." });

        var orders = await LoadOrdersAsync(load, register, ct);
        var driver = load.DriverId is null ? null : await db.Drivers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == load.DriverId, ct);
        var vehicle = load.VehicleId is null ? null : await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(x => x.Id == load.VehicleId, ct);
        var trailer = load.TrailerId is null ? null : await db.Trailers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == load.TrailerId, ct);
        var orderedStops = OperationalStopOrdering.Order(load.Stops);
        var deliveryByOrder = orderedStops
            .Where(stop => stop.OrderId is not null && stop.Name.StartsWith("Deliver", StringComparison.OrdinalIgnoreCase))
            .GroupBy(stop => stop.OrderId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(stop => stop.Sequence).Last().Name);

        return Ok(new
        {
            load.Id,
            load.Reference,
            load.PlanningDate,
            load.Status,
            driver = driver is null ? null : new { driver.DisplayName, driver.EmployeeNumber, driver.MobileNumber },
            vehicle = vehicle is null ? null : new
            {
                vehicle.Registration,
                vehicle.FleetNumber,
                vehicle.FuelProvider,
                vehicle.FuelPin,
                vehicle.ShellCard,
                vehicle.BpRedCard,
                vehicle.BpPlainCard,
                vehicle.FuelCardLastFour
            },
            trailer = trailer is null ? null : new { trailer.TrailerNumber, trailer.Type },
            stops = orderedStops.Select((stop, index) => new
            {
                stop.Id,
                sequence = index + 1,
                stop.Name,
                stop.Address,
                stop.Latitude,
                stop.Longitude,
                stop.PlannedArrivalUtc,
                order = stop.OrderId is Guid orderId && orders.TryGetValue(orderId, out var order) ? new
                {
                    order.Reference,
                    order.CustomerCode,
                    order.Pallets,
                    order.CollectionDate,
                    order.DeliveryDate,
                    order.SellerName,
                    order.MarketName,
                    order.StallNumber,
                    order.DriverInstructions,
                    order.MapLink,
                    deliveryName = deliveryByOrder.GetValueOrDefault(orderId)
                } : null
            })
        });
    }

    private async Task<Dictionary<Guid, TransportOrder>> LoadOrdersAsync(Load load, bool register, CancellationToken ct)
    {
        var ids = load.Stops.Where(x => x.OrderId is not null).Select(x => x.OrderId!.Value).Distinct().ToList();
        if (ids.Count == 0) return [];
        if (!register)
        {
            try
            {
                return await db.TransportOrders.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
            }
            catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
            }
        }
        return (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct))
            .Where(x => ids.Contains(x.Id)).ToDictionary(x => x.Id);
    }

    private async Task<(Load? Load, bool Register)> FindLoadAsync(Guid id, bool includeStops, bool tracking, CancellationToken ct)
    {
        var registered = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
        if (registered is not null)
        {
            await RunOperationalStore.EnrichAsync(db, [registered], ct);
            return (registered, true);
        }

        try
        {
            IQueryable<Load> query = tracking ? db.Loads : db.Loads.AsNoTracking();
            if (includeStops) query = query.Include(x => x.Stops);
            var load = await query.SingleOrDefaultAsync(x => x.Id == id, ct);
            if (load is not null)
            {
                await RunOperationalStore.EnrichAsync(db, [load], ct);
                return (load, false);
            }
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        return (null, false);
    }

    private async Task SaveCoreLoadAsync(Load load, bool register, CancellationToken ct)
    {
        if (register)
        {
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            return;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (PlanningResilience.SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        }
    }

    private static List<LoadStop> RenumberOperationalStops(IEnumerable<LoadStop>? stops) => OperationalStopOrdering.Order(stops)
        .Select((stop, index) =>
        {
            stop.Sequence = index + 1;
            return stop;
        })
        .ToList();

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static bool CanTransition(LoadStatus current, LoadStatus next) => current == next || (current, next) switch
    {
        (LoadStatus.Draft, LoadStatus.Planned) => true,
        (LoadStatus.Planned, LoadStatus.Draft) => true,
        (LoadStatus.Dispatched, LoadStatus.InProgress) => true,
        (LoadStatus.Dispatched, LoadStatus.Cancelled) => true,
        (LoadStatus.InProgress, LoadStatus.Completed) => true,
        (LoadStatus.InProgress, LoadStatus.Cancelled) => true,
        _ => false
    };
}

public sealed record RunAllocationRequest(Guid? VehicleId, Guid? DriverId, Guid? TrailerId, DateTimeOffset? PlannedStartUtc = null);
public sealed record RunOperationalRequest(decimal? PalletSpacesUsed, decimal? TotalPalletSpaces, string? CapacityType, string? DepotSplits, decimal? TemperatureC, string? PlannerNotes);
public sealed record RunStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc, string? PlannerNote);
public sealed record RunStatusRequest(string Status);
