using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/integrations/fleetio")]
[Authorize]
public sealed class FleetioResilientSyncController(
    FleetioClient fleetioClient,
    TmsDbContext db,
    ILogger<FleetioResilientSyncController> logger) : ControllerBase
{
    internal const string MappingUnavailableWarning =
        "Integration Mappings is temporarily unavailable. Fleetio assets were still synced using deterministic registration and SLH trailer-number matching; Fleetio-supplied identity, status and compliance fields were applied while TMS-only fields were preserved where Fleetio supplied no value.";

    private sealed record DesiredMapping(string ExternalKey, string ExternalLabel, string EntityType, Guid EntityId);

    [HttpPost("sync-assets-resilient")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncAssetsResilient(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
        {
            return BadRequest(new
            {
                configured = false,
                missingSettings = fleetioClient.MissingSettings,
                message = $"Fleetio cannot sync until these settings are complete: {string.Join(", ", fleetioClient.MissingSettings)}."
            });
        }

        try
        {
            // Pull due-date enrichment as part of the same master sync so Fleetio is the
            // authoritative source for the compliance fields displayed against vehicles.
            var assets = await fleetioClient.GetVehiclesAsync(100, includeDueDates: true, ct);
            var trailerAssets = assets.Where(IsTrailer).ToList();
            var vehicleAssets = assets.Where(asset => !IsTrailer(asset)).ToList();
            var vehicles = await db.Set<Vehicle>().ToListAsync(ct);
            var trailers = await db.Set<Trailer>().ToListAsync(ct);

            var (matchingMappings, mappingsAvailable) = await TryReadMappings(ct);
            var desiredMappings = new List<DesiredMapping>();

            var vehiclesUpdated = 0;
            var vehiclesCreated = 0;
            var trailersUpdated = 0;
            var trailersCreated = 0;
            var trailersCapacityRequired = 0;
            var trailerAliasesCanonicalised = 0;
            var trailerDuplicatesMerged = 0;
            var skipped = 0;

            // Canonicalise every active numeric trailer identity before matching Fleetio.
            // 02, 2, SLH02, SLH2, Trailer 02 and TRL-02 are all the same operational asset: SLH2.
            var canonicalGroups = trailers
                .Where(item => item.Active && TryTrailerIndex(item.TrailerNumber, out _))
                .GroupBy(item => CanonicalTrailerNumber(item.TrailerNumber)!, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var group in canonicalGroups)
            {
                var canonicalNumber = group.Key;
                var candidates = group.ToList();
                var keeper = candidates.FirstOrDefault(item => string.Equals(item.TrailerNumber.Trim(), canonicalNumber, StringComparison.OrdinalIgnoreCase))
                    ?? candidates.FirstOrDefault(item => item.TrailerNumber.Trim().StartsWith("SLH", StringComparison.OrdinalIgnoreCase))
                    ?? candidates[0];

                if (!string.Equals(keeper.TrailerNumber.Trim(), canonicalNumber, StringComparison.OrdinalIgnoreCase))
                {
                    keeper.TrailerNumber = canonicalNumber;
                    trailerAliasesCanonicalised++;
                }

                foreach (var duplicate in candidates.Where(item => item.Id != keeper.Id))
                {
                    await MergeTrailerInto(duplicate, keeper, matchingMappings, ct);
                    trailerDuplicatesMerged++;
                }
            }

            foreach (var asset in vehicleAssets)
            {
                var registration = BestVehicleRegistration(asset);
                if (string.IsNullOrWhiteSpace(registration))
                {
                    skipped++;
                    continue;
                }

                var mappedId = MappingTarget(matchingMappings, asset.Id, "Vehicle");
                var registrationKey = Normalise(registration);
                var vehicle = mappedId is not null ? vehicles.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                vehicle ??= vehicles.FirstOrDefault(item => Normalise(item.Registration) == registrationKey);
                vehicle ??= vehicles.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.FleetioId) && string.Equals(item.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));

                if (vehicle is null)
                {
                    vehicle = new Vehicle
                    {
                        Registration = ClipRequired(registration, 20),
                        FleetNumber = Clip(asset.FleetNumber, 40),
                        FleetioId = Clip(asset.Id, 80),
                        FleetioName = Clip(asset.Name, 160),
                        FleetioStatus = Clip(asset.Status, 80),
                        FleetioVor = asset.Vor,
                        FleetioPmiDueUtc = asset.PmiDueUtc,
                        FleetioMotDueUtc = asset.MotDueUtc,
                        FleetioServiceStatus = Clip(asset.ServiceStatus, 80),
                        FleetioLastSyncedUtc = DateTimeOffset.UtcNow,
                        Active = true
                    };
                    db.Set<Vehicle>().Add(vehicle);
                    vehicles.Add(vehicle);
                    vehiclesCreated++;
                }
                else
                {
                    vehicle.Registration = ClipRequired(registration, 20);
                    vehicle.FleetioId = Clip(asset.Id, 80);
                    vehicle.FleetioName = Clip(asset.Name, 160);
                    vehicle.FleetioStatus = Clip(asset.Status, 80);
                    vehicle.FleetioVor = asset.Vor;
                    vehicle.FleetioPmiDueUtc = asset.PmiDueUtc;
                    vehicle.FleetioMotDueUtc = asset.MotDueUtc;
                    vehicle.FleetioServiceStatus = Clip(asset.ServiceStatus, 80);
                    vehicle.FleetioLastSyncedUtc = DateTimeOffset.UtcNow;
                    if (!string.IsNullOrWhiteSpace(asset.FleetNumber)) vehicle.FleetNumber = Clip(asset.FleetNumber, 40);
                    vehicle.Active = true;
                    vehiclesUpdated++;
                }

                desiredMappings.Add(new DesiredMapping(asset.Id, asset.Name ?? registration, "Vehicle", vehicle.Id));
            }

            foreach (var asset in trailerAssets)
            {
                var fleetioName = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var canonicalNumber = CanonicalTrailerNumber(fleetioName);
                var preferredTrailerNumber = canonicalNumber ?? (!string.IsNullOrWhiteSpace(fleetioName) ? fleetioName : cNumber);
                if (string.IsNullOrWhiteSpace(preferredTrailerNumber))
                {
                    skipped++;
                    continue;
                }

                var mappedId = MappingTarget(matchingMappings, asset.Id, "Trailer");
                var mappedMatch = mappedId is not null ? trailers.FirstOrDefault(item => item.Id == mappedId.Value && item.Active) : null;
                var canonicalMatch = !string.IsNullOrWhiteSpace(canonicalNumber)
                    ? trailers.FirstOrDefault(item => item.Active && string.Equals(CanonicalTrailerNumber(item.TrailerNumber), canonicalNumber, StringComparison.OrdinalIgnoreCase))
                    : null;
                var nameMatch = !string.IsNullOrWhiteSpace(fleetioName)
                    ? trailers.FirstOrDefault(item => item.Active && Normalise(item.TrailerNumber) == Normalise(fleetioName))
                    : null;
                var cNumberMatch = !string.IsNullOrWhiteSpace(cNumber)
                    ? trailers.FirstOrDefault(item => item.Active && Normalise(item.TrailerNumber) == Normalise(cNumber))
                    : null;

                var trailer = mappedMatch ?? canonicalMatch ?? nameMatch ?? cNumberMatch;

                if (trailer is null)
                {
                    // Fleetio does not hold operational pallet capacity. Create the linked
                    // TMS identity, but leave capacity unconfirmed for a planner to enter.
                    trailer = new Trailer
                    {
                        TrailerNumber = ClipRequired(preferredTrailerNumber, 40),
                        Type = Clip(asset.Type, 80),
                        StandardCapacity = null,
                        EuroCapacity = null,
                        Active = true
                    };
                    db.Set<Trailer>().Add(trailer);
                    trailers.Add(trailer);
                    trailersCreated++;
                }
                else
                {
                    // Fleetio can identify the same trailer by both the SLH number/name and
                    // a C-number registration. Merge every matching legacy row into one keeper
                    // so original capacities and load history are retained on the linked record.
                    foreach (var duplicate in new[] { mappedMatch, canonicalMatch, nameMatch, cNumberMatch }
                                 .Where(item => item is not null && item.Id != trailer.Id)
                                 .Cast<Trailer>()
                                 .DistinctBy(item => item.Id))
                    {
                        await MergeTrailerInto(duplicate, trailer, matchingMappings, ct);
                        trailerDuplicatesMerged++;
                    }

                    if (!string.IsNullOrWhiteSpace(canonicalNumber)) trailer.TrailerNumber = canonicalNumber;
                    trailer.Type = Clip(asset.Type, 80) ?? trailer.Type;
                    // Capacity is owned by the TMS. Never invent or overwrite it from Fleetio.
                    trailer.Active = true;
                    trailersUpdated++;
                }

                if (trailer.StandardCapacity is null || trailer.EuroCapacity is null)
                    trailersCapacityRequired++;

                desiredMappings.Add(new DesiredMapping(asset.Id, fleetioName ?? cNumber ?? preferredTrailerNumber, "Trailer", trailer.Id));
            }

            await db.SaveChangesAsync(ct);

            string? mappingWarning = null;
            var mappingsUpdated = 0;
            if (!mappingsAvailable)
            {
                mappingWarning = MappingUnavailableWarning;
            }
            else
            {
                try
                {
                    db.ChangeTracker.Clear();
                    var trackedMappings = await db.IntegrationMappings.Where(item => item.Provider == "Fleetio").ToListAsync(ct);
                    foreach (var wanted in desiredMappings)
                    {
                        var mapping = trackedMappings.FirstOrDefault(item => item.Active && string.Equals(item.ExternalKey, wanted.ExternalKey, StringComparison.OrdinalIgnoreCase) && string.Equals(item.TmsEntityType, wanted.EntityType, StringComparison.OrdinalIgnoreCase));
                        if (mapping is null)
                        {
                            mapping = new IntegrationMapping
                            {
                                Provider = "Fleetio",
                                ExternalKey = ClipRequired(wanted.ExternalKey, 200),
                                ExternalLabel = Clip(wanted.ExternalLabel, 200),
                                TmsEntityType = wanted.EntityType,
                                TmsEntityId = wanted.EntityId,
                                Active = true,
                                UpdatedBy = User.Identity?.Name ?? "Fleetio sync"
                            };
                            db.IntegrationMappings.Add(mapping);
                            trackedMappings.Add(mapping);
                        }
                        else
                        {
                            mapping.ExternalLabel = Clip(wanted.ExternalLabel, 200);
                            mapping.TmsEntityId = wanted.EntityId;
                            mapping.Active = true;
                            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
                            mapping.UpdatedBy = User.Identity?.Name ?? "Fleetio sync";
                        }
                        mappingsUpdated++;
                    }
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Fleetio assets synced but IntegrationMappings could not be persisted.");
                    mappingWarning = "Integration Mappings could not be persisted, but Fleetio asset data was synced using deterministic registration and SLH trailer-number matching.";
                }
            }

            var summary = $"Fleetio sync completed: {vehiclesUpdated} vehicle(s) updated, {vehiclesCreated} vehicle(s) created, {trailersUpdated} trailer(s) updated, {trailersCreated} trailer(s) created, {trailerAliasesCanonicalised} trailer alias(es) canonicalised to SLH numbers and {trailerDuplicatesMerged} duplicate trailer record(s) consolidated.";
            if (trailersCapacityRequired > 0)
                summary += $" {trailersCapacityRequired} linked trailer(s) still require Standard/Euro capacity confirmation in the TMS master.";
            if (!string.IsNullOrWhiteSpace(mappingWarning)) summary += $" {mappingWarning}";

            return Ok(new
            {
                configured = true,
                connected = true,
                sourceAssetCount = assets.Count,
                sourceVehicleCount = vehicleAssets.Count,
                sourceTrailerCount = trailerAssets.Count,
                vehiclesUpdated,
                vehiclesCreated,
                trailersUpdated,
                trailersCreated,
                trailersCapacityRequired,
                trailerAliasesCanonicalised,
                trailerDuplicatesMerged,
                skipped,
                mappingsUpdated,
                mappingWarning,
                syncedAtUtc = DateTimeOffset.UtcNow,
                message = summary
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio resilient asset sync failed before the operational master could be committed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                configured = true,
                connected = false,
                message = $"Fleetio asset sync failed before the TMS master could be saved: {exception.GetBaseException().Message}"
            });
        }
    }

    private async Task MergeTrailerInto(Trailer duplicate, Trailer keeper, List<IntegrationMapping> mappings, CancellationToken ct)
    {
        keeper.Type ??= duplicate.Type;
        keeper.StandardCapacity ??= duplicate.StandardCapacity;
        keeper.EuroCapacity ??= duplicate.EuroCapacity;
        keeper.Notes ??= duplicate.Notes;
        await ReassignTrailerLoads(duplicate.Id, keeper.Id, ct);
        duplicate.Active = false;

        foreach (var mapping in mappings.Where(item => item.Active && string.Equals(item.TmsEntityType, "Trailer", StringComparison.OrdinalIgnoreCase) && item.TmsEntityId == duplicate.Id))
            mapping.TmsEntityId = keeper.Id;
    }

    private async Task ReassignTrailerLoads(Guid fromTrailerId, Guid toTrailerId, CancellationToken ct)
    {
        try
        {
            var loads = await db.Loads.Where(load => load.TrailerId == fromTrailerId).ToListAsync(ct);
            foreach (var load in loads) load.TrailerId = toTrailerId;
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            var loads = await PlanningRegisterStore.ReadLoadsAsync(db, null, ct);
            foreach (var load in loads.Where(load => load.TrailerId == fromTrailerId))
            {
                load.TrailerId = toTrailerId;
                await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name ?? "Fleetio trailer consolidation", ct);
            }
        }
    }

    private async Task<(List<IntegrationMapping> Mappings, bool Available)> TryReadMappings(CancellationToken ct)
    {
        try
        {
            var repaired = await IntegrationMappingSchemaRepair.EnsureAsync(db, logger, ct);
            if (!repaired) return ([], false);
            return (await db.IntegrationMappings.AsNoTracking().Where(item => item.Active && item.Provider == "Fleetio").ToListAsync(ct), true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "IntegrationMappings unavailable during Fleetio sync; falling back to deterministic master identity matching.");
            return ([], false);
        }
    }

    private static Guid? MappingTarget(IEnumerable<IntegrationMapping> mappings, string fleetioId, string entityType) =>
        mappings.FirstOrDefault(item => string.Equals(item.ExternalKey, fleetioId, StringComparison.OrdinalIgnoreCase) && string.Equals(item.TmsEntityType, entityType, StringComparison.OrdinalIgnoreCase))?.TmsEntityId;

    private static bool IsTrailer(FleetioVehicle asset)
    {
        if (asset.Type?.Contains("Trailer", StringComparison.OrdinalIgnoreCase) == true) return true;
        return !string.IsNullOrWhiteSpace(asset.Registration) && Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase);
    }

    private static string? CanonicalTrailerNumber(string? value)
    {
        if (!TryTrailerIndex(value, out var index)) return null;
        return $"SLH{index}";
    }

    private static bool TryTrailerIndex(string? value, out int index)
    {
        index = 0;
        var text = value?.Trim() ?? string.Empty;
        var match = Regex.Match(text, "^(?:(?:SLH|TRAILER|TRL)[\\s_-]*)?0*(\\d{1,3})(?:$|[\\s:_-])", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups[1].Value, out index) && index is >= 1 and <= 999;
    }

    private static string? BestVehicleRegistration(FleetioVehicle asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Registration) && !Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase)) return asset.Registration.Trim();
        if (!string.IsNullOrWhiteSpace(asset.Name) && LooksLikeUkRegistration(asset.Name)) return asset.Name.Trim();
        return null;
    }

    private static bool LooksLikeUkRegistration(string value)
    {
        var key = Normalise(value);
        return key.Length is >= 5 and <= 8 && key.Any(char.IsLetter) && key.Any(char.IsDigit);
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException || message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) || message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) || message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
    private static string ClipRequired(string value, int maxLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}