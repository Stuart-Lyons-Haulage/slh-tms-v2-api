using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/integrations/samsara")]
[Authorize]
public sealed class SamsaraDispatchController(
    TmsDbContext db,
    SamsaraClient samsara,
    SamsaraOptions options,
    ILogger<SamsaraDispatchController> logger) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return Ok(new
            {
                configured = false,
                connected = false,
                vehicleCount = 0,
                driverCount = 0,
                missingSettings = samsara.MissingSettings,
                message = $"Samsara runtime settings are incomplete: {string.Join(", ", samsara.MissingSettings)}."
            });

        try
        {
            var summary = await samsara.GetConnectionSummaryAsync(ct);
            return Ok(new
            {
                configured = true,
                connected = summary.Connected,
                summary.VehicleCount,
                summary.DriverCount,
                missingSettings = Array.Empty<string>(),
                message = $"Samsara is connected. {summary.VehicleCount} vehicle(s) and {summary.DriverCount} driver(s) are visible to the API token."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Samsara status check failed.");
            return Ok(new
            {
                configured = true,
                connected = false,
                vehicleCount = 0,
                driverCount = 0,
                missingSettings = Array.Empty<string>(),
                message = $"Samsara could not be reached or rejected the API token: {exception.GetBaseException().Message}"
            });
        }
    }

    [HttpGet("dispatch/status")]
    public async Task<IActionResult> DispatchStatus([FromQuery] DateOnly date, CancellationToken ct)
    {
        var loads = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
        var byId = loads.ToDictionary(load => load.Id);
        if (byId.Count == 0)
        {
            var sqlLoads = await db.Loads.AsNoTracking()
                .Where(load => load.PlanningDate == date)
                .ToListAsync(ct);
            byId = sqlLoads.ToDictionary(load => load.Id);
        }

        var ids = byId.Keys.ToList();
        var mappings = ids.Count == 0
            ? []
            : await db.IntegrationMappings.AsNoTracking()
                .Where(item => item.Active &&
                               item.Provider == "Samsara" &&
                               item.TmsEntityType == "Load" &&
                               ids.Contains(item.TmsEntityId))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToListAsync(ct);

        return Ok(new
        {
            planningDate = date,
            configured = samsara.IsConfigured,
            runs = mappings
                .GroupBy(item => item.TmsEntityId)
                .Select(group => group.First())
                .Select(item => new
                {
                    runId = item.TmsEntityId,
                    reference = byId.GetValueOrDefault(item.TmsEntityId)?.Reference,
                    routeId = item.ExternalKey,
                    exportedAtUtc = item.UpdatedAtUtc
                })
                .OrderBy(item => item.reference)
                .ToList()
        });
    }

    [HttpGet("dispatch/{runId:guid}/status")]
    public async Task<IActionResult> DispatchStatus(Guid runId, CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return Ok(new { configured = false, exported = false, missingSettings = samsara.MissingSettings });

        try
        {
            var route = await samsara.GetRouteByRunIdAsync(runId, ct);
            return Ok(new
            {
                configured = true,
                exported = route is not null,
                routeId = route?.Id,
                routeName = route?.Name,
                assignedDriverId = route?.DriverId,
                assignedVehicleId = route?.VehicleId,
                externalId = $"{samsara.ExternalIdKey}:{runId:N}"
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Samsara route status failed for run {RunId}.", runId);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.GetBaseException().Message });
        }
    }

    [HttpPost("dispatch/{runId:guid}")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Dispatch(Guid runId, CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return BadRequest(new
            {
                message = $"Samsara cannot dispatch until these settings are complete: {string.Join(", ", samsara.MissingSettings)}.",
                missingSettings = samsara.MissingSettings
            });

        var load = await FindLoadAsync(runId, ct);
        if (load is null)
            return NotFound(new { message = "The selected run could not be found." });
        if (load.Status == LoadStatus.Cancelled)
            return BadRequest(new { message = "A cancelled run cannot be sent to Samsara." });
        if (load.VehicleId is null)
            return BadRequest(new { message = "Allocate a vehicle before sending the run to Samsara." });
        if (load.Stops.Count < 2)
            return BadRequest(new { message = "Samsara routes require at least two stops." });

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        if (vehicle is null)
            return BadRequest(new { message = "The allocated vehicle could not be found in Vehicle Master." });

        var driver = load.DriverId is null
            ? null
            : await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);

        try
        {
            var dispatchState = await DriverDispatchStateStore.ReadAsync(db, [load.Id], ct);
            dispatchState.TryGetValue(load.Id, out var state);
            var orderedStops = load.Stops.OrderBy(stop => stop.Sequence).ToList();
            var firstScheduled = state?.PlannedStartUtc ?? orderedStops[0].PlannedArrivalUtc;
            if (firstScheduled is null)
                return BadRequest(new { message = "Set the planned dispatch start before sending the run to Samsara." });

            var sites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(5000).ToListAsync(ct);
            try
            {
                await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Site Master coordinate enrichment was unavailable during Samsara dispatch.");
            }

            var samsaraStops = new List<SamsaraRouteStopRequest>();
            var missingLocations = new List<string>();
            foreach (var (stop, index) in orderedStops.Select((value, index) => (value, index)))
            {
                var resolved = ResolveLocation(stop, sites);
                if (resolved.Latitude is null || resolved.Longitude is null)
                {
                    missingLocations.Add(stop.Name);
                    continue;
                }

                samsaraStops.Add(new SamsaraRouteStopRequest(
                    stop.Id,
                    resolved.Address,
                    resolved.Latitude.Value,
                    resolved.Longitude.Value,
                    samsara.StopRadiusMeters,
                    index == 0 ? stop.PlannedArrivalUtc : stop.PlannedArrivalUtc,
                    index == 0 ? firstScheduled : null,
                    stop.PlannerNote));
            }

            if (missingLocations.Count > 0)
                return BadRequest(new
                {
                    message = $"Samsara needs coordinates for every route stop. Complete Site Master/geofence mapping for: {string.Join(", ", missingLocations.Distinct(StringComparer.OrdinalIgnoreCase))}.",
                    missingStops = missingLocations
                });

            var samsaraDriverId = driver is null ? null : await ResolveDriverIdAsync(driver, ct);
            var samsaraVehicleId = await ResolveVehicleIdAsync(vehicle, ct);
            if (string.IsNullOrWhiteSpace(samsaraDriverId) && string.IsNullOrWhiteSpace(samsaraVehicleId))
                return BadRequest(new
                {
                    message = $"No Samsara driver or vehicle match could be found for run {load.Reference}. Vehicle {vehicle.Registration} must exist in Samsara or be mapped before export."
                });

            var notes = string.Join("\n", new[]
            {
                $"SLH TMS run {load.Reference}",
                driver is null ? null : $"Driver: {driver.DisplayName}",
                $"Vehicle: {vehicle.Registration}",
                load.TrailerId is null ? null : $"Trailer TMS ID: {load.TrailerId}",
                string.IsNullOrWhiteSpace(load.PlannerNotes) ? null : $"Planner: {load.PlannerNotes}"
            }.Where(line => !string.IsNullOrWhiteSpace(line)));

            var request = new SamsaraRouteRequest(
                load.Id,
                $"SLH {load.Reference}",
                notes,
                samsaraDriverId,
                string.IsNullOrWhiteSpace(samsaraDriverId) ? samsaraVehicleId : null,
                samsaraStops);

            var result = await samsara.UpsertRouteAsync(request, ct);
            await SaveMappingAsync(
                "Load",
                load.Id,
                result.RouteId ?? result.ExternalId,
                load.Reference,
                ct);
            return Ok(new
            {
                success = true,
                runId = load.Id,
                load.Reference,
                result.RouteId,
                result.ExternalId,
                result.Created,
                result.Updated,
                assignment = !string.IsNullOrWhiteSpace(samsaraDriverId) ? "driver" : "vehicle",
                samsaraDriverId,
                samsaraVehicleId,
                stopCount = samsaraStops.Count,
                message = result.Created
                    ? $"{load.Reference} was created in Samsara."
                    : $"{load.Reference} already existed in Samsara and was updated rather than duplicated."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Samsara dispatch failed for run {RunId}.", runId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = $"Samsara dispatch failed: {exception.GetBaseException().Message}"
            });
        }
    }

    private async Task<Load?> FindLoadAsync(Guid runId, CancellationToken ct)
    {
        var registered = await PlanningRegisterStore.GetLoadAsync(db, runId, ct);
        if (registered is not null) return registered;
        return await db.Loads.AsNoTracking().Include(load => load.Stops).SingleOrDefaultAsync(load => load.Id == runId, ct);
    }

    private async Task<string?> ResolveVehicleIdAsync(Vehicle vehicle, CancellationToken ct)
    {
        var mapped = await ExistingMappingAsync("Vehicle", vehicle.Id, ct);
        if (!string.IsNullOrWhiteSpace(mapped)) return mapped;

        var vehicles = await samsara.GetVehiclesAsync(ct);
        var registration = Normalise(vehicle.Registration);
        var vin = Normalise(vehicle.VIN);
        var matches = vehicles.Where(item =>
                (!string.IsNullOrWhiteSpace(registration) &&
                    (Normalise(item.LicensePlate) == registration || Normalise(item.Name) == registration)) ||
                (!string.IsNullOrWhiteSpace(vin) && Normalise(item.Vin) == vin))
            .ToList();

        if (matches.Count != 1) return null;
        await SaveMappingAsync("Vehicle", vehicle.Id, matches[0].Id, vehicle.Registration, ct);
        return matches[0].Id;
    }

    private async Task<string?> ResolveDriverIdAsync(Driver driver, CancellationToken ct)
    {
        var mapped = await ExistingMappingAsync("Driver", driver.Id, ct);
        if (!string.IsNullOrWhiteSpace(mapped)) return mapped;

        var drivers = await samsara.GetDriversAsync(ct);
        var employee = Normalise(driver.EmployeeNumber);
        var name = Normalise(driver.DisplayName);
        var matches = drivers.Where(item =>
                item.ExternalIds.Values.Any(value => Normalise(value) == employee) ||
                (!string.IsNullOrWhiteSpace(name) && Normalise(item.Name) == name))
            .ToList();

        if (matches.Count != 1) return null;
        await SaveMappingAsync("Driver", driver.Id, matches[0].Id, driver.DisplayName, ct);
        return matches[0].Id;
    }

    private async Task<string?> ExistingMappingAsync(string entityType, Guid entityId, CancellationToken ct)
    {
        var mapping = await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Active &&
                           item.Provider == "Samsara" &&
                           item.TmsEntityType == entityType &&
                           item.TmsEntityId == entityId)
            .OrderByDescending(item => item.UpdatedAtUtc)
            .FirstOrDefaultAsync(ct);
        return mapping?.ExternalKey;
    }

    private async Task SaveMappingAsync(string entityType, Guid entityId, string samsaraId, string label, CancellationToken ct)
    {
        var mapping = await db.IntegrationMappings.SingleOrDefaultAsync(item =>
            item.Provider == "Samsara" &&
            item.TmsEntityType == entityType &&
            item.TmsEntityId == entityId &&
            item.Active, ct);

        if (mapping is null)
        {
            mapping = new IntegrationMapping
            {
                Provider = "Samsara",
                ExternalKey = samsaraId,
                ExternalLabel = label,
                TmsEntityType = entityType,
                TmsEntityId = entityId,
                Active = true,
                MappingKind = "ProviderIdentity",
                NormalizedExternalValue = Normalise(samsaraId),
                Notes = "Automatically matched by SLH TMS Samsara integration.",
                UpdatedBy = User.Identity?.Name ?? "system:samsara"
            };
            db.IntegrationMappings.Add(mapping);
        }
        else
        {
            mapping.ExternalKey = samsaraId;
            mapping.ExternalLabel = label;
            mapping.NormalizedExternalValue = Normalise(samsaraId);
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = User.Identity?.Name ?? "system:samsara";
        }

        await db.SaveChangesAsync(ct);
    }

    private static ResolvedStopLocation ResolveLocation(LoadStop stop, IReadOnlyList<Site> sites)
    {
        if (stop.Latitude is not null && stop.Longitude is not null)
            return new ResolvedStopLocation(
                string.IsNullOrWhiteSpace(stop.Address) ? CleanStopName(stop.Name) : stop.Address!,
                (double)stop.Latitude.Value,
                (double)stop.Longitude.Value);

        var stopKeys = new[]
        {
            CleanStopName(stop.Name),
            stop.Address
        }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(Normalise).Where(value => value.Length > 0).ToHashSet();

        var matches = sites.Where(site => SiteKeys(site).Any(stopKeys.Contains)).Take(2).ToList();
        if (matches.Count != 1)
            return new ResolvedStopLocation(stop.Address ?? CleanStopName(stop.Name), null, null);

        var site = matches[0];
        return new ResolvedStopLocation(
            stop.Address ?? site.CollectionAddress ?? site.Name,
            site.Latitude is null ? null : (double)site.Latitude.Value,
            site.Longitude is null ? null : (double)site.Longitude.Value);
    }

    private static IEnumerable<string> SiteKeys(Site site)
    {
        foreach (var value in new[] { site.Name, site.DriverTextName, site.ExternalCode, site.CollectionAddress })
        {
            var key = Normalise(value);
            if (key.Length > 0) yield return key;
        }

        foreach (var alias in (site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Normalise(alias);
            if (key.Length > 0) yield return key;
        }
    }

    private static string CleanStopName(string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, @"^(?:Collect|Deliver)\s*[·:\-]\s*", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record ResolvedStopLocation(string Address, double? Latitude, double? Longitude);
}
