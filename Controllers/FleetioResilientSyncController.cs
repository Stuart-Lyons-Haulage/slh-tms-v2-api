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
    IntegrationSyncCoordinator coordinator,
    ILogger<FleetioResilientSyncController> logger) : ControllerBase
{
    internal const string MappingUnavailableWarning =
        "Integration Mappings is temporarily unavailable. Fleetio assets were still synced using deterministic registration and SLH trailer-number matching; Fleetio-supplied identity, status and compliance fields were applied while TMS-only fields were preserved where Fleetio supplied no value.";

    private sealed record DesiredMapping(string ExternalKey, string ExternalLabel, string EntityType, Guid EntityId);

    [HttpPost("sync-assets-resilient")]
    [Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> SyncAssetsResilient(CancellationToken ct)
    {
        // Backwards-compatible route: resilience now lives in the canonical coordinator
        // rather than a second database writer.
        var actor = User.Identity?.Name ?? "admin:fleetio-resilient";
        var result = await coordinator.SyncFleetioAsync(actor, ct);
        return result.Success ? Ok(result) : StatusCode(StatusCodes.Status503ServiceUnavailable, result);
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