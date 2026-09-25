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
                planningAuthority = "SLH TMS",
                samsaraRole = "Execution and route progress",
                recomputeScheduledTimes = options.RecomputeScheduledTimes,
                addressSyncEnabled = options.EnableAddressSync,
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
                planningAuthority = "SLH TMS",
                samsaraRole = "Execution and route progress",
                recomputeScheduledTimes = options.RecomputeScheduledTimes,
                addressSyncEnabled = options.EnableAddressSync,
                routeStartingCondition = options.RouteStartingCondition,
                routeCompletionCondition = options.RouteCompletionCondition,
                sequencingMethod = options.SequencingMethod,
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
                planningAuthority = "SLH TMS",
                samsaraRole = "Execution and route progress",
                recomputeScheduledTimes = options.RecomputeScheduledTimes,
                addressSyncEnabled = options.EnableAddressSync,
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
            planningAuthority = "SLH TMS",
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
                scheduledRouteStartTime = route?.ScheduledRouteStartTime,
                scheduledRouteEndTime = route?.ScheduledRouteEndTime,
                externalId = $"{samsara.ExternalIdKey}:{runId:N}",
                stops = route?.Stops.Select(stop => new
                {
                    stop.Id,
                    stop.SequenceNumber,
                    stop.Name,
                    stop.State,
                    stop.AddressId,
                    stop.ScheduledArrivalTime,
                    stop.ScheduledDepartureTime,
                    stop.ActualArrivalTime,
                    stop.ActualDepartureTime,
                    stop.EstimatedArrivalTime,
                    stop.LiveSharingUrl,
                    tmsStopId = ResolveTmsStopId(stop)
                }).ToList() ?? []
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Samsara route status failed for run {RunId}.", runId);
            return StatusCode(StatusCodes.Status502BadGateway, new { message = exception.GetBaseException().Message });
        }
    }

    [HttpGet("dispatch/progress-feed")]
    public async Task<IActionResult> ProgressFeed([FromQuery] string? after, CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return BadRequest(new { message = "Samsara is not configured.", missingSettings = samsara.MissingSettings });

        try
        {
            var feed = await samsara.GetRouteAuditFeedAsync(after, ct);
            return Ok(new
            {
                entries = feed.Entries,
                endCursor = feed.EndCursor,
                feed.HasNextPage
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Samsara route progress feed failed.");
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
            return BadRequest(new
            {
                message = "This run is cancelled. Use the Samsara cancellation endpoint to remove any previously exported route.",
                cancellationEndpoint = $"/api/v1/integrations/samsara/dispatch/{runId}"
            });

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

            var orderIds = orderedStops
                .Where(stop => stop.OrderId is not null)
                .Select(stop => stop.OrderId!.Value)
                .Distinct()
                .ToList();
            var orders = orderIds.Count == 0
                ? new Dictionary<Guid, TransportOrder>()
                : await db.TransportOrders.AsNoTracking()
                    .Where(order => orderIds.Contains(order.Id))
                    .ToDictionaryAsync(order => order.Id, ct);

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
            var missingSchedule = new List<string>();
            var addressFallbacks = new List<string>();
            var departFirstStop = !string.Equals(options.RouteStartingCondition, "arriveFirstStop", StringComparison.OrdinalIgnoreCase);
            var departLastStop = !string.Equals(options.RouteCompletionCondition, "arriveLastStop", StringComparison.OrdinalIgnoreCase);

            foreach (var (stop, index) in orderedStops.Select((value, index) => (value, index)))
            {
                var isFirst = index == 0;
                var isLast = index == orderedStops.Count - 1;
                if (!isFirst && stop.PlannedArrivalUtc is null)
                {
                    missingSchedule.Add(stop.Name);
                    continue;
                }
                var resolved = ResolveLocation(stop, sites);
                if (resolved.Latitude is null || resolved.Longitude is null)
                {
                    missingLocations.Add(stop.Name);
                    continue;
                }

                string? samsaraAddressId = null;
                if (samsara.AddressSyncEnabled && resolved.Site is not null)
                {
                    try
                    {
                        var addressResult = await samsara.UpsertAddressAsync(
                            new SamsaraAddressRequest(
                                resolved.Site.Id,
                                resolved.Site.DriverTextName ?? resolved.Site.Name,
                                resolved.Address,
                                resolved.Latitude.Value,
                                resolved.Longitude.Value,
                                samsara.StopRadiusMeters),
                            ct);

                        samsaraAddressId = addressResult.AddressId;
                        if (!string.IsNullOrWhiteSpace(samsaraAddressId))
                            await SaveMappingAsync("Site", resolved.Site.Id, samsaraAddressId, resolved.Site.Name, ct);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        addressFallbacks.Add(stop.Name);
                        logger.LogWarning(
                            exception,
                            "Samsara reusable address sync failed for site {SiteId}; route stop will use a single-use location.",
                            resolved.Site.Id);
                    }
                }

                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                var stopNotes = BuildStopNotes(stop, order);
                var scheduledArrival = isFirst && departFirstStop
                    ? null
                    : stop.PlannedArrivalUtc ?? firstScheduled;
                var scheduledDeparture = isFirst && departFirstStop
                    ? firstScheduled
                    : isLast && departLastStop
                        ? stop.PlannedArrivalUtc
                        : null;

                samsaraStops.Add(new SamsaraRouteStopRequest(
                    stop.Id,
                    index + 1,
                    CleanStopName(stop.Name),
                    samsaraAddressId,
                    resolved.Address,
                    resolved.Latitude.Value,
                    resolved.Longitude.Value,
                    samsara.StopRadiusMeters,
                    scheduledArrival,
                    scheduledDeparture,
                    stopNotes));
            }

            if (missingLocations.Count > 0)
                return BadRequest(new
                {
                    message = $"Samsara needs coordinates for every route stop. Complete Site Master/geofence mapping for: {string.Join(", ", missingLocations.Distinct(StringComparer.OrdinalIgnoreCase))}.",
                    missingStops = missingLocations
                });

            if (missingSchedule.Count > 0)
                return BadRequest(new
                {
                    message = $"Every Samsara stop after the route start needs a planned arrival time because SLH TMS owns the schedule. Complete the plan for: {string.Join(", ", missingSchedule.Distinct(StringComparer.OrdinalIgnoreCase))}.",
                    missingStops = missingSchedule
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
                "Planning authority: SLH TMS",
                driver is null ? null : $"Driver: {driver.DisplayName}",
                $"Vehicle: {vehicle.Registration}",
                state?.PlannedStartUtc is null ? null : $"Planned yard start: {state.PlannedStartUtc:O}",
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

            if (result.Route is not null)
            {
                foreach (var remoteStop in result.Route.Stops)
                {
                    var tmsStopId = ResolveTmsStopId(remoteStop);
                    if (tmsStopId is null || string.IsNullOrWhiteSpace(remoteStop.Id)) continue;

                    var localStop = orderedStops.FirstOrDefault(stop => stop.Id == tmsStopId.Value);
                    await SaveMappingAsync(
                        "LoadStop",
                        tmsStopId.Value,
                        remoteStop.Id,
                        localStop?.Name ?? tmsStopId.Value.ToString(),
                        ct);
                }
            }

            return Ok(new
            {
                success = true,
                runId = load.Id,
                load.Reference,
                result.RouteId,
                result.ExternalId,
                result.Created,
                result.Updated,
                planningAuthority = "SLH TMS",
                samsaraRecomputedSchedule = options.RecomputeScheduledTimes,
                assignment = !string.IsNullOrWhiteSpace(samsaraDriverId) ? "driver" : "vehicle",
                samsaraDriverId,
                samsaraVehicleId,
                allocatedVehicle = vehicle.Registration,
                allocatedDriver = driver?.DisplayName,
                stopCount = samsaraStops.Count,
                reusableAddressStops = samsaraStops.Count(stop => !string.IsNullOrWhiteSpace(stop.AddressId)),
                singleUseFallbackStops = samsaraStops.Count(stop => string.IsNullOrWhiteSpace(stop.AddressId)),
                addressFallbacks,
                message = result.Created
                    ? $"{load.Reference} was created in Samsara from the SLH TMS plan."
                    : $"{load.Reference} already existed in Samsara and was updated from the current SLH TMS plan rather than duplicated."
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

    [HttpDelete("dispatch/{runId:guid}")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> CancelDispatch(Guid runId, CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return BadRequest(new
            {
                message = $"Samsara cannot cancel a route until these settings are complete: {string.Join(", ", samsara.MissingSettings)}.",
                missingSettings = samsara.MissingSettings
            });

        try
        {
            var deleted = await samsara.DeleteRouteByRunIdAsync(runId, ct);
            await DeactivateMappingsAsync(runId, ct);

            return Ok(new
            {
                success = true,
                runId,
                deleted,
                message = deleted
                    ? "The Samsara route was deleted and its TMS mappings were deactivated."
                    : "No Samsara route existed; any local Samsara mappings were deactivated."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Samsara route cancellation failed for run {RunId}.", runId);
            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                message = $"Samsara route cancellation failed: {exception.GetBaseException().Message}"
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
                (!string.IsNullOrWhiteSpace(employee) &&
                 item.ExternalIds.Values.Any(value => Normalise(value) == employee)) ||
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

    private async Task DeactivateMappingsAsync(Guid runId, CancellationToken ct)
    {
        var stopIds = await db.LoadStops.AsNoTracking()
            .Where(stop => stop.LoadId == runId)
            .Select(stop => stop.Id)
            .ToListAsync(ct);

        var mappings = await db.IntegrationMappings
            .Where(item => item.Active &&
                           item.Provider == "Samsara" &&
                           ((item.TmsEntityType == "Load" && item.TmsEntityId == runId) ||
                            (item.TmsEntityType == "LoadStop" && stopIds.Contains(item.TmsEntityId))))
            .ToListAsync(ct);

        foreach (var mapping in mappings)
        {
            mapping.Active = false;
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = User.Identity?.Name ?? "system:samsara";
        }

        if (mappings.Count > 0)
            await db.SaveChangesAsync(ct);
    }

    private static string BuildStopNotes(LoadStop stop, TransportOrder? order)
    {
        var lines = new List<string>
        {
            stop.Name
        };

        if (order is not null)
        {
            lines.Add($"Order: {order.Reference}");
            lines.Add($"Customer: {order.CustomerCode}");
            if (order.Pallets is not null) lines.Add($"Pallets: {order.Pallets}");
            if (order.DeliveryWindowStartUtc is not null || order.DeliveryWindowEndUtc is not null)
                lines.Add($"Delivery window: {FormatWindow(order.DeliveryWindowStartUtc, order.DeliveryWindowEndUtc)}");
            if (!string.IsNullOrWhiteSpace(order.MarketName)) lines.Add($"Market: {order.MarketName}");
            if (!string.IsNullOrWhiteSpace(order.SellerName)) lines.Add($"Seller: {order.SellerName}");
            if (!string.IsNullOrWhiteSpace(order.StallNumber)) lines.Add($"Stand/Stall: {order.StallNumber}");
            if (!string.IsNullOrWhiteSpace(order.DriverInstructions))
            {
                lines.Add(string.Empty);
                lines.Add($"Instructions: {order.DriverInstructions}");
            }
        }

        if (!string.IsNullOrWhiteSpace(stop.PlannerNote))
        {
            lines.Add(string.Empty);
            lines.Add($"Planner: {stop.PlannerNote}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatWindow(DateTimeOffset? start, DateTimeOffset? end) =>
        start is not null && end is not null
            ? $"{start:dd/MM/yyyy HH:mm} - {end:HH:mm}"
            : start is not null
                ? $"from {start:dd/MM/yyyy HH:mm}"
                : $"by {end:dd/MM/yyyy HH:mm}";

    private static ResolvedStopLocation ResolveLocation(LoadStop stop, IReadOnlyList<Site> sites)
    {
        var stopKeys = new[]
        {
            CleanStopName(stop.Name),
            stop.Address
        }.Where(value => !string.IsNullOrWhiteSpace(value))
         .Select(Normalise)
         .Where(value => value.Length > 0)
         .ToHashSet();

        var matches = sites.Where(site => SiteKeys(site).Any(stopKeys.Contains)).Take(2).ToList();
        var matchedSite = matches.Count == 1 ? matches[0] : null;

        if (stop.Latitude is not null && stop.Longitude is not null)
            return new ResolvedStopLocation(
                string.IsNullOrWhiteSpace(stop.Address)
                    ? matchedSite?.CollectionAddress ?? CleanStopName(stop.Name)
                    : stop.Address!,
                (double)stop.Latitude.Value,
                (double)stop.Longitude.Value,
                matchedSite);

        if (matchedSite is null)
            return new ResolvedStopLocation(stop.Address ?? CleanStopName(stop.Name), null, null, null);

        return new ResolvedStopLocation(
            stop.Address ?? matchedSite.CollectionAddress ?? matchedSite.Name,
            matchedSite.Latitude is null ? null : (double)matchedSite.Latitude.Value,
            matchedSite.Longitude is null ? null : (double)matchedSite.Longitude.Value,
            matchedSite);
    }

    private Guid? ResolveTmsStopId(SamsaraRouteStopSnapshot stop)
    {
        if (!stop.ExternalIds.TryGetValue(samsara.StopExternalIdKey, out var value) || string.IsNullOrWhiteSpace(value))
            return null;

        if (Guid.TryParseExact(value, "N", out var compact)) return compact;
        return Guid.TryParse(value, out var regular) ? regular : null;
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

    private sealed record ResolvedStopLocation(string Address, double? Latitude, double? Longitude, Site? Site);
}
