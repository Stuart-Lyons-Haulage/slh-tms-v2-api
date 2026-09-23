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
public sealed class FleetioAssetSyncController(
    FleetioClient fleetioClient,
    TmsDbContext db,
    ILogger<FleetioAssetSyncController> logger) : ControllerBase
{
    [HttpGet("asset-status")]
    public async Task<IActionResult> AssetStatus(CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return BadRequest(new { configured = false, missingSettings = fleetioClient.MissingSettings });

        try
        {
            var assets = await fleetioClient.GetVehiclesAsync(100, ct);
            var vehicles = await db.Set<Vehicle>().AsNoTracking().Where(v => v.Active).ToListAsync(ct);
            var trailers = await db.Set<Trailer>().AsNoTracking().Where(t => t.Active).ToListAsync(ct);
            var mappings = await SafeFleetioMappings(ct);

            var powered = assets.Where(asset => !IsTrailer(asset)).Select(asset =>
            {
                var mappedId = MappingTarget(mappings, asset.Id, "Vehicle");
                var registration = BestVehicleRegistration(asset);
                var match = mappedId is not null ? vehicles.FirstOrDefault(v => v.Id == mappedId.Value) : null;
                match ??= string.IsNullOrWhiteSpace(registration) ? null : vehicles.FirstOrDefault(v => Normalise(v.Registration) == Normalise(registration));
                match ??= vehicles.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v.FleetioId) && string.Equals(v.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));
                return new
                {
                    tmsVehicleId = match?.Id,
                    registration = match?.Registration ?? registration ?? asset.Name ?? asset.Id,
                    fleetNumber = match?.FleetNumber ?? asset.FleetNumber,
                    fleetioId = asset.Id,
                    fleetioName = asset.Name,
                    fleetioStatus = asset.Status,
                    vin = asset.Vin,
                    year = asset.Year,
                    make = asset.Make,
                    model = asset.Model,
                    trim = asset.Trim,
                    issuesCount = asset.IssuesCount,
                    workOrdersCount = asset.WorkOrdersCount,
                    primaryMeterValue = asset.PrimaryMeterValue,
                    primaryMeterUnit = asset.PrimaryMeterUnit,
                    pmiDueUtc = asset.PmiDueUtc,
                    motDueUtc = asset.MotDueUtc,
                    serviceStatus = asset.ServiceStatus,
                    matched = match is not null
                };
            }).OrderBy(x => x.registration).ToList();

            var trailerRows = assets.Where(IsTrailer).Select(asset =>
            {
                var mappedId = MappingTarget(mappings, asset.Id, "Trailer");
                var slh = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var mappedMatch = mappedId is not null ? trailers.FirstOrDefault(t => t.Id == mappedId.Value) : null;
                var nameMatch = !string.IsNullOrWhiteSpace(slh)
                    ? trailers.FirstOrDefault(t => Normalise(t.TrailerNumber) == Normalise(slh))
                    : null;
                var cMatch = !string.IsNullOrWhiteSpace(cNumber)
                    ? trailers.FirstOrDefault(t => Normalise(t.TrailerNumber) == Normalise(cNumber))
                    : null;
                var match = mappedMatch ?? nameMatch ?? cMatch;
                return new
                {
                    tmsTrailerId = match?.Id,
                    trailerNumber = match?.TrailerNumber ?? slh ?? cNumber ?? asset.Id,
                    fleetioCNumber = cNumber,
                    fleetioId = asset.Id,
                    fleetioName = slh,
                    fleetioStatus = asset.Status,
                    type = asset.Type,
                    vin = asset.Vin,
                    year = asset.Year,
                    make = asset.Make,
                    model = asset.Model,
                    trim = asset.Trim,
                    issuesCount = asset.IssuesCount,
                    workOrdersCount = asset.WorkOrdersCount,
                    pmiDueUtc = asset.PmiDueUtc,
                    motDueUtc = asset.MotDueUtc,
                    serviceStatus = asset.ServiceStatus,
                    matched = match is not null
                };
            }).OrderBy(x => x.trailerNumber).ToList();

            return Ok(new
            {
                configured = true,
                connected = true,
                retrievedAtUtc = DateTimeOffset.UtcNow,
                vehicles = powered,
                trailers = trailerRows
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio asset status failed.");
            return StatusCode(500, new { configured = true, connected = false, message = exception.GetBaseException().Message });
        }
    }

    [HttpGet("asset-maintenance/{fleetioId}")]
    public async Task<IActionResult> AssetMaintenance(string fleetioId, CancellationToken ct)
    {
        if (!fleetioClient.IsConfigured)
            return BadRequest(new { configured = false, missingSettings = fleetioClient.MissingSettings });

        try
        {
            var snapshot = await fleetioClient.GetMaintenanceSnapshotAsync(fleetioId, ct);
            return Ok(snapshot);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio maintenance detail failed for asset {FleetioId}.", fleetioId);
            return StatusCode(500, new { configured = true, connected = false, message = exception.GetBaseException().Message });
        }
    }

    [HttpPost("sync-assets")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncAssets(CancellationToken ct)
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
            var assets = await fleetioClient.GetVehiclesAsync(100, includeDueDates: false, ct);
            var trailerAssets = assets.Where(IsTrailer).ToList();
            var vehicleAssets = assets.Where(asset => !IsTrailer(asset)).ToList();

            var vehicles = await db.Set<Vehicle>().ToListAsync(ct);
            var trailers = await db.Set<Trailer>().ToListAsync(ct);
            var mappings = await SafeFleetioMappings(ct, tracked: true);

            var vehiclesUpdated = 0;
            var vehiclesCreated = 0;
            var trailersUpdated = 0;
            var trailersCreated = 0;
            var trailerDuplicatesMerged = 0;
            var skipped = 0;

            foreach (var asset in vehicleAssets)
            {
                var registration = BestVehicleRegistration(asset);
                if (string.IsNullOrWhiteSpace(registration))
                {
                    skipped++;
                    continue;
                }

                var mappedId = MappingTarget(mappings, asset.Id, "Vehicle");
                var registrationKey = Normalise(registration);
                var vehicle = mappedId is not null ? vehicles.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                vehicle ??= vehicles.FirstOrDefault(item => Normalise(item.Registration) == registrationKey);
                vehicle ??= vehicles.FirstOrDefault(item =>
                    !string.IsNullOrWhiteSpace(item.FleetioId) &&
                    string.Equals(item.FleetioId, asset.Id, StringComparison.OrdinalIgnoreCase));

                if (vehicle is null)
                {
                    vehicle = new Vehicle
                    {
                        Registration = ClipRequired(registration, 20),
                        FleetNumber = Clip(asset.FleetNumber, 40),
                        FleetioId = Clip(asset.Id, 80),
                        FleetioName = Clip(asset.Name, 160),
                        FleetioStatus = Clip(asset.Status, 80),
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
                    if (!string.IsNullOrWhiteSpace(asset.FleetNumber)) vehicle.FleetNumber = Clip(asset.FleetNumber, 40);
                    vehicle.Active = true;
                    vehiclesUpdated++;
                }

                vehicle.FleetioPmiDueUtc = asset.PmiDueUtc;
                vehicle.FleetioMotDueUtc = asset.MotDueUtc;
                vehicle.FleetioServiceStatus = asset.ServiceStatus;
                vehicle.FleetioLastSyncedUtc = DateTimeOffset.UtcNow;
                UpsertMapping(mappings, asset.Id, asset.Name ?? registration, "Vehicle", vehicle.Id);
            }

            foreach (var asset in trailerAssets)
            {
                var fleetioName = asset.Name?.Trim();
                var cNumber = asset.Registration?.Trim();
                var preferredTrailerNumber = !string.IsNullOrWhiteSpace(fleetioName) ? fleetioName : cNumber;
                if (string.IsNullOrWhiteSpace(preferredTrailerNumber))
                {
                    skipped++;
                    continue;
                }

                var mappedId = MappingTarget(mappings, asset.Id, "Trailer");
                var mappedMatch = mappedId is not null ? trailers.FirstOrDefault(item => item.Id == mappedId.Value) : null;
                var nameMatch = !string.IsNullOrWhiteSpace(fleetioName)
                    ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(fleetioName))
                    : null;
                var cMatch = !string.IsNullOrWhiteSpace(cNumber)
                    ? trailers.FirstOrDefault(item => Normalise(item.TrailerNumber) == Normalise(cNumber))
                    : null;
                var trailer = mappedMatch ?? nameMatch ?? cMatch;

                if (nameMatch is not null && cMatch is not null && nameMatch.Id != cMatch.Id)
                {
                    await ReassignTrailerLoads(cMatch.Id, nameMatch.Id, ct);
                    foreach (var mapping in mappings.Where(x => x.TmsEntityType == "Trailer" && x.TmsEntityId == cMatch.Id))
                    {
                        mapping.TmsEntityId = nameMatch.Id;
                        mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
                        mapping.UpdatedBy = User.Identity?.Name ?? "Fleetio sync";
                    }
                    cMatch.Active = false;
                    trailer = nameMatch;
                    trailerDuplicatesMerged++;
                }

                if (trailer is null)
                {
                    trailer = new Trailer
                    {
                        TrailerNumber = ClipRequired(preferredTrailerNumber, 40),
                        Type = Clip(asset.Type, 80),
                        Active = true
                    };
                    db.Set<Trailer>().Add(trailer);
                    trailers.Add(trailer);
                    trailersCreated++;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(fleetioName))
                        trailer.TrailerNumber = ClipRequired(fleetioName, 40);
                    trailer.Type = Clip(asset.Type, 80) ?? trailer.Type;
                    trailer.Active = true;
                    trailersUpdated++;
                }

                UpsertMapping(mappings, asset.Id, fleetioName ?? cNumber ?? preferredTrailerNumber, "Trailer", trailer.Id);
            }

            await db.SaveChangesAsync(ct);

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
                trailerDuplicatesMerged,
                skipped,
                syncedAtUtc = DateTimeOffset.UtcNow,
                message = $"Fleetio sync completed into the canonical TMS master: {vehiclesUpdated} vehicle(s) updated, {vehiclesCreated} vehicle(s) created, {trailersUpdated} trailer(s) updated, {trailersCreated} trailer(s) created and {trailerDuplicatesMerged} duplicate trailer identity record(s) consolidated."
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Fleetio full asset sync failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new
            {
                configured = true,
                connected = false,
                message = $"Fleetio asset sync failed: {exception.GetBaseException().Message}"
            });
        }
    }

    private async Task<List<IntegrationMapping>> SafeFleetioMappings(CancellationToken ct, bool tracked = false)
    {
        try
        {
            var query = db.IntegrationMappings.Where(x => x.Active && x.Provider == "Fleetio");
            return tracked ? await query.ToListAsync(ct) : await query.AsNoTracking().ToListAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleetio integration mappings are unavailable; identity matching will use registration/name fallbacks.");
            return [];
        }
    }

    private static Guid? MappingTarget(IEnumerable<IntegrationMapping> mappings, string fleetioId, string entityType) =>
        mappings.FirstOrDefault(x =>
            string.Equals(x.ExternalKey, fleetioId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.TmsEntityType, entityType, StringComparison.OrdinalIgnoreCase))?.TmsEntityId;

    private void UpsertMapping(List<IntegrationMapping> mappings, string fleetioId, string label, string entityType, Guid entityId)
    {
        var mapping = mappings.FirstOrDefault(x =>
            string.Equals(x.ExternalKey, fleetioId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.TmsEntityType, entityType, StringComparison.OrdinalIgnoreCase));
        if (mapping is null)
        {
            mapping = new IntegrationMapping
            {
                Provider = "Fleetio",
                ExternalKey = ClipRequired(fleetioId, 200),
                ExternalLabel = Clip(label, 200),
                TmsEntityType = entityType,
                TmsEntityId = entityId,
                Active = true,
                UpdatedBy = User.Identity?.Name ?? "Fleetio sync"
            };
            db.IntegrationMappings.Add(mapping);
            mappings.Add(mapping);
        }
        else
        {
            mapping.ExternalLabel = Clip(label, 200);
            mapping.TmsEntityId = entityId;
            mapping.Active = true;
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = User.Identity?.Name ?? "Fleetio sync";
        }
    }

    private async Task ReassignTrailerLoads(Guid fromTrailerId, Guid toTrailerId, CancellationToken ct)
    {
        try
        {
            var loadsUsingDuplicate = await db.Loads.Where(load => load.TrailerId == fromTrailerId).ToListAsync(ct);
            foreach (var load in loadsUsingDuplicate) load.TrailerId = toTrailerId;
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Dedicated Loads table unavailable while consolidating trailer {FromTrailer}; using planning register fallback.", fromTrailerId);
        }

        try
        {
            var registerLoads = await PlanningRegisterStore.ReadLoadsAsync(db, null, ct);
            foreach (var load in registerLoads.Where(load => load.TrailerId == fromTrailerId))
            {
                load.TrailerId = toTrailerId;
                await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name ?? "Fleetio trailer consolidation", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Planning register could not reassign loads from duplicate trailer {FromTrailer} to {ToTrailer}; the duplicate trailer will remain active for safety.", fromTrailerId, toTrailerId);
            throw;
        }
    }

    private static bool IsTrailer(FleetioVehicle asset)
    {
        if (asset.Type?.Contains("Trailer", StringComparison.OrdinalIgnoreCase) == true) return true;
        return !string.IsNullOrWhiteSpace(asset.Registration) && Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase);
    }

    private static string? BestVehicleRegistration(FleetioVehicle asset)
    {
        if (!string.IsNullOrWhiteSpace(asset.Registration) && !Regex.IsMatch(asset.Registration.Trim(), "^C\\d{5,}$", RegexOptions.IgnoreCase))
            return asset.Registration.Trim();
        if (!string.IsNullOrWhiteSpace(asset.Name) && LooksLikeUkRegistration(asset.Name)) return asset.Name.Trim();
        return null;
    }

    private static bool LooksLikeUkRegistration(string value)
    {
        var key = Normalise(value);
        return key.Length is >= 5 and <= 8 && key.Any(char.IsLetter) && key.Any(char.IsDigit);
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
