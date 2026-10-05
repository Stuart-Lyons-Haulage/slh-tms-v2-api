using System.Text;
using System.Security.Cryptography;
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
                assetSyncEnabled = options.EnableAssetSync,
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
                assetSyncEnabled = options.EnableAssetSync,
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
                               (item.TmsEntityType == "Load" || item.TmsEntityType == "LoadRelay") &&
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

        // A local mapping is an audit record, not proof that the remote route still
        // exists. Verify mapped routes when Samsara is available so deleted or
        // rejected routes become eligible for a safe retry. Keep the provider calls
        // bounded and preserve the local view if an individual check is unavailable.
        var verifiedMappings = mappings;
        var staleRouteCount = 0;
        if (connected && mappings.Count > 0)
        {
            using var routeVerificationGate = new SemaphoreSlim(4, 4);
            var verification = await Task.WhenAll(mappings
                .GroupBy(item => new { item.TmsEntityType, item.TmsEntityId })
                .Select(async group =>
                {
                    var mapping = group.First();
                    var load = byId.GetValueOrDefault(mapping.TmsEntityId);
                    var suffix = mapping.TmsEntityType == "LoadRelay"
                        ? "delivery"
                        : load?.RelayPlan is { Enabled: true } ? "collection" : null;
                    await routeVerificationGate.WaitAsync(ct);
                    try
                    {
                        var route = await samsara.GetRouteByRunIdAsync(mapping.TmsEntityId, ct, suffix);
                        return (mapping.TmsEntityType, mapping.TmsEntityId, Exists: route is not null);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning(exception, "Samsara route verification failed for {EntityType} {EntityId}.", mapping.TmsEntityType, mapping.TmsEntityId);
                        return (mapping.TmsEntityType, mapping.TmsEntityId, Exists: true);
                    }
                    finally
                    {
                        routeVerificationGate.Release();
                    }
                }));

            var verificationByKey = verification.ToDictionary(item => (item.TmsEntityType, item.TmsEntityId));
            verifiedMappings = mappings
                .Where(item => !verificationByKey.TryGetValue((item.TmsEntityType, item.TmsEntityId), out var state) || state.Exists)
                .ToList();
            staleRouteCount = verification.Count(item => !item.Exists);
        }

        var rows = verifiedMappings
            .GroupBy(item => item.TmsEntityId)
            .Where(group =>
            {
                var load = byId.GetValueOrDefault(group.Key);
                return load?.RelayPlan is not { Enabled: true } || group.Any(item => item.TmsEntityType == "Load");
            })
            .Select(group =>
            {
                var item = group.FirstOrDefault(value => value.TmsEntityType == "Load") ?? group.First();
                var delivery = group.FirstOrDefault(value => value.TmsEntityType == "LoadRelay");
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
                    deliveryRouteId = delivery?.ExternalKey,
                    exportedAtUtc = group.Max(value => value.UpdatedAtUtc),
                    executionState = latest?.Envelope.Progress?.State,
                    executionOperation = latest?.Envelope.Progress?.Operation,
                    executionUpdatedAtUtc = latest?.Envelope.Progress?.OccurredAtUtc ?? latest?.Envelope.UpdatedAtUtc,
                    lastStopName = latest?.Name
                };
            })
            .OrderBy(item => item.reference)
            .ToList();

        return Ok(new
        {
            planningDate = date,
            configured = samsara.IsConfigured,
            connected,
            connectionMessage,
            remoteVerification = connected,
            staleRouteCount,
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

    [HttpPost("master-data/sync")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncMasterData(CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return BadRequest(new { message = $"Samsara settings are incomplete: {string.Join(", ", samsara.MissingSettings)}." });

        if (!samsara.AddressSyncEnabled && !options.EnableAssetSync)
            return BadRequest(new { message = "Both Samsara address and asset synchronisation are disabled in runtime configuration." });

        var sites = samsara.AddressSyncEnabled ? await ReadSiteAddressCandidatesAsync(ct) : [];
        var vehicles = options.EnableAssetSync
            ? await db.Vehicles.AsNoTracking().Where(item => item.Active).OrderBy(item => item.Registration).ToListAsync(ct)
            : [];
        var trailers = options.EnableAssetSync
            ? await db.Trailers.AsNoTracking().Where(item => item.Active).OrderBy(item => item.TrailerNumber).ToListAsync(ct)
            : [];

        var siteResults = new List<object>();
        foreach (var candidate in sites)
        {
            try
            {
                var result = await samsara.UpsertAddressAsync(new SamsaraAddressRequest(
                    candidate.Site.Id,
                    candidate.Site.ExternalCode,
                    candidate.Site.DriverTextName ?? candidate.Site.Name,
                    candidate.Site.CollectionAddress!,
                    (double)candidate.Site.Latitude!.Value,
                    (double)candidate.Site.Longitude!.Value,
                    candidate.Site.GeofenceRadiusMetres ?? samsara.StopRadiusMeters), ct);
                await SaveMappingAsync("Site", candidate.Site.Id, result.AddressId ?? result.ExternalId, candidate.Site.Name, ct);
                siteResults.Add(new { reference = candidate.Site.ExternalCode, status = result.Created ? "created" : "updated" });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Samsara Master Site sync failed for {SiteReference}.", candidate.Site.ExternalCode);
                siteResults.Add(new { reference = candidate.Site.ExternalCode, status = "failed", error = exception.GetBaseException().Message });
            }
        }

        var assetResults = new List<object>();
        var remoteVehicles = options.EnableAssetSync ? await samsara.GetAssetsAsync("vehicle", ct) : [];
        var remoteTrailers = options.EnableAssetSync ? await samsara.GetAssetsAsync("trailer", ct) : [];
        foreach (var vehicle in vehicles)
        {
            await SyncAsset("Vehicle", "vehicle", vehicle.Id.ToString("N"), vehicle.Registration, vehicle.Registration, vehicle.VIN, vehicle.Notes, remoteVehicles, assetResults, ct);
        }
        foreach (var trailer in trailers)
        {
            await SyncAsset("Trailer", "trailer", trailer.Id.ToString("N"), trailer.TrailerNumber, null, null, trailer.Notes, remoteTrailers, assetResults, ct);
        }

        return Ok(new
        {
            sites = new { eligible = sites.Count, succeeded = siteResults.Count(item => !item.ToString()!.Contains("failed", StringComparison.OrdinalIgnoreCase)), results = siteResults },
            assets = new { eligible = vehicles.Count + trailers.Count, succeeded = assetResults.Count(item => !item.ToString()!.Contains("failed", StringComparison.OrdinalIgnoreCase)), results = assetResults },
            message = "SLH Master Data synchronisation completed. Review any failed records before exporting routes."
        });

        async Task SyncAsset(string entityType, string type, string reference, string name, string? licensePlate, string? vin, string? notes, IReadOnlyList<SamsaraAsset> existingAssets, List<object> results, CancellationToken token)
        {
            try
            {
                var existing = existingAssets.FirstOrDefault(item =>
                    (item.ExternalIds.TryGetValue(options.AssetExternalIdKey, out var value) && string.Equals(value, reference, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(licensePlate) && string.Equals(item.LicensePlate, licensePlate, StringComparison.OrdinalIgnoreCase)) ||
                    (string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)));
                var result = await samsara.UpsertAssetAsync(new SamsaraAssetRequest(type, entityType, reference, name, licensePlate, vin, notes, options.AssetExternalIdKey), existing, lookupIfMissing: false, token);
                var entityId = Guid.ParseExact(reference, "N");
                await SaveMappingAsync(entityType, entityId, result.Id ?? result.ExternalId, name, token);
                results.Add(new { entityType, reference = name, status = result.Created ? "created" : "updated" });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "Samsara {EntityType} asset sync failed for {Name}.", entityType, name);
                results.Add(new { entityType, reference = name, status = "failed", error = exception.GetBaseException().Message });
            }
        }
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
            var stopNotes = BuildStopNotes(stop, order, options.DefaultStopDwellMinutes);
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
                : available.AvailableFrom.Value;
            var legalStart = available.AvailableFrom.Value;
            var rebasedStart = DispatchTachoRules.RebasePlannedStart(firstScheduled, legalStart);
            if (state?.DriverId != driver.Id || state.PlannedStartUtc is null || rebasedStart != state.PlannedStartUtc)
            {
                state = await DriverDispatchStateStore.SetPlannedStartAsync(
                    db, load.Id, rebasedStart, User.Identity?.Name, ct,
                    rebasedStart != firstScheduled
                        ? "Rebased on resend to TachoMaster legal start"
                        : available.RequiredRestPeriod == 9 ? "Calculated from TachoMaster · reduced 9h daily rest" : "Calculated from TachoMaster · regular 11h daily rest",
                    driver.Id,
                    state?.UseReducedDailyRest == true);
            }
            firstScheduled = rebasedStart;

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
            var physicalStops = SamsaraPhysicalStopGrouping.GroupAdjacent(resolvedStops.Select(item =>
                new SamsaraPhysicalStopCandidate(
                    item.Stop.Id,
                    item.Stop.Name,
                    item.Location.Site?.Id,
                    item.Location.Address,
                    item.Location.Latitude,
                    item.Location.Longitude,
                    item.Stop.PlannedArrivalUtc,
                    OperationalStopOrdering.IsCollection(item.Stop.Name),
                    item.Stop.OrderId,
                    item.Stop.PlannerNote)));

            var mappedSiteIds = physicalStops
                .Where(item => item.Representative.SiteId is not null)
                .Select(item => item.Representative.SiteId!.Value)
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
                var sitesToSync = physicalStops
                    .Where(item => item.Representative.SiteId is not null &&
                                   item.Representative.Latitude is not null &&
                                   item.Representative.Longitude is not null &&
                                   !samsaraAddressBySiteId.ContainsKey(item.Representative.SiteId.Value))
                    .GroupBy(item => item.Representative.SiteId!.Value)
                    .Select(group => group.First())
                    .ToList();

                var addressResults = await Task.WhenAll(sitesToSync.Select(async item =>
                {
                    var site = sites.Single(site => site.Id == item.Representative.SiteId!.Value);
                    await addressGate.WaitAsync(ct);
                    try
                    {
                        var result = await samsara.UpsertAddressAsync(
                            new SamsaraAddressRequest(
                                site.Id,
                                site.ExternalCode,
                                site.DriverTextName ?? site.Name,
                                item.Representative.Address!,
                                item.Representative.Latitude!.Value,
                                item.Representative.Longitude!.Value,
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
                            sites.Single(item => item.Id == addressResult.SiteId).Name,
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

            foreach (var physicalStop in physicalStops.Select((item, index) => (Group: item, Index: index)))
            {
                var representative = physicalStop.Group.Representative;
                var stop = orderedStops.First(item => item.Id == representative.StopId);
                var representativeSite = representative.SiteId is Guid representativeSiteId
                    ? sites.SingleOrDefault(item => item.Id == representativeSiteId)
                    : null;
                var physicalStopName = representativeSite?.DriverTextName ?? representativeSite?.Name ?? CleanStopName(stop.Name);
                var index = physicalStop.Index;
                var isFirst = index == 0;
                var isLast = index == physicalStops.Count - 1;
                var plannedArrival = physicalStop.Group.EarliestPlannedArrivalUtc;
                if (!options.RecomputeScheduledTimes && !isFirst && plannedArrival is null)
                {
                    missingSchedule.Add(physicalStopName);
                    continue;
                }
                if (representative.Latitude is null || representative.Longitude is null)
                {
                    missingLocations.Add(physicalStopName);
                    continue;
                }

                string? samsaraAddressId = null;
                if (samsara.AddressSyncEnabled && representative.SiteId is Guid siteId)
                {
                    samsaraAddressBySiteId.TryGetValue(siteId, out samsaraAddressId);
                    if (addressFallbackBySiteId.Contains(siteId))
                        addressFallbacks.Add(physicalStopName);
                }

                var stopNotes = BuildStopNotes(physicalStop.Group, orderedStops, orders, options.DefaultStopDwellMinutes);
                DateTimeOffset? scheduledArrival = isFirst && departFirstStop
                    ? null
                    : options.RecomputeScheduledTimes
                        ? null
                        : plannedArrival ?? firstScheduled;
                var earliestFirstDeparture = firstScheduled.AddMinutes(Math.Max(0, options.WalkaroundMinutes));
                var scheduledDeparture = isFirst && departFirstStop
                    ? plannedArrival is DateTimeOffset plannedFirstDeparture && plannedFirstDeparture > earliestFirstDeparture
                        ? plannedFirstDeparture
                        : earliestFirstDeparture
                    : isLast && departLastStop
                        ? plannedArrival
                        : null;

                samsaraStops.Add(new SamsaraRouteStopRequest(
                    representative.StopId,
                    index + 1,
                    physicalStopName,
                    samsaraAddressId,
                    representative.Address ?? CleanStopName(stop.Name),
                    representative.Latitude.Value,
                    representative.Longitude.Value,
                    representativeSite is not null
                        ? representativeSite.GeofenceRadiusMetres ?? samsara.StopRadiusMeters
                        : samsara.StopRadiusMeters,
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

            // Validate the exact payload that will be sent, not only the source
            // register. A stale or partially-recovered planning copy can contain
            // stops that are later excluded while resolving coordinates/times. Do
            // not allow that to become Samsara's opaque `stops: []` 400 response.
            if (samsaraStops.Count < 2)
            {
                var payloadStopNames = samsaraStops.Select(stop => stop.Name).ToList();
                logger.LogWarning(
                    "Samsara export blocked for run {RunId} ({Reference}): source stops {SourceStopCount}, payload stops {PayloadStopCount} ({PayloadStopNames}).",
                    load.Id,
                    load.Reference,
                    orderedStops.Count,
                    samsaraStops.Count,
                    string.Join(", ", payloadStopNames));

                return BadRequest(new
                {
                    message = $"Samsara export was stopped because {load.Reference} produced only {samsaraStops.Count} usable stop(s). At least two planned stops are required.",
                    runId = load.Id,
                    sourceStopCount = orderedStops.Count,
                    payloadStopCount = samsaraStops.Count,
                    payloadStops = payloadStopNames
                });
            }

            if (load.RelayPlan is { Enabled: true } relay)
                return await DispatchRelayAsync(load, relay, vehicle, driver, trailer, state, orderedStops, samsaraStops, sites, ct);

            // Mapping pre-sync normally means these are local lookups only. Keep them
            // sequential because EF Core does not permit concurrent operations on one DbContext.
            var samsaraDriverId = driver is null ? null : await ResolveDriverIdAsync(driver, ct);
            var samsaraVehicleId = await ResolveVehicleIdAsync(vehicle, ct);

            var notes = string.Join("\n", new[]
            {
                $"SLH TMS run {load.Reference}",
                $"Planning date: {load.PlanningDate:yyyy-MM-dd}",
                "Planning authority: SLH TMS",
                driver is null ? null : $"Driver: {driver.DisplayName}",
                $"Vehicle: {vehicle.Registration}",
                state?.PlannedStartUtc is null ? null : $"Planned yard start: {state.PlannedStartUtc:O}",
                $"Walkaround/sign-on allowance: {Math.Max(0, options.WalkaroundMinutes)} minutes",
                $"Default site dwell/wait allowance: {Math.Max(0, options.DefaultStopDwellMinutes)} minutes",
                trailer is null ? null : $"Trailer: {trailer.TrailerNumber}",
                string.IsNullOrWhiteSpace(load.PlannerNotes) ? null : $"Planner: {load.PlannerNotes}"
            }.Where(line => !string.IsNullOrWhiteSpace(line)));

            var request = new SamsaraRouteRequest(
                load.Id,
                SamsaraRouteName(load),
                notes,
                samsaraDriverId,
                string.IsNullOrWhiteSpace(samsaraDriverId) ? samsaraVehicleId : null,
                samsaraStops);

            var result = await samsara.UpsertRouteAsync(request, ct);
            await DeactivateMappingsAsync(runId, ct, orderedStops.Select(stop => stop.Id).ToList());
            await SaveMappingAsync("Load", load.Id, result.RouteId ?? result.ExternalId, load.Reference, ct);

            if (result.Route is not null)
            {
                foreach (var remoteStop in result.Route.Stops)
                {
                    var tmsStopId = ResolveTmsStopId(remoteStop);
                    if (tmsStopId is null || string.IsNullOrWhiteSpace(remoteStop.Id)) continue;

                    var group = physicalStops.FirstOrDefault(item => item.Representative.StopId == tmsStopId.Value);
                    // A physical Samsara stop can represent several local LoadStop rows.
                    // IntegrationMappings deliberately permits only one active mapping for
                    // a provider/external key/type, so persist the canonical representative
                    // rather than attempting to insert the same Samsara stop once per job.
                    if (group is not null)
                    {
                        var representative = group.Representative;
                        await SaveMappingAsync("LoadStop", representative.StopId, remoteStop.Id, representative.Name, ct);
                    }
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
                assignment = !string.IsNullOrWhiteSpace(samsaraDriverId)
                    ? "driver"
                    : !string.IsNullOrWhiteSpace(samsaraVehicleId) ? "vehicle" : "unallocated",
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
                    ? (string.IsNullOrWhiteSpace(samsaraDriverId) && string.IsNullOrWhiteSpace(samsaraVehicleId)
                        ? $"{load.Reference} was created in Samsara as an unallocated route from the SLH TMS plan."
                        : $"{load.Reference} was created in Samsara from the SLH TMS plan.")
                    : (string.IsNullOrWhiteSpace(samsaraDriverId) && string.IsNullOrWhiteSpace(samsaraVehicleId)
                        ? $"{load.Reference} already existed in Samsara and was updated as an unallocated route from the current SLH TMS plan rather than duplicated."
                        : $"{load.Reference} already existed in Samsara and was updated from the current SLH TMS plan rather than duplicated.")
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

    private async Task<IActionResult> DispatchRelayAsync(
        Load load,
        LoadRelayPlan relay,
        Vehicle collectionVehicle,
        Driver collectionDriver,
        Trailer? collectionTrailer,
        DriverDispatchState? collectionState,
        IReadOnlyList<LoadStop> orderedStops,
        IReadOnlyList<SamsaraRouteStopRequest> routeStops,
        IReadOnlyList<Site> sites,
        CancellationToken ct)
    {
        if (relay.DeliveryDriverId is not Guid deliveryDriverId ||
            relay.DeliveryVehicleId is not Guid deliveryVehicleId ||
            relay.DeliveryTrailerId is not Guid deliveryTrailerId)
            return BadRequest(new { message = "Relay runs require a delivery driver, delivery vehicle and replacement trailer before Samsara export." });

        var deliveryDriver = await db.Drivers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryDriverId && item.Active, ct);
        var deliveryVehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryVehicleId && item.Active, ct);
        var deliveryTrailer = await db.Trailers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == deliveryTrailerId && item.Active, ct);
        if (deliveryDriver is null || deliveryVehicle is null || deliveryTrailer is null)
            return BadRequest(new { message = "The relay delivery resources must all be active Master Data records." });

        var available = (await dispatch.GetAvailableTimesAsync(new DispatchAvailableTimesRequest(load.PlanningDate, [deliveryDriver.Id]), ct)).Single();
        if (available.AvailableFrom is null || !string.IsNullOrWhiteSpace(available.BreachDetail))
            return BadRequest(new { message = available.BreachDetail ?? "A legal TachoMaster start cannot be calculated for the delivery driver." });

        var handoverSite = relay.HandoverSiteId is Guid handoverSiteId
            ? sites.FirstOrDefault(site => site.Id == handoverSiteId)
            : sites.FirstOrDefault(site => SameSite(site, relay.HandoverSite));
        if (handoverSite is null || handoverSite.Latitude is null || handoverSite.Longitude is null)
            return BadRequest(new { message = $"The relay handover site '{relay.HandoverSite}' must be an active Site Master location with coordinates." });

        var split = relay.HandoverAfterStopSequence is int requestedSplit
            ? requestedSplit
            : Math.Max(1, routeStops.Count - 1);
        split = Math.Clamp(split, 1, routeStops.Count - 1);
        var handoverTime = relay.PlannedHandoverUtc
            ?? routeStops[Math.Min(split - 1, routeStops.Count - 1)].ScheduledDepartureTime
            ?? routeStops[split].ScheduledArrivalTime;
        if (handoverTime is DateTimeOffset plannedHandover && plannedHandover < available.AvailableFrom.Value)
            return BadRequest(new { message = $"The planned relay handover is before {deliveryDriver.DisplayName}'s legal Tacho start." });

        var handoverId = StableRelayGuid(load.Id, "handover");
        var handoverStop = new SamsaraRouteStopRequest(
            handoverId,
            split + 1,
            handoverSite.DriverTextName ?? handoverSite.Name,
            null,
            handoverSite.CollectionAddress ?? handoverSite.Name,
            (double)handoverSite.Latitude.Value,
            (double)handoverSite.Longitude.Value,
            handoverSite.GeofenceRadiusMetres ?? samsara.StopRadiusMeters,
            handoverTime,
            handoverTime,
            $"Trailer swap / relay handover. Collection driver: {collectionDriver.DisplayName}. Delivery driver: {deliveryDriver.DisplayName}. Trailer: {collectionTrailer?.TrailerNumber ?? "not allocated"} → {deliveryTrailer.TrailerNumber}.");

        var collectionStops = routeStops.Take(split).Append(handoverStop).Select((stop, index) => stop with { SequenceNumber = index + 1 }).ToList();
        var deliveryStops = new[] { handoverStop }.Concat(routeStops.Skip(split)).Select((stop, index) => stop with { SequenceNumber = index + 1 }).ToList();
        var collectionSamsaraDriverId = await ResolveDriverIdAsync(collectionDriver, ct);
        var deliverySamsaraDriverId = await ResolveDriverIdAsync(deliveryDriver, ct);
        var collectionNotes = RelayNotes(load, collectionDriver, collectionVehicle, collectionTrailer, deliveryDriver, deliveryVehicle, deliveryTrailer, "collection", collectionState?.PlannedStartUtc);
        var deliveryNotes = RelayNotes(load, deliveryDriver, deliveryVehicle, deliveryTrailer, collectionDriver, collectionVehicle, collectionTrailer, "delivery", handoverTime ?? available.AvailableFrom);

        var collectionResult = await samsara.UpsertRouteAsync(new SamsaraRouteRequest(
            load.Id,
            $"{SamsaraRouteName(load)} · Collection",
            collectionNotes,
            collectionSamsaraDriverId,
            string.IsNullOrWhiteSpace(collectionSamsaraDriverId) ? await ResolveVehicleIdAsync(collectionVehicle, ct) : null,
            collectionStops,
            "collection"), ct);
        var deliveryResult = await samsara.UpsertRouteAsync(new SamsaraRouteRequest(
            load.Id,
            $"{SamsaraRouteName(load)} · Delivery",
            deliveryNotes,
            deliverySamsaraDriverId,
            string.IsNullOrWhiteSpace(deliverySamsaraDriverId) ? await ResolveVehicleIdAsync(deliveryVehicle, ct) : null,
            deliveryStops,
            "delivery"), ct);

        await DeactivateMappingsAsync(load.Id, ct, orderedStops.Select(stop => stop.Id).ToList());
        await SaveMappingAsync("Load", load.Id, collectionResult.RouteId ?? collectionResult.ExternalId, $"{load.Reference} · Collection leg", ct);
        await SaveMappingAsync("LoadRelay", load.Id, deliveryResult.RouteId ?? deliveryResult.ExternalId, $"{load.Reference} · Delivery leg", ct);

        return Ok(new
        {
            success = true,
            runId = load.Id,
            reference = load.Reference,
            relay = true,
            handoverSite = handoverSite.DriverTextName ?? handoverSite.Name,
            collectionRouteId = collectionResult.RouteId,
            deliveryRouteId = deliveryResult.RouteId,
            collectionDriver = collectionDriver.DisplayName,
            deliveryDriver = deliveryDriver.DisplayName,
            collectionTrailer = collectionTrailer?.TrailerNumber,
            deliveryTrailer = deliveryTrailer.TrailerNumber,
            stopCount = collectionStops.Count + deliveryStops.Count - 2,
            message = $"{load.Reference} exported to Samsara as linked collection and delivery routes with a trailer handover at {handoverSite.DriverTextName ?? handoverSite.Name}."
        });
    }

    private static string RelayNotes(Load load, Driver legDriver, Vehicle legVehicle, Trailer? legTrailer, Driver otherDriver, Vehicle otherVehicle, Trailer? otherTrailer, string leg, DateTimeOffset? start) =>
        string.Join("\n", new[]
        {
            $"SLH TMS run {load.Reference} · relay {leg} leg",
            $"Planning date: {load.PlanningDate:yyyy-MM-dd}",
            $"Driver: {legDriver.DisplayName}",
            $"Vehicle: {legVehicle.Registration}",
            legTrailer is null ? null : $"Trailer: {legTrailer.TrailerNumber}",
            $"Relay counterpart: {otherDriver.DisplayName} / {otherVehicle.Registration}{(otherTrailer is null ? string.Empty : $" / {otherTrailer.TrailerNumber}")}",
            start is null ? null : $"Leg start / planned handover: {start:O}",
            string.IsNullOrWhiteSpace(load.PlannerNotes) ? null : $"Planner: {load.PlannerNotes}"
        }.Where(line => !string.IsNullOrWhiteSpace(line)));

    private static bool SameSite(Site site, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var target = NormaliseRelaySite(value);
        return new[] { site.Name, site.DriverTextName, site.ExternalCode }
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Any(item => NormaliseRelaySite(item) == target);
    }

    private static string NormaliseRelaySite(string? value) => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static Guid StableRelayGuid(Guid runId, string suffix)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"slh-relay:{runId:N}:{suffix}"));
        return new Guid(bytes);
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
            var load = await FindLoadAsync(runId, ct);
            var deleted = await samsara.DeleteRouteByRunIdAsync(runId, ct, load?.RelayPlan?.Enabled == true ? "collection" : null);
            var deliveryDeleted = false;
            if (load?.RelayPlan?.Enabled == true)
                deliveryDeleted = await samsara.DeleteRouteByRunIdAsync(runId, ct, "delivery");
            await DeactivateMappingsAsync(runId, ct);

            return Ok(new
            {
                success = true,
                runId,
                deleted = deleted || deliveryDeleted,
                collectionDeleted = deleted,
                deliveryDeleted,
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

    private async Task DeactivateMappingsAsync(Guid runId, CancellationToken ct, IReadOnlyCollection<Guid>? knownStopIds = null)
    {
        var stopIds = await db.LoadStops.AsNoTracking()
            .Where(stop => stop.LoadId == runId)
            .Select(stop => stop.Id)
            .ToListAsync(ct);
        if (knownStopIds is not null)
            stopIds = stopIds.Concat(knownStopIds).Distinct().ToList();

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

    private static string BuildStopNotes(LoadStop stop, TransportOrder? order, int defaultDwellMinutes) =>
        BuildStopNotes(
            new SamsaraPhysicalStopGroup([
                new SamsaraPhysicalStopCandidate(
                    stop.Id,
                    stop.Name,
                    null,
                    stop.Address,
                    stop.Latitude is null ? null : (double)stop.Latitude.Value,
                    stop.Longitude is null ? null : (double)stop.Longitude.Value,
                    stop.PlannedArrivalUtc,
                    OperationalStopOrdering.IsCollection(stop.Name),
                    stop.OrderId,
                    stop.PlannerNote)
            ]),
            [stop],
            order is null ? new Dictionary<Guid, TransportOrder>() : new Dictionary<Guid, TransportOrder> { [order.Id] = order },
            defaultDwellMinutes);

    private static string BuildStopNotes(
        SamsaraPhysicalStopGroup group,
        IReadOnlyCollection<LoadStop> orderedStops,
        IReadOnlyDictionary<Guid, TransportOrder> orders,
        int defaultDwellMinutes)
    {
        var representative = group.Representative;
        var lines = new List<string>
        {
            representative.Name,
            $"Default site dwell/wait: {Math.Max(0, defaultDwellMinutes)} minutes"
        };

        var seenOrders = new HashSet<Guid>();
        foreach (var member in group.Members)
        {
            var localStop = orderedStops.FirstOrDefault(stop => stop.Id == member.StopId);
            if (member.OrderId is Guid orderId && seenOrders.Add(orderId) && orders.TryGetValue(orderId, out var order))
            {
                lines.Add(string.Empty);
                lines.Add($"Job: {order.Reference} · {order.CustomerCode} · {order.Pallets?.ToString() ?? "pallet quantity not recorded"} pallets");
                if (order.DeliveryWindowStartUtc is not null || order.DeliveryWindowEndUtc is not null)
                    lines.Add($"Delivery window: {FormatWindow(order.DeliveryWindowStartUtc, order.DeliveryWindowEndUtc)}");
                if (!string.IsNullOrWhiteSpace(order.MarketName)) lines.Add($"Market: {order.MarketName}");
                if (!string.IsNullOrWhiteSpace(order.SellerName)) lines.Add($"Seller: {order.SellerName}");
                if (!string.IsNullOrWhiteSpace(order.StallNumber)) lines.Add($"Stand/Stall: {order.StallNumber}");
                if (!string.IsNullOrWhiteSpace(order.DriverInstructions)) lines.Add($"Instructions: {order.DriverInstructions}");
            }

            if ((member.OrderId is null || !orders.ContainsKey(member.OrderId.Value)) && !string.IsNullOrWhiteSpace(localStop?.Address))
                lines.Add($"Job manifest: {localStop.Address}");

            if (!string.IsNullOrWhiteSpace(localStop?.PlannerNote) && !lines.Contains($"Planner: {localStop.PlannerNote}"))
            {
                lines.Add($"Planner: {localStop.PlannerNote}");
            }
        }

        return SamsaraNoteRules.LimitJobNotes(string.Join("\n", lines));
    }

    private static string SamsaraRouteName(Load load)
    {
        var display = RunDisplayLabel.For(load);
        var number = new string(display.SkipWhile(character => !char.IsDigit(character)).TakeWhile(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(number) ? $"SLH {display}" : $"SLH Route {number}";
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
        var matchedSite = SamsaraStopSiteMatcher.FindSite(stop, sites);

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
