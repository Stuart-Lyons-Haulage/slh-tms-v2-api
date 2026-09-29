using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
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
    DispatchService dispatch,
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
                routeProgressSyncEnabled = options.EnableRouteProgressSync,
                routeProgressPollSeconds = options.RouteProgressPollSeconds,
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
                routeProgressSyncEnabled = options.EnableRouteProgressSync,
                routeProgressPollSeconds = options.RouteProgressPollSeconds,
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
                routeProgressSyncEnabled = options.EnableRouteProgressSync,
                routeProgressPollSeconds = options.RouteProgressPollSeconds,
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

        var stopIds = byId.Values
            .SelectMany(load => load.Stops)
            .Select(stop => stop.Id)
            .Distinct()
            .ToList();

        var stopMappings = stopIds.Count == 0
            ? []
            : await db.IntegrationMappings.AsNoTracking()
                .Where(item => item.Active &&
                               item.Provider == "Samsara" &&
                               item.TmsEntityType == "LoadStop" &&
                               stopIds.Contains(item.TmsEntityId))
                .OrderByDescending(item => item.UpdatedAtUtc)
                .ToListAsync(ct);

        var progressByStop = stopMappings
            .GroupBy(item => item.TmsEntityId)
            .Select(group => group.First())
            .Select(item => new SamsaraStopProgressEnvelope(
                item.TmsEntityId,
                item.ExternalLabel,
                item.UpdatedAtUtc,
                SamsaraRouteProgressService.ReadProgress(item.Notes)))
            .Where(item => item.Progress is not null)
            .ToDictionary(item => item.StopId);

        var rows = mappings
            .GroupBy(item => item.TmsEntityId)
            .Select(group => group.First())
            .Select(item =>
            {
                var load = byId.GetValueOrDefault(item.TmsEntityId);
                var latest = load?.Stops
                    .Where(stop => progressByStop.ContainsKey(stop.Id))
                    .Select(stop => new
                    {
                        stop.Name,
                        Envelope = progressByStop[stop.Id]
                    })
                    .OrderByDescending(value =>
                        value.Envelope.Progress?.OccurredAtUtc ?? value.Envelope.UpdatedAtUtc)
                    .FirstOrDefault();

                return new
                {
                    runId = item.TmsEntityId,
                    reference = load?.Reference,
                    routeId = item.ExternalKey,
                    exportedAtUtc = item.UpdatedAtUtc,
                    executionState = latest?.Envelope.Progress?.State,
                    executionOperation = latest?.Envelope.Progress?.Operation,
                    executionUpdatedAtUtc = latest?.Envelope.Progress?.OccurredAtUtc ?? latest?.Envelope.UpdatedAtUtc,
                    lastStopName = latest?.Name
                };
            })
            .OrderBy(item => item.reference)
            .ToList();

        var connected = false;
        string? connectionMessage = null;
        if (samsara.IsConfigured)
        {
            try
            {
                connected = await samsara.CheckConnectivityAsync(ct);
                connectionMessage = connected ? "Samsara EU API connected." : "Samsara is configured but did not pass the connection check.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Samsara dispatch connectivity probe failed.");
                connectionMessage = $"Samsara is configured but not reachable: {exception.GetBaseException().Message}";
            }
        }
        else
        {
            connectionMessage = $"Samsara runtime settings are incomplete: {string.Join(", ", samsara.MissingSettings)}.";
        }

        return Ok(new
        {
            planningDate = date,
            configured = samsara.IsConfigured,
            connected,
            connectionMessage,
            planningAuthority = "SLH TMS",
            routeProgressSyncEnabled = options.EnableRouteProgressSync,
            runs = rows
        });
    }

    [HttpGet("sites/address-sync/status")]
    public async Task<IActionResult> SiteAddressSyncStatus(CancellationToken ct)
    {
        var sites = await ReadEnrichedSitesAsync(ct);
        var candidates = ToSiteAddressCandidates(sites);
        var mappedIds = await db.IntegrationMappings.AsNoTracking()
            .Where(item => item.Active &&
                           item.Provider == "Samsara" &&
                           item.TmsEntityType == "Site")
            .Select(item => item.TmsEntityId)
            .ToListAsync(ct);
        var mapped = candidates.Count(item => mappedIds.Contains(item.Site.Id));
        var pending = candidates.Count - mapped;
        var activeSites = sites.Count;
        var sitesWithReference = sites.Count(site => !string.IsNullOrWhiteSpace(site.ExternalCode));
        var sitesWithAddress = sites.Count(site => !string.IsNullOrWhiteSpace(site.CollectionAddress));
        var sitesWithCoordinates = sites.Count(site => site.Latitude is not null && site.Longitude is not null);
        var missingCoordinates = sites.Count(site => !string.IsNullOrWhiteSpace(site.ExternalCode) &&
                                                     (site.Latitude is null || site.Longitude is null));
        var missingAddress = sites.Count(site => !string.IsNullOrWhiteSpace(site.ExternalCode) &&
                                                 string.IsNullOrWhiteSpace(site.CollectionAddress));

        return Ok(new
        {
            configured = samsara.IsConfigured,
            addressSyncEnabled = samsara.AddressSyncEnabled,
            activeSites,
            sitesWithReference,
            sitesWithAddress,
            sitesWithCoordinates,
            eligibleSites = candidates.Count,
            alreadyMapped = mapped,
            pending,
            missingCoordinates,
            missingAddress,
            message = !samsara.IsConfigured
                ? $"Samsara settings are incomplete: {string.Join(", ", samsara.MissingSettings)}."
                : candidates.Count == 0
                    ? "No active Master Sites currently have both an address and coordinates for reusable Samsara Addresses."
                    : $"{candidates.Count} active Master Site(s) are ready for Samsara Address synchronisation."
        });
    }

    [HttpPost("sites/address-sync")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncSiteAddresses(CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return BadRequest(new
            {
                message = $"Samsara cannot synchronise site addresses until these settings are complete: {string.Join(", ", samsara.MissingSettings)}.",
                missingSettings = samsara.MissingSettings
            });

        if (!samsara.AddressSyncEnabled)
            return BadRequest(new { message = "Samsara reusable Address synchronisation is disabled in runtime configuration." });

        var candidates = await ReadSiteAddressCandidatesAsync(ct);
        using var addressGate = new SemaphoreSlim(4, 4);
        var results = await Task.WhenAll(candidates.Select(async candidate =>
        {
            await addressGate.WaitAsync(ct);
            try
            {
                var result = await samsara.UpsertAddressAsync(
                    new SamsaraAddressRequest(
                        candidate.Site.Id,
                        candidate.Site.ExternalCode,
                        candidate.Site.DriverTextName ?? candidate.Site.Name,
                        candidate.Site.CollectionAddress!,
                        (double)candidate.Site.Latitude!.Value,
                        (double)candidate.Site.Longitude!.Value,
                        candidate.Site.GeofenceRadiusMetres ?? samsara.StopRadiusMeters),
                    ct);
                return new SiteAddressSyncResult(candidate.Site, result, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Samsara Master Site address sync failed for {SiteReference}.", candidate.Site.ExternalCode);
                return new SiteAddressSyncResult(candidate.Site, null, exception.GetBaseException().Message);
            }
            finally
            {
                addressGate.Release();
            }
        }));

        foreach (var result in results.Where(item => item.Upsert is not null))
        {
            await SaveMappingAsync(
                "Site",
                result.Site.Id,
                result.Upsert!.AddressId ?? result.Upsert.ExternalId,
                result.Site.Name,
                ct);
        }

        var failures = results.Where(item => item.Error is not null)
            .Select(item => new { siteReference = item.Site.ExternalCode, siteName = item.Site.Name, error = item.Error })
            .ToList();
        var created = results.Count(item => item.Upsert?.Created == true);
        var updated = results.Count(item => item.Upsert?.Updated == true);
        return Ok(new
        {
            eligibleSites = candidates.Count,
            created,
            updated,
            failed = failures.Count,
            failures,
            message = failures.Count == 0
                ? $"{created + updated} Master Site address(es) synchronised to Samsara."
                : $"{created + updated} Master Site address(es) synchronised; {failures.Count} require attention."
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

    [HttpPost("dispatch/mappings/sync")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncDispatchMappings([FromQuery] DateOnly date, CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return BadRequest(new
            {
                message = $"Samsara cannot sync mappings until these settings are complete: {string.Join(", ", samsara.MissingSettings)}.",
                missingSettings = samsara.MissingSettings
            });

        var loads = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
        if (loads.Count == 0)
            loads = await db.Loads.AsNoTracking().Where(load => load.PlanningDate == date).ToListAsync(ct);

        var driverIds = loads.Where(load => load.DriverId is not null).Select(load => load.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(load => load.VehicleId is not null).Select(load => load.VehicleId!.Value).Distinct().ToList();
        var localDrivers = driverIds.Count == 0
            ? []
            : await db.Drivers.AsNoTracking().Where(driver => driverIds.Contains(driver.Id)).ToListAsync(ct);
        var localVehicles = vehicleIds.Count == 0
            ? []
            : await db.Vehicles.AsNoTracking().Where(vehicle => vehicleIds.Contains(vehicle.Id)).ToListAsync(ct);

        var remoteDriversTask = samsara.GetDriversAsync(ct);
        var remoteVehiclesTask = samsara.GetVehiclesAsync(ct);
        await Task.WhenAll(remoteDriversTask, remoteVehiclesTask);
        var remoteDrivers = await remoteDriversTask;
        var remoteVehicles = await remoteVehiclesTask;

        var unmatchedDrivers = new List<string>();
        var unmatchedVehicles = new List<string>();
        var driversMapped = 0;
        var vehiclesMapped = 0;

        foreach (var driver in localDrivers)
        {
            if (!string.IsNullOrWhiteSpace(await ExistingMappingAsync("Driver", driver.Id, ct)))
            {
                driversMapped++;
                continue;
            }

            var match = MatchDriver(driver, remoteDrivers);
            if (match is null)
            {
                unmatchedDrivers.Add(driver.DisplayName);
                continue;
            }

            await SaveMappingAsync("Driver", driver.Id, match.Id, match.Username ?? match.Name ?? driver.DisplayName, ct);
            driversMapped++;
        }

        foreach (var vehicle in localVehicles)
        {
            if (!string.IsNullOrWhiteSpace(await ExistingMappingAsync("Vehicle", vehicle.Id, ct)))
            {
                vehiclesMapped++;
                continue;
            }

            var match = MatchVehicle(vehicle, remoteVehicles);
            if (match is null)
            {
                unmatchedVehicles.Add(vehicle.Registration);
                continue;
            }

            await SaveMappingAsync("Vehicle", vehicle.Id, match.Id, vehicle.Registration, ct);
            vehiclesMapped++;
        }

        return Ok(new
        {
            planningDate = date,
            driversMapped,
            vehiclesMapped,
            unmatchedDrivers,
            unmatchedVehicles,
            message = unmatchedDrivers.Count == 0 && unmatchedVehicles.Count == 0
                ? "Samsara driver and vehicle mappings are ready for dispatch."
                : "Samsara mappings were refreshed; unmatched resources still need attention."
        });
    }

    [HttpGet("dispatch/{runId:guid}/csv")]
    public async Task<IActionResult> DownloadDispatchCsv(Guid runId, CancellationToken ct)
    {
        var load = await FindLoadAsync(runId, ct);
        if (load is null)
            return NotFound(new { message = "The selected run could not be found." });
        if (load.VehicleId is null)
            return BadRequest(new { message = "Allocate a vehicle before downloading the Samsara CSV." });
        if (load.Stops.Count < 2)
            return BadRequest(new { message = "Samsara routes require at least two stops." });

        var orderedStops = load.Stops.OrderBy(stop => stop.Sequence).ToList();
        if (orderedStops.Any(stop => stop.PlannedArrivalUtc is null))
            return BadRequest(new
            {
                message = "Every stop needs a planned time before the Samsara CSV can be generated.",
                missingStops = orderedStops.Where(stop => stop.PlannedArrivalUtc is null).Select(stop => stop.Name).ToList()
            });

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.VehicleId, ct);
        if (vehicle is null)
            return BadRequest(new { message = "The allocated vehicle could not be found in Vehicle Master." });
        var driver = load.DriverId is null
            ? null
            : await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.DriverId, ct);
        var trailer = load.TrailerId is null
            ? null
            : await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.TrailerId, ct);

        var orderIds = orderedStops.Where(stop => stop.OrderId is not null).Select(stop => stop.OrderId!.Value).Distinct().ToList();
        var orders = orderIds.Count == 0
            ? new Dictionary<Guid, TransportOrder>()
            : await db.TransportOrders.AsNoTracking().Where(order => orderIds.Contains(order.Id)).ToDictionaryAsync(order => order.Id, ct);

        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(5000).ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Site Master coordinate enrichment was unavailable during Samsara CSV generation.");
        }

        var samsaraDriverUsername = string.Empty;
        if (driver is not null && samsara.IsConfigured)
        {
            try
            {
                var remoteDrivers = await samsara.GetDriversAsync(ct);
                var mappedId = await ExistingMappingAsync("Driver", driver.Id, ct);
                var match = !string.IsNullOrWhiteSpace(mappedId)
                    ? remoteDrivers.SingleOrDefault(item => string.Equals(item.Id, mappedId, StringComparison.Ordinal))
                    : MatchDriver(driver, remoteDrivers);
                samsaraDriverUsername = match?.Username ?? string.Empty;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Samsara driver username lookup failed during CSV fallback generation.");
            }
        }

        var csv = new StringBuilder();
        csv.AppendLine("Route Name,Assigned Driver Username,Assigned Vehicle Name,Stop Name,Stop Arrival Time,Stop Departure Time,Stop Notes,Address Name,Latitude,Longitude,Full Address");

        for (var index = 0; index < orderedStops.Count; index++)
        {
            var stop = orderedStops[index];
            var resolved = ResolveLocation(stop, sites);
            if (resolved.Latitude is null || resolved.Longitude is null)
                return BadRequest(new { message = $"Samsara CSV needs coordinates for every route stop. Complete Site Master/geofence mapping for {stop.Name}." });

            orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
            var stopNotes = BuildStopNotes(stop, order);
            if (index == 0)
            {
                stopNotes = string.Join("\n", new[]
                {
                    stopNotes,
                    driver is null ? null : $"Driver: {driver.DisplayName}",
                    $"Vehicle: {vehicle.Registration}",
                    trailer is null ? null : $"Trailer: {trailer.TrailerNumber}"
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            var isFirst = index == 0;
            var isLast = index == orderedStops.Count - 1;
            var arrival = isFirst ? null : stop.PlannedArrivalUtc;
            var departure = isFirst || isLast ? stop.PlannedArrivalUtc : null;
            var addressName = resolved.Site?.DriverTextName ?? resolved.Site?.Name ?? CleanStopName(stop.Name);

            csv.AppendLine(string.Join(",", new[]
            {
                Csv($"SLH {load.Reference}"),
                Csv(samsaraDriverUsername),
                Csv(vehicle.Registration),
                Csv(CleanStopName(stop.Name)),
                Csv(FormatCsvTime(arrival)),
                Csv(FormatCsvTime(departure)),
                Csv(stopNotes),
                Csv(addressName),
                Csv(resolved.Latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Csv(resolved.Longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Csv(resolved.Address)
            }));
        }

        var fileName = $"SLH-{Normalise(load.Reference)}-Samsara.csv";
        return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", fileName);
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
        var trailer = load.TrailerId is null
            ? null
            : await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == load.TrailerId, ct);

        try
        {
            var dispatchState = await DriverDispatchStateStore.ReadAsync(db, [load.Id], ct);
            dispatchState.TryGetValue(load.Id, out var state);
            var orderedStops = load.Stops.OrderBy(stop => stop.Sequence).ToList();
            if (driver is null)
                return BadRequest(new { message = "Allocate a driver before sending the run to Samsara." });
            var available = (await dispatch.GetAvailableTimesAsync(
                new DispatchAvailableTimesRequest(load.PlanningDate, [driver.Id], state?.UseReducedDailyRest == true ? [driver.Id] : []), ct))
                .Single();
            if (available.AvailableFrom is null || !string.IsNullOrWhiteSpace(available.BreachDetail))
                return BadRequest(new { message = available.BreachDetail ?? "A legal dispatch start cannot be calculated from completed TachoMaster duty data." });
            var firstScheduled = state?.DriverId == driver.Id && state.PlannedStartUtc is DateTimeOffset persistedStart
                ? persistedStart
                : available.AvailableFrom;
            if (firstScheduled < available.AvailableFrom)
                return BadRequest(new { message = $"The run cannot be sent to Samsara before the TachoMaster legal start {available.AvailableFrom:O}." });
            if (state?.DriverId != driver.Id || state.PlannedStartUtc is null)
            {
                state = await DriverDispatchStateStore.SetPlannedStartAsync(
                    db, load.Id, firstScheduled, User.Identity?.Name, ct,
                    available.RequiredRestPeriod == 9 ? "Calculated from TachoMaster · reduced 9h daily rest" : "Calculated from TachoMaster · regular 11h daily rest",
                    driver.Id,
                    state?.UseReducedDailyRest == true);
            }

            var firstPlannedStopTime = orderedStops[0].PlannedArrivalUtc;
            if (firstPlannedStopTime is DateTimeOffset firstStopTime && firstStopTime < firstScheduled)
                return BadRequest(new
                {
                    message = $"The first planned stop {orderedStops[0].Name} is timed at {firstStopTime:O}, before the driver's legal Tacho start {firstScheduled:O}."
                });

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

            var resolvedStops = orderedStops
                .Select((stop, index) => new ResolvedDispatchStop(
                    stop,
                    index,
                    ResolveLocation(stop, sites)))
                .ToList();

            var mappedSiteIds = resolvedStops
                .Where(item => item.Location.Site is not null)
                .Select(item => item.Location.Site!.Id)
                .Distinct()
                .ToList();
            var existingSiteMappings = mappedSiteIds.Count == 0
                ? []
                : await db.IntegrationMappings.AsNoTracking()
                    .Where(item => item.Active &&
                                   item.Provider == "Samsara" &&
                                   item.TmsEntityType == "Site" &&
                                   mappedSiteIds.Contains(item.TmsEntityId))
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .ToListAsync(ct);
            var samsaraAddressBySiteId = existingSiteMappings
                .GroupBy(item => item.TmsEntityId)
                .ToDictionary(group => group.Key, group => group.First().ExternalKey);

            // Address creation is the only genuinely per-site first-export work. Reuse
            // the local mapping on later exports, and keep first-time provider calls
            // bounded so a route with many stops cannot spend the whole API request in
            // a serial GET -> POST/PATCH loop.
            var addressFallbackBySiteId = new HashSet<Guid>();
            if (samsara.AddressSyncEnabled)
            {
                using var addressGate = new SemaphoreSlim(4, 4);
                var sitesToSync = resolvedStops
                    .Where(item => item.Location.Site is not null &&
                                   item.Location.Latitude is not null &&
                                   item.Location.Longitude is not null &&
                                   !samsaraAddressBySiteId.ContainsKey(item.Location.Site!.Id))
                    .GroupBy(item => item.Location.Site!.Id)
                    .Select(group => group.First())
                    .ToList();

                var addressResults = await Task.WhenAll(sitesToSync.Select(async item =>
                {
                    var site = item.Location.Site!;
                    await addressGate.WaitAsync(ct);
                    try
                    {
                        var result = await samsara.UpsertAddressAsync(
                            new SamsaraAddressRequest(
                                site.Id,
                                site.ExternalCode,
                                site.DriverTextName ?? site.Name,
                                item.Location.Address,
                                item.Location.Latitude,
                                item.Location.Longitude,
                                site.GeofenceRadiusMetres ?? samsara.StopRadiusMeters),
                            ct);
                        return new AddressSyncResult(site.Id, result.AddressId);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(
                            exception,
                            "Samsara reusable address sync failed for site {SiteId}; route stop will use a single-use location.",
                            site.Id);
                        return new AddressSyncResult(site.Id, null);
                    }
                    finally
                    {
                        addressGate.Release();
                    }
                }));

                foreach (var addressResult in addressResults)
                {
                    if (!string.IsNullOrWhiteSpace(addressResult.AddressId))
                    {
                        samsaraAddressBySiteId[addressResult.SiteId] = addressResult.AddressId;
                        await SaveMappingAsync(
                            "Site",
                            addressResult.SiteId,
                            addressResult.AddressId,
                            resolvedStops.First(item => item.Location.Site?.Id == addressResult.SiteId).Location.Site!.Name,
                            ct);
                    }
                    else
                    {
                        addressFallbackBySiteId.Add(addressResult.SiteId);
                    }
                }
            }

            var samsaraStops = new List<SamsaraRouteStopRequest>();
            var missingLocations = new List<string>();
            var missingSchedule = new List<string>();
            var addressFallbacks = new List<string>();
            var departFirstStop = !string.Equals(options.RouteStartingCondition, "arriveFirstStop", StringComparison.OrdinalIgnoreCase);
            var departLastStop = !string.Equals(options.RouteCompletionCondition, "arriveLastStop", StringComparison.OrdinalIgnoreCase);

            foreach (var resolvedStop in resolvedStops)
            {
                var stop = resolvedStop.Stop;
                var index = resolvedStop.Index;
                var isFirst = index == 0;
                var isLast = index == orderedStops.Count - 1;
                if (!isFirst && stop.PlannedArrivalUtc is null)
                {
                    missingSchedule.Add(stop.Name);
                    continue;
                }
                var resolved = resolvedStop.Location;
                if (resolved.Latitude is null || resolved.Longitude is null)
                {
                    missingLocations.Add(stop.Name);
                    continue;
                }

                string? samsaraAddressId = null;
                if (samsara.AddressSyncEnabled && resolved.Site is not null)
                {
                    samsaraAddressBySiteId.TryGetValue(resolved.Site.Id, out samsaraAddressId);
                    if (addressFallbackBySiteId.Contains(resolved.Site.Id))
                        addressFallbacks.Add(stop.Name);
                }

                orders.TryGetValue(stop.OrderId ?? Guid.Empty, out var order);
                var stopNotes = BuildStopNotes(stop, order);
                var scheduledArrival = isFirst && departFirstStop
                    ? null
                    : options.RecomputeScheduledTimes
                        ? null
                        : stop.PlannedArrivalUtc ?? firstScheduled;
                var scheduledDeparture = isFirst && departFirstStop
                    ? stop.PlannedArrivalUtc ?? firstScheduled
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
                    resolved.Site?.GeofenceRadiusMetres ?? samsara.StopRadiusMeters,
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

            if (!options.RecomputeScheduledTimes && missingSchedule.Count > 0)
                return BadRequest(new
                {
                    message = $"Every Samsara stop after the route start needs a planned arrival time because SLH TMS owns the schedule. Complete the plan for: {string.Join(", ", missingSchedule.Distinct(StringComparer.OrdinalIgnoreCase))}.",
                    missingStops = missingSchedule
                });

            // Mapping pre-sync normally means these are local lookups only. Keep them
            // sequential because EF Core does not permit concurrent operations on one DbContext.
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
                trailer is null ? null : $"Trailer: {trailer.TrailerNumber}",
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
                allocatedTrailer = trailer?.TrailerNumber,
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
        var match = MatchVehicle(vehicle, vehicles);
        if (match is null) return null;
        await SaveMappingAsync("Vehicle", vehicle.Id, match.Id, vehicle.Registration, ct);
        return match.Id;
    }

    private async Task<string?> ResolveDriverIdAsync(Driver driver, CancellationToken ct)
    {
        var mapped = await ExistingMappingAsync("Driver", driver.Id, ct);
        if (!string.IsNullOrWhiteSpace(mapped)) return mapped;

        var drivers = await samsara.GetDriversAsync(ct);
        var match = MatchDriver(driver, drivers);
        if (match is null) return null;
        await SaveMappingAsync("Driver", driver.Id, match.Id, match.Username ?? match.Name ?? driver.DisplayName, ct);
        return match.Id;
    }

    private static SamsaraVehicle? MatchVehicle(Vehicle vehicle, IReadOnlyList<SamsaraVehicle> vehicles)
    {
        var registration = Normalise(vehicle.Registration);
        var vin = Normalise(vehicle.VIN);
        var matches = vehicles.Where(item =>
                (!string.IsNullOrWhiteSpace(registration) &&
                    (Normalise(item.LicensePlate) == registration || Normalise(item.Name) == registration)) ||
                (!string.IsNullOrWhiteSpace(vin) && Normalise(item.Vin) == vin))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static SamsaraDriver? MatchDriver(Driver driver, IReadOnlyList<SamsaraDriver> drivers)
    {
        var employee = Normalise(driver.EmployeeNumber);
        var name = Normalise(driver.DisplayName);
        var matches = drivers.Where(item =>
                (!string.IsNullOrWhiteSpace(employee) &&
                 item.ExternalIds.Values.Any(value => Normalise(value) == employee)) ||
                (!string.IsNullOrWhiteSpace(name) && Normalise(item.Name) == name))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
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

    private async Task<List<SiteAddressCandidate>> ReadSiteAddressCandidatesAsync(CancellationToken ct)
    {
        var sites = await ReadEnrichedSitesAsync(ct);
        return ToSiteAddressCandidates(sites);
    }

    private async Task<List<Site>> ReadEnrichedSitesAsync(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking()
            .Where(site => site.Active)
            .OrderBy(site => site.Name)
            .Take(5000)
            .ToListAsync(ct);
        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Site Master coordinate enrichment was unavailable during Samsara address synchronisation.");
        }

        return sites;
    }

    private static List<SiteAddressCandidate> ToSiteAddressCandidates(IEnumerable<Site> sites) => sites
            .Where(site => !string.IsNullOrWhiteSpace(site.ExternalCode) &&
                          !string.IsNullOrWhiteSpace(site.CollectionAddress) &&
                          site.Latitude is not null &&
                          site.Longitude is not null)
            .Select(site => new SiteAddressCandidate(site))
            .ToList();

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

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Contains('"')) text = text.Replace("\"", "\"\"");
        return text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? $"\"{text}\"" : text;
    }

    private static string FormatCsvTime(DateTimeOffset? value)
    {
        if (value is null) return string.Empty;
        try
        {
            var uk = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            return TimeZoneInfo.ConvertTime(value.Value, uk).ToString("dd/MM/yyyy HH:mm");
        }
        catch (TimeZoneNotFoundException)
        {
            return value.Value.ToString("dd/MM/yyyy HH:mm");
        }
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

    private sealed record SamsaraStopProgressEnvelope(
        Guid StopId,
        string? Label,
        DateTimeOffset UpdatedAtUtc,
        SamsaraStopProgressState? Progress);

    private sealed record ResolvedDispatchStop(
        LoadStop Stop,
        int Index,
        ResolvedStopLocation Location);

    private sealed record AddressSyncResult(
        Guid SiteId,
        string? AddressId);

    private sealed record SiteAddressCandidate(Site Site);

    private sealed record SiteAddressSyncResult(
        Site Site,
        SamsaraAddressUpsertResult? Upsert,
        string? Error);

    private sealed record ResolvedStopLocation(string Address, double? Latitude, double? Longitude, Site? Site);
}
