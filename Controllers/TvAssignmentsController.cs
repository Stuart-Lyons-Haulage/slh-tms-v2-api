using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Compact read-only assignment projection for the office TV. It intentionally accepts the
/// SQL-backed paired key from either the normal header or the TV URL query string because some
/// embedded/older television browsers do not reliably forward custom XHR headers through the
/// portal proxy.
/// </summary>
[ApiController]
[Route("api/v1/tv-display/assignments")]
public sealed class TvAssignmentsController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        var suppliedPairedKey = !string.IsNullOrWhiteSpace(displayKey)
            ? displayKey
            : Request.Query.TryGetValue("key", out var queryKey) ? queryKey.FirstOrDefault() : null;
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, suppliedPairedKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This TV display is not authorised." });

        var day = date ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.PlanningDate == day && load.Status != LoadStatus.Cancelled)
            .ToList();
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        WallboardPhysicalStops.Apply(loads);

        var driverIds = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(load => load.TrailerId is not null).Select(load => load.TrailerId!.Value).Distinct().ToList();

        var drivers = await SafeDictionary(db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)), driver => driver.Id, ct);
        var vehicles = await SafeDictionary(db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)), vehicle => vehicle.Id, ct);
        var trailers = await SafeDictionary(db.Trailers.AsNoTracking().Where(trailer => trailerIds.Contains(trailer.Id)), trailer => trailer.Id, ct);

        var records = loads
            .OrderBy(load => load.Stops.Where(stop => stop.PlannedArrivalUtc is not null)
                .Select(stop => stop.PlannedArrivalUtc)
                .Min() ?? DateTimeOffset.MaxValue)
            .ThenBy(load => load.Reference)
            .Select(load =>
            {
                var finalStop = load.Stops.OrderBy(stop => stop.Sequence).LastOrDefault();
                return new DriverAssignmentResponse(
                    load.Id,
                    load.PlanningDate,
                    RunDisplayLabel.For(load),
                    load.Status.ToString(),
                    load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver)
                        ? new AssignmentDriver(driver.Id, driver.DisplayName, driver.EmployeeNumber)
                        : null,
                    load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle)
                        ? new AssignmentVehicle(vehicle.Id, vehicle.Registration, vehicle.FleetNumber)
                        : null,
                    load.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailer)
                        ? trailer.TrailerNumber
                        : null,
                    load.Stops.Count,
                    finalStop?.Name,
                    finalStop?.Latitude,
                    finalStop?.Longitude);
            })
            .ToList();

        return Ok(records);
    }

    private static async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(
        IQueryable<T> query,
        Func<T, TKey> keySelector,
        CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch { return new Dictionary<TKey, T>(); }
    }
}
