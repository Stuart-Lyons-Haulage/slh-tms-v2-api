using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1")]
[Authorize]
public sealed class PlanningController(TmsDbContext db, AzureMapsRouteClient maps, DriverSmsDispatchService sms, IConfiguration configuration, MasterAssignmentComplianceService compliance) : ControllerBase
{
    [HttpGet("orders")]
    public async Task<IActionResult> Orders([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct)
    {
        try
        {
            var query = db.TransportOrders.AsNoTracking().Where(order => order.Status != OrderStatus.Cancelled).AsQueryable();
            if (from is not null) query = query.Where(order => order.CollectionDate >= from);
            if (to is not null) query = query.Where(order => order.CollectionDate <= to);
            var primary = await query.OrderBy(order => order.CollectionDate).ThenBy(order => order.Reference).Take(1000).ToListAsync(ct);
            var registered = await PlanningRegisterStore.ReadOrdersAsync(db, from, to, ct);
            foreach (var order in registered.Where(order => primary.All(existing => !string.Equals(existing.Reference, order.Reference, StringComparison.OrdinalIgnoreCase))))
                primary.Add(order);
            return Ok(primary.OrderBy(order => order.CollectionDate).ThenBy(order => order.Reference).Take(1000).ToList());
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            return Ok(await PlanningRegisterStore.ReadOrdersAsync(db, from, to, ct));
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            return Ok(await PlanningRegisterStore.ReadOrdersAsync(db, from, to, ct));
        }
    }

    [HttpGet("loads"), AllowAnonymous]
    public async Task<IActionResult> Loads([FromQuery] DateOnly? date, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var loads = await PlanningResilience.ReadLoadsAsync(db, date, ct);
        try
        {
            await LoadCommercialStore.EnrichAsync(db, loads, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
        }
        return Ok(loads);
    }

    [HttpPost("loads"), HttpPost("runs"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CreateLoad(CreateLoadRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Reference) || request.Stops.Count == 0) return BadRequest("A reference and at least one stop are required.");
        if (request.PalletSpacesUsed < 0 || request.TotalPalletSpaces < 0) return BadRequest("Capacity values cannot be negative.");
        var load = new Load
        {
            Reference = request.Reference.Trim(),
            PlanningDate = request.PlanningDate,
            VehicleId = request.VehicleId,
            DriverId = request.DriverId,
            TrailerId = request.TrailerId,
            Status = LoadStatus.Draft,
            PalletSpacesUsed = request.PalletSpacesUsed,
            TotalPalletSpaces = request.TotalPalletSpaces,
            CapacityType = Clip(request.CapacityType, 40) ?? "Standard pallets",
            DepotSplits = Clip(request.DepotSplits, 1000),
            TemperatureC = request.TemperatureC,
            PlannerNotes = Clip(request.PlannerNotes, 1000),
            Stops = request.Stops.Select((stop, index) => new LoadStop
            {
                OrderId = stop.OrderId,
                Sequence = index + 1,
                Name = stop.Name.Trim(),
                Address = stop.Address,
                Latitude = stop.Latitude,
                Longitude = stop.Longitude,
                PlannedArrivalUtc = stop.PlannedArrivalUtc
            }).ToList()
        };

        try
        {
            if (await db.Loads.AnyAsync(item => item.Reference == request.Reference, ct)) return Conflict("A load with this reference already exists.");
            if ((await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Any(item => string.Equals(item.Reference, request.Reference, StringComparison.OrdinalIgnoreCase))) return Conflict("A load with this reference already exists.");
            db.Loads.Add(load);
            await db.SaveChangesAsync(ct);
            if (load.PalletSpacesUsed is not null || load.TotalPalletSpaces is not null || load.DepotSplits is not null || load.TemperatureC is not null || load.PlannerNotes is not null)
                await LoadCommercialStore.SaveAsync(db, load, Values(load), User.Identity?.Name, ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            if ((await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Any(item => string.Equals(item.Reference, request.Reference, StringComparison.OrdinalIgnoreCase))) return Conflict("A load with this reference already exists.");
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        }
        return Created($"/api/v1/loads/{load.Id}", load);
    }

    [HttpPut("loads/{id:guid}/allocation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Allocate(Guid id, UpdateLoadAllocationRequest request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: false, asTracking: true, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        if (request.VehicleId is not null && !await db.Vehicles.AnyAsync(vehicle => vehicle.Id == request.VehicleId && vehicle.Active, ct)) return BadRequest("Vehicle is not active.");
        if (request.DriverId is not null && !await db.Drivers.AnyAsync(driver => driver.Id == request.DriverId && driver.Active, ct)) return BadRequest("Driver is not active.");
        if (request.TrailerId is not null && !await db.Trailers.AnyAsync(trailer => trailer.Id == request.TrailerId && trailer.Active, ct)) return BadRequest("Trailer is not active.");
        var masterCompliance = await compliance.CheckAsync(request.DriverId, request.VehicleId, ct);
        if (!masterCompliance.Allowed) return Conflict(new { code = "master_compliance_blocked", errors = masterCompliance.Errors, warnings = masterCompliance.Warnings });

        load.VehicleId = request.VehicleId;
        load.DriverId = request.DriverId;
        load.TrailerId = request.TrailerId;
        if (request.TrailerId is not null)
        {
            var trailer = await db.Trailers.AsNoTracking().SingleAsync(item => item.Id == request.TrailerId, ct);
            load.TotalPalletSpaces = string.Equals(load.CapacityType, "Euro pallets", StringComparison.OrdinalIgnoreCase)
                ? trailer.EuroCapacity ?? trailer.StandardCapacity
                : trailer.StandardCapacity ?? trailer.EuroCapacity;
        }
        load.Status = request.VehicleId is not null && request.DriverId is not null ? LoadStatus.Planned : LoadStatus.Draft;
        await SaveLoadAsync(load, register, ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/commercial"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateCommercial(Guid id, UpdateLoadCommercialRequest request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: false, asTracking: true, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        if (new decimal?[] { request.RevenueAmount, request.FuelSurchargeAmount, request.EstimatedCostAmount, request.ActualCostAmount, request.EstimatedDistanceMiles, request.EmptyMiles }.Any(value => value < 0))
            return BadRequest("Commercial values cannot be negative.");
        var values = new LoadCommercialValues(request.RevenueAmount, request.FuelSurchargeAmount, request.EstimatedCostAmount, request.ActualCostAmount,
            request.EstimatedDistanceMiles, request.EmptyMiles, Clip(request.InvoiceStatus, 40), Clip(request.CommercialNotes, 500), load.PalletSpacesUsed,
            load.TotalPalletSpaces, load.CapacityType, load.DepotSplits, load.TemperatureC, load.PlannerNotes);
        load.RevenueAmount = values.RevenueAmount;
        load.FuelSurchargeAmount = values.FuelSurchargeAmount;
        load.EstimatedCostAmount = values.EstimatedCostAmount;
        load.ActualCostAmount = values.ActualCostAmount;
        load.EstimatedDistanceMiles = values.EstimatedDistanceMiles;
        load.EmptyMiles = values.EmptyMiles;
        load.InvoiceStatus = values.InvoiceStatus;
        load.CommercialNotes = values.CommercialNotes;
        if (register) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        else await LoadCommercialStore.SaveAsync(db, load, values, User.Identity?.Name, ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/utilisation"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateUtilisation(Guid id, UpdateLoadUtilisationRequest request, CancellationToken ct)
    {
        if (request.PalletSpacesUsed < 0 || request.TotalPalletSpaces < 0) return BadRequest("Capacity values cannot be negative.");
        var (load, register) = await FindLoadAsync(id, includeStops: false, asTracking: true, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        load.PalletSpacesUsed = request.PalletSpacesUsed;
        load.TotalPalletSpaces = request.TotalPalletSpaces;
        load.CapacityType = Clip(request.CapacityType, 40) ?? "Standard pallets";
        load.DepotSplits = Clip(request.DepotSplits, 1000);
        load.TemperatureC = request.TemperatureC;
        load.PlannerNotes = Clip(request.PlannerNotes, 1000);
        await SaveLoadAsync(load, register, ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/status"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStatus(Guid id, UpdateLoadStatusRequest request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: true, asTracking: true, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        if (!Enum.TryParse<LoadStatus>(request.Status, true, out var next)) return BadRequest("The requested load status is not valid.");
        if (!CanTransition(load.Status, next)) return BadRequest($"A load cannot move from {load.Status} to {next}.");
        if ((next is LoadStatus.Dispatched or LoadStatus.InProgress) && (load.DriverId is null || load.VehicleId is null)) return BadRequest("Allocate both a driver and vehicle before dispatching a load.");

        load.Status = next;
        if (!register)
        {
            var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).ToList();
            if (orderIds.Count > 0)
            {
                try
                {
                    var orders = await db.TransportOrders.Where(order => orderIds.Contains(order.Id)).ToListAsync(ct);
                    foreach (var order in orders)
                    {
                        if (next is LoadStatus.Planned or LoadStatus.Dispatched) order.Status = OrderStatus.Planned;
                        else if (next == LoadStatus.InProgress) order.Status = OrderStatus.InTransit;
                        else if (next == LoadStatus.Completed) order.Status = OrderStatus.Delivered;
                        else if (next == LoadStatus.Cancelled) order.Status = OrderStatus.Cancelled;
                    }
                }
                catch (Exception exception) when (IsSchemaUnavailable(exception))
                {
                    db.ChangeTracker.Clear();
                    register = true;
                }
            }
        }
        await SaveLoadAsync(load, register, ct);
        return Ok(load);
    }

    [HttpPut("loads/{id:guid}/stops"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> UpdateStops(Guid id, List<UpdateLoadStopRequest> request, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: true, asTracking: true, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        if (request.Count == 0 || request.Any(stop => string.IsNullOrWhiteSpace(stop.Name))) return BadRequest("At least one named stop is required.");
        if (!register) db.LoadStops.RemoveRange(load.Stops);
        load.Stops = request.Select((stop, index) => new LoadStop
        {
            OrderId = stop.OrderId,
            Sequence = index + 1,
            Name = stop.Name.Trim(),
            Address = stop.Address,
            Latitude = stop.Latitude,
            Longitude = stop.Longitude,
            PlannedArrivalUtc = stop.PlannedArrivalUtc,
            PlannerNote = Clip(stop.PlannerNote, 1000)
        }).ToList();
        await SaveLoadAsync(load, register, ct);
        return Ok(load);
    }

    [HttpGet("loads/{id:guid}/route")]
    public async Task<IActionResult> Route(Guid id, CancellationToken ct)
    {
        var (load, _) = await FindLoadAsync(id, includeStops: true, asTracking: false, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        var points = load.Stops.Where(stop => stop.Longitude is not null && stop.Latitude is not null).OrderBy(stop => stop.Sequence)
            .Select(stop => (stop.Longitude!.Value, stop.Latitude!.Value)).ToList();
        return Ok(await maps.Directions(points, ct));
    }

    [HttpGet("loads/{id:guid}/dispatch")]
    public async Task<IActionResult> Dispatch(Guid id, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: true, asTracking: false, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        var orders = await LoadOrdersAsync(load, register, ct);
        var temperature = await SitePlanningProfileStore.ResolveRunTemperaturesAsync(db, orders.Values, ct);
        var driver = load.DriverId is null ? null : await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        var vehicle = load.VehicleId is null ? null : await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        var trailer = load.TrailerId is null ? null : await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.TrailerId, ct);
        return Ok(new
        {
            load.Id,
            load.Reference,
            load.PlanningDate,
            load.Status,
            loadTemperatureC = temperature.LoadTemperatureC,
            temperatureConflict = temperature.HasConflict,
            temperatures = temperature.DistinctTemperatures.Select(SitePlanningProfileStore.FormatTemperature).ToList(),
            driver = driver is null ? null : new { driver.DisplayName, driver.EmployeeNumber, driver.MobileNumber },
            vehicle = vehicle is null ? null : new { vehicle.Registration, vehicle.FleetNumber },
            trailer = trailer is null ? null : new { trailer.TrailerNumber, trailer.Type },
            stops = load.Stops.OrderBy(stop => stop.Sequence).Select(stop => new
            {
                stop.Id,
                stop.Sequence,
                stop.Name,
                stop.Address,
                stop.Latitude,
                stop.Longitude,
                stop.PlannedArrivalUtc,
                order = stop.OrderId is not null && orders.TryGetValue(stop.OrderId.Value, out var order) ? new
                {
                    order.Reference,
                    order.CustomerCode,
                    order.SellerName,
                    order.MarketName,
                    order.StallNumber,
                    order.DriverInstructions,
                    order.MapLink,
                    temperatureC = temperature.OrderTemperatures.GetValueOrDefault(order.Id)
                } : null
            })
        });
    }

    [HttpPost("loads/{id:guid}/dispatch/sms"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SendDispatchSms(Guid id, CancellationToken ct)
    {
        var (load, register) = await FindLoadAsync(id, includeStops: true, asTracking: true, ct);
        if (load is null) return NotFound("The imported run could not be found in live loads or the planning register.");
        if (load.DriverId is null || load.VehicleId is null) return BadRequest("Allocate both a driver and vehicle before sending a dispatch.");
        var driver = await db.Drivers.SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        var vehicle = await db.Vehicles.SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        if (driver is null || vehicle is null) return BadRequest("The allocated driver or vehicle could not be found.");
        if (string.IsNullOrWhiteSpace(driver.MobileNumber)) return BadRequest("The assigned driver has no approved mobile number.");

        var orders = await LoadOrdersAsync(load, register, ct);
        var marketMapLinks = await ResolveMarketMapLinksAsync(orders.Values, ct);
        var temperature = await SitePlanningProfileStore.ResolveRunTemperaturesAsync(db, orders.Values, ct);
        if (temperature.HasConflict)
        {
            var values = temperature.DistinctTemperatures.Select(SitePlanningProfileStore.FormatTemperature).ToList();
            return BadRequest(new { message = $"Temperature conflict on run {load.Reference}: {string.Join(" / ", values)}. Resolve the load temperature before sending the driver text.", temperatures = values });
        }
        var formattedTemperature = temperature.LoadTemperatureC is null ? null : SitePlanningProfileStore.FormatTemperature(temperature.LoadTemperatureC.Value);
        var stops = load.Stops.OrderBy(stop => stop.Sequence).Select(stop =>
        {
            orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
            var orderTemperature = order is not null && temperature.OrderTemperatures.TryGetValue(order.Id, out var value) && value is not null
                ? SitePlanningProfileStore.FormatTemperature(value.Value)
                : null;
            return string.Join("\n", new[]
            {
                $"{stop.Sequence}. {stop.Name}",
                orderTemperature is null ? null : $"Temperature: {orderTemperature}",
                order?.MarketName is null ? null : $"Market: {order.MarketName}{(string.IsNullOrWhiteSpace(order.StallNumber) ? string.Empty : $" · Stall {order.StallNumber}")}",
                order?.SellerName is null ? null : $"Seller: {order.SellerName}",
                string.IsNullOrWhiteSpace(stop.Address) ? null : $"Address: {stop.Address}",
                string.IsNullOrWhiteSpace(order?.DriverInstructions) ? null : $"Notes: {order!.DriverInstructions}",
                string.IsNullOrWhiteSpace(order?.MapLink) ?
                    (order?.MarketName is not null && marketMapLinks.TryGetValue(MarketKey(order.MarketName), out var marketMap) ? $"Market map (read-only PDF): {marketMap}" : null) :
                    $"Map: {order.MapLink}"
            }.Where(line => line is not null));
        });
        var message = string.Join("\n\n", new[]
        {
            $"SLH run {load.Reference}",
            $"Driver: {driver.DisplayName}",
            $"Vehicle: {vehicle.Registration}",
            formattedTemperature is null ? null : $"LOAD TEMPERATURE: {formattedTemperature}\nSet trailer to {formattedTemperature} before collection",
            string.Empty,
            string.Join("\n\n", stops)
        }.Where(line => line is not null));
        var receipt = await sms.SendAsync(driver.MobileNumber, message, ct);
        if (load.Status == LoadStatus.Planned) load.Status = LoadStatus.Dispatched;
        await SaveLoadAsync(load, register, ct);
        return Accepted(new { receipt.MessageId, receipt.MobileSuffix, receipt.Provider, load.Status, loadTemperatureC = temperature.LoadTemperatureC });
    }

    private async Task<Dictionary<string, string>> ResolveMarketMapLinksAsync(IEnumerable<TransportOrder> orders, CancellationToken ct)
    {
        var marketKeys = orders
            .Select(order => order.MarketName)
            .Where(market => !string.IsNullOrWhiteSpace(market))
            .Select(market => MarketKey(market!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (marketKeys.Count == 0) return new(StringComparer.OrdinalIgnoreCase);

        var maps = await db.MarketContacts.AsNoTracking()
            .Where(contact => contact.Active && contact.ReadOnlyMapPdfUrl != null && contact.ReadOnlyMapPdfUrl != "")
            .Select(contact => new { contact.Market, contact.ReadOnlyMapPdfUrl })
            .ToListAsync(ct);
        return maps
            .Where(contact => marketKeys.Contains(MarketKey(contact.Market)))
            .GroupBy(contact => MarketKey(contact.Market), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(contact => contact.ReadOnlyMapPdfUrl!).First(), StringComparer.OrdinalIgnoreCase);
    }

    private static string MarketKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    [HttpGet("maps/geocode")]
    public async Task<IActionResult> Geocode([FromQuery] string address, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(address)) return BadRequest("An address is required.");
        return Ok(await maps.SearchAddress(address, ct));
    }

    private async Task<(Load? Load, bool Register)> FindLoadAsync(Guid id, bool includeStops, bool asTracking, CancellationToken ct)
    {
        try
        {
            IQueryable<Load> query = asTracking ? db.Loads : db.Loads.AsNoTracking();
            if (includeStops) query = query.Include(load => load.Stops);
            var load = await query.SingleOrDefaultAsync(item => item.Id == id, ct);
            if (load is not null)
            {
                await LoadCommercialStore.EnrichAsync(db, [load], ct);
                return (load, false);
            }
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
        }

        var registered = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
        if (registered is not null) await LoadCommercialStore.EnrichAsync(db, [registered], ct);
        return (registered, registered is not null);
    }

    private async Task<Dictionary<Guid, TransportOrder>> LoadOrdersAsync(Load load, bool register, CancellationToken ct)
    {
        var orderIds = load.Stops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        if (orderIds.Count == 0) return [];
        if (!register)
        {
            try
            {
                return await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);
            }
            catch (Exception exception) when (IsSchemaUnavailable(exception))
            {
                db.ChangeTracker.Clear();
            }
        }
        return (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);
    }

    private async Task SaveLoadAsync(Load load, bool register, CancellationToken ct)
    {
        if (register)
        {
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            return;
        }

        try
        {
            await LoadCommercialStore.SaveAsync(db, load, Values(load), User.Identity?.Name, ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
        }
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanTransition(LoadStatus current, LoadStatus next) => current == next || (current, next) switch
    {
        (LoadStatus.Draft, LoadStatus.Planned) => true,
        (LoadStatus.Draft, LoadStatus.Cancelled) => true,
        (LoadStatus.Planned, LoadStatus.Draft) => true,
        (LoadStatus.Planned, LoadStatus.Dispatched) => true,
        (LoadStatus.Planned, LoadStatus.Cancelled) => true,
        (LoadStatus.Dispatched, LoadStatus.InProgress) => true,
        (LoadStatus.Dispatched, LoadStatus.Cancelled) => true,
        (LoadStatus.InProgress, LoadStatus.Completed) => true,
        _ => false
    };

    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];
    private static LoadCommercialValues Values(Load load) => new(load.RevenueAmount, load.FuelSurchargeAmount, load.EstimatedCostAmount, load.ActualCostAmount,
        load.EstimatedDistanceMiles, load.EmptyMiles, load.InvoiceStatus, load.CommercialNotes, load.PalletSpacesUsed, load.TotalPalletSpaces,
        load.CapacityType, load.DepotSplits, load.TemperatureC, load.PlannerNotes);
}

public sealed record CreateLoadRequest(string Reference, DateOnly PlanningDate, Guid? VehicleId, Guid? DriverId, Guid? TrailerId, List<CreateLoadStopRequest> Stops,
    decimal? PalletSpacesUsed = null, decimal? TotalPalletSpaces = null, string? CapacityType = null, string? DepotSplits = null, decimal? TemperatureC = null, string? PlannerNotes = null);
public sealed record CreateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc);
public sealed record UpdateLoadAllocationRequest(Guid? VehicleId, Guid? DriverId, Guid? TrailerId);
public sealed record UpdateLoadStatusRequest(string Status);
public sealed record UpdateLoadStopRequest(Guid? OrderId, string Name, string? Address, decimal? Latitude, decimal? Longitude, DateTimeOffset? PlannedArrivalUtc, string? PlannerNote = null);
public sealed record UpdateLoadCommercialRequest(decimal? RevenueAmount, decimal? FuelSurchargeAmount, decimal? EstimatedCostAmount, decimal? ActualCostAmount, decimal? EstimatedDistanceMiles, decimal? EmptyMiles, string? InvoiceStatus, string? CommercialNotes);
public sealed record UpdateLoadUtilisationRequest(decimal? PalletSpacesUsed, decimal? TotalPalletSpaces, string? CapacityType, string? DepotSplits, decimal? TemperatureC, string? PlannerNotes);
