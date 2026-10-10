using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
 
namespace Slh.Tms.Api.Controllers;
 
[ApiController, Route("api/v1/operations")]
[Authorize]
public sealed class OperationsController(
    TmsDbContext db) : ControllerBase
{
    [HttpGet("forecast")]
    public async Task<IActionResult> Forecast([FromQuery] DateOnly? from, CancellationToken ct)
    {
        var firstDate = from ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var lastDate = firstDate.AddDays(6);
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).Take(2000).ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, null, ct)).Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate && load.Status != LoadStatus.Cancelled).ToList();
        }
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        var orderIds = loads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = await SafeDictionary(db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)), order => order.Id, ct);
        if (orders.Count == 0 && orderIds.Count > 0) orders = (await PlanningRegisterStore.ReadOrdersAsync(db, null, null, ct)).Where(order => orderIds.Contains(order.Id)).ToDictionary(order => order.Id);
        var trailers = await SafeDictionary(db.Trailers.AsNoTracking().Where(trailer => trailer.Active), trailer => trailer.Id, ct);
        var activeDrivers = await db.Drivers.AsNoTracking().CountAsync(driver => driver.Active, ct);
        var activeVehicles = await db.Vehicles.AsNoTracking().CountAsync(vehicle => vehicle.Active, ct);
        var activeTrailerCapacity = trailers.Values.Sum(trailer => trailer.StandardCapacity ?? 0);
        var days = Enumerable.Range(0, 7).Select(offset =>
        {
            var date = firstDate.AddDays(offset);
            var dayLoads = loads.Where(load => load.PlanningDate == date).ToList();
            var dayOrderIds = dayLoads.SelectMany(load => load.Stops).Where(stop => stop.OrderId != null).Select(stop => stop.OrderId!.Value).Distinct();
            var pallets = (int)Math.Ceiling(dayLoads.Sum(load => load.PalletSpacesUsed ?? 0));
            if (pallets == 0) pallets = dayOrderIds.Sum(id => orders.TryGetValue(id, out var order) ? order.Pallets ?? 0 : 0);
            var plannedCapacity = (int)Math.Ceiling(dayLoads.Sum(load => load.TotalPalletSpaces ?? 0));
            var utilisation = plannedCapacity > 0 ? Math.Round((decimal)pallets / plannedCapacity * 100, 1) : (decimal?)null;
            var overCapacityLoads = dayLoads.Count(load => load.TotalPalletSpaces > 0 && load.PalletSpacesUsed > load.TotalPalletSpaces);
            var emptyMiles = dayLoads.Sum(load => load.EmptyMiles ?? 0);
            var assignedDrivers = dayLoads.Where(load => load.DriverId != null).Select(load => load.DriverId).Distinct().Count();
            var assignedVehicles = dayLoads.Where(load => load.VehicleId != null).Select(load => load.VehicleId).Distinct().Count();
            var exceptions = dayLoads.Count(load => load.DriverId is null || load.VehicleId is null || load.Stops.Any(stop => stop.Latitude is null || stop.Longitude is null)
                || load.TotalPalletSpaces > 0 && load.PalletSpacesUsed > load.TotalPalletSpaces);
            return new ForecastDay(date, dayLoads.Count, assignedDrivers, activeDrivers, assignedVehicles, activeVehicles, pallets,
                plannedCapacity > 0 ? plannedCapacity : activeTrailerCapacity, emptyMiles, exceptions, utilisation, overCapacityLoads);
        }).ToList();
        return Ok(new
        {
            from = firstDate,
            to = lastDate,
            generatedAtUtc = DateTimeOffset.UtcNow,
            activeDrivers,
            activeVehicles,
            days,
            totals = new
            {
                loads = days.Sum(day => day.Loads),
                emptyMiles = days.Sum(day => day.EmptyMiles),
                exceptions = days.Sum(day => day.Exceptions),
                plannedPallets = days.Sum(day => day.PlannedPallets),
                availableTrailerPallets = days.Sum(day => day.AvailableTrailerPallets),
                utilisationPercent = days.Sum(day => day.AvailableTrailerPallets) > 0
                    ? Math.Round((decimal)days.Sum(day => day.PlannedPallets) / days.Sum(day => day.AvailableTrailerPallets) * 100, 1) : (decimal?)null,
                overCapacityLoads = days.Sum(day => day.OverCapacityLoads)
            }
        });
    }
 
    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
            message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(IQueryable<T> query, Func<T, TKey> keySelector, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { return []; }
    }
 
    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
 
public sealed record ForecastDay(DateOnly Date, int Loads, int AssignedDrivers, int AvailableDrivers, int AssignedVehicles, int AvailableVehicles, int PlannedPallets, int AvailableTrailerPallets, decimal EmptyMiles, int Exceptions, decimal? UtilisationPercent, int OverCapacityLoads);
