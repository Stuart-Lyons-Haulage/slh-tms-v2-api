Warning: truncated output (original token count: 20120)
Total output lines: 1618

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
    }…8120 tokens truncated…onTrailer,
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

        var available = (await dispatch.GetAvailableTimesAsync(new DispatchAvailableTimesRequest(load.PlanningDate, [deliveryDriver.Id]), ct, forceTachoRefresh: true)).Single();
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
