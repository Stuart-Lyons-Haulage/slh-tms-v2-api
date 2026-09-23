using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1")]
[Authorize]
public sealed class DriverPlanningController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet("driver-assignments"), AllowAnonymous]
    public async Task<IActionResult> Assignments(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken ct)
    {
        var signedInAllowed = User.Identity?.IsAuthenticated == true;
        // Older Hisense/Vewd browsers can drop custom headers. The paired key is
        // read-only and is validated against the same SQL-backed display key.
        if (string.IsNullOrWhiteSpace(displayKey) && Request.Query.TryGetValue("key", out var queryKey))
            displayKey = queryKey.FirstOrDefault();
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        if (!signedInAllowed && !pairedKeyAllowed && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var firstDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-7);
        var lastDate = to ?? firstDate;
        if (lastDate < firstDate || lastDate.DayNumber - firstDate.DayNumber > 92)
            return BadRequest("Choose a valid date range of no more than 93 days.");

        // Read assignments through the same resilient production source used by the
        // planned-runs wallboard feed. The operational enrichment is essential here:
        // Dispatch can persist the newest allocation in the operational register while a
        // stale dbo.Loads / planning-register copy still has blank DriverId / VehicleId.
        // Without this enrichment the route is visible but both wallboards show TBC.
        var loads = (await PlanningResilience.ReadLoadsAsync(db, null, ct))
            .Where(load => load.PlanningDate >= firstDate && load.PlanningDate <= lastDate)
            .OrderBy(load => load.PlanningDate)
            .ThenBy(load => load.Reference)
            .Take(2000)
            .ToList();

        await RunOperationalStore.EnrichAsync(db, loads, ct);
        loads = PlanningResilience.CollapseLogicalDuplicates(loads);

        try { await LoadCommercialStore.EnrichAsync(db, loads, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); }

        var driverIds = loads.Where(load => load.DriverId != null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(load => load.VehicleId != null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(load => load.TrailerId != null).Select(load => load.TrailerId!.Value).Distinct().ToList();
        var drivers = await SafeDictionary(db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)), driver => driver.Id, ct);
        var vehicles = await SafeDictionary(db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)), vehicle => vehicle.Id, ct);
        var trailers = await SafeDictionary(db.Trailers.AsNoTracking().Where(trailer => trailerIds.Contains(trailer.Id)), trailer => trailer.Id, ct);

        return Ok(loads.Select(load =>
        {
            var finalStop = load.Stops.OrderBy(stop => stop.Sequence).LastOrDefault();
            return new DriverAssignmentResponse(load.Id, load.PlanningDate, RunDisplayLabel.For(load), load.Status.ToString(),
                load.DriverId is Guid driverId && drivers.TryGetValue(driverId, out var driver) ? new AssignmentDriver(driver.Id, driver.DisplayName, driver.EmployeeNumber) : null,
                load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) ? new AssignmentVehicle(vehicle.Id, vehicle.Registration, vehicle.FleetNumber) : null,
                load.TrailerId is Guid trailerId && trailers.TryGetValue(trailerId, out var trailer) ? trailer.TrailerNumber : null,
                load.Stops.Count, finalStop?.Name, finalStop?.Latitude, finalStop?.Longitude);
        }));
    }

    [HttpGet("planning/return-load-suggestions")]
    public async Task<IActionResult> ReturnLoadSuggestions([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var planningDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var lookback = planningDate.AddDays(-7);
        List<Load> recentLoads;
        List<Load> targetLoads;
        try
        {
            recentLoads = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate >= lookback && load.PlanningDate < planningDate && load.DriverId != null && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).ToListAsync(ct);
            targetLoads = await db.Loads.AsNoTracking().Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate && load.DriverId == null && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.Reference).ToListAsync(ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            var registerLoads = await PlanningRegisterStore.ReadLoadsAsync(db, null, ct);
            recentLoads = registerLoads
                .Where(load => load.PlanningDate >= lookback && load.PlanningDate < planningDate && load.DriverId != null && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.PlanningDate).ThenBy(load => load.Reference).ToList();
            targetLoads = registerLoads
                .Where(load => load.PlanningDate == planningDate && load.DriverId == null && load.Status != LoadStatus.Cancelled)
                .OrderBy(load => load.Reference).ToList();
        }

        try
        {
            await LoadCommercialStore.EnrichAsync(db, recentLoads, ct);
            await LoadCommercialStore.EnrichAsync(db, targetLoads, ct);
        }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { db.ChangeTracker.Clear(); }

        var driverIds = recentLoads.Select(load => load.DriverId!.Value).Distinct().ToList();
        var drivers = await SafeDictionary(db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id) && driver.Active), driver => driver.Id, ct);

        var suggestions = new List<ReturnLoadSuggestion>();
        foreach (var driverLoads in recentLoads.GroupBy(load => load.DriverId!.Value))
        {
            if (!drivers.TryGetValue(driverLoads.Key, out var driver)) continue;
            var byDate = driverLoads.GroupBy(load => load.PlanningDate).ToDictionary(group => group.Key, group => group.ToList());
            var consecutiveDays = 0;
            for (var day = planningDate.AddDays(-1); byDate.ContainsKey(day); day = day.AddDays(-1)) consecutiveDays++;
            if (consecutiveDays == 0) continue;

            var latestLoad = driverLoads.OrderByDescending(load => load.PlanningDate).ThenByDescending(load => load.CreatedAtUtc).First();
            var finalStop = latestLoad.Stops.OrderBy(stop => stop.Sequence).LastOrDefault();
            var live = await LatestLiveStatus(latestLoad.VehicleId, ct);
            var latitude = live?.Latitude ?? finalStop?.Latitude;
            var longitude = live?.Longitude ?? finalStop?.Longitude;
            var location = live is null ? finalStop?.Name : $"Live position · {live.VehicleIdentifier}";
            var isNorth = latitude >= 53m;

            var compatible = targetLoads.Select(load => new
            {
                Load = load,
                First = load.Stops.OrderBy(stop => stop.Sequence).FirstOrDefault(),
                Last = load.Stops.OrderBy(stop => stop.Sequence).LastOrDefault()
            }).Where(item => item.First?.Latitude is not null && item.Last?.Latitude is not null)
              .Where(item => !isNorth || item.First!.Latitude >= 52.5m)
              .OrderBy(item => item.Last!.Latitude).FirstOrDefault();

            var priority = consecutiveDays >= 5 && isNorth ? 100 : consecutiveDays >= 5 ? 80 : isNorth ? 60 : 30;
            var reason = consecutiveDays >= 5 && isNorth
                ? $"Day {consecutiveDays + 1}: driver is north and should be prioritised for southbound work."
                : consecutiveDays >= 5
                    ? $"Day {consecutiveDays + 1}: prioritise work that returns the driver toward home."
                    : isNorth ? "Driver finished in the north; consider a southbound return load." : "Driver worked yesterday and is available for continuity planning.";
            suggestions.Add(new ReturnLoadSuggestion(driver.Id, driver.DisplayName, driver.EmployeeNumber, consecutiveDays, RunDisplayLabel.For(latestLoad),
                latestLoad.PlanningDate, location, latitude, longitude, compatible?.Load.Id, compatible is null ? null : RunDisplayLabel.For(compatible.Load), priority, reason));
        }

        return Ok(new { planningDate, generatedAtUtc = DateTimeOffset.UtcNow, suggestions = suggestions.OrderByDescending(item => item.Priority).ThenBy(item => item.DriverName) });
    }

    private async Task<VehicleLiveStatus?> LatestLiveStatus(Guid? vehicleId, CancellationToken ct)
    {
        if (vehicleId is null) return null;
        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == vehicleId, ct);
        if (vehicle is null) return null;
        var keys = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalise).ToList();
        var statuses = await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct);
        return statuses.Where(status => keys.Contains(Normalise(status.VehicleIdentifier))).OrderByDescending(status => status.LastEventTimeUtc).FirstOrDefault();
    }

    private static async Task<Dictionary<TKey, T>> SafeDictionary<T, TKey>(IQueryable<T> query, Func<T, TKey> keySelector, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(keySelector, ct); }
        catch (Exception exception) when (IsSchemaUnavailable(exception)) { return []; }
    }

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record AssignmentDriver(Guid Id, string DisplayName, string EmployeeNumber);
public sealed record AssignmentVehicle(Guid Id, string Registration, string? FleetNumber);
public sealed record DriverAssignmentResponse(Guid LoadId, DateOnly PlanningDate, string LoadReference, string Status, AssignmentDriver? Driver, AssignmentVehicle? Vehicle, string? TrailerNumber, int StopCount, string? FinalStop, decimal? FinalLatitude, decimal? FinalLongitude);
public sealed record ReturnLoadSuggestion(Guid DriverId, string DriverName, string EmployeeNumber, int ConsecutiveDays, string PreviousLoadReference, DateOnly PreviousPlanningDate, string? LastLocation, decimal? Latitude, decimal? Longitude, Guid? SuggestedLoadId, string? SuggestedLoadReference, int Priority, string Reason);
