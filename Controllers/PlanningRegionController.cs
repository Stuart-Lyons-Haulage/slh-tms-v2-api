using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/planning-control")]
[Authorize]
public sealed class PlanningRegionController(TmsDbContext db, ILogger<PlanningRegionController> logger) : ControllerBase
{
    [HttpGet("regions")]
    public async Task<IActionResult> Regions([FromQuery] DateOnly date, CancellationToken ct)
    {
        var degraded = false;
        var temperatureSync = new TemperatureSyncResult(0, 0, []);
        try
        {
            await SitePlanningProfileStore.SyncOrderProfilesAsync(db, date, ct);
            temperatureSync = await SitePlanningProfileStore.ApplyDailyRunTemperaturesAsync(db, date, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            degraded = true;
            logger.LogWarning(ex, "Planning region enrichment could not run; continuing with a basic destination list.");
        }

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<StagedImport> rows;
        try
        {
            rows = await db.StagedImports.AsNoTracking()
                .Where(x => (x.EntityType == "order" || x.EntityType == "register:order") && x.Status != StagingStatus.Rejected)
                .OrderByDescending(x => x.ReceivedAtUtc).Take(8000).ToListAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            logger.LogWarning(ex, "Planning region staging rows could not be read; returning an empty region map.");
            rows = [];
            degraded = true;
        }
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                if (DateOnly.TryParse(Text(root, "collectionDate"), out var collectionDate) && collectionDate != date) continue;
                var pallets = int.TryParse(Text(root, "pallets", "palletQty", "palletQuantity", "quantity"), out var parsed) ? parsed : 0;
                if (pallets <= 0) continue;
                var destination = Text(root, "deliveryLocation", "deliverySite", "delivery", "destination", "depot", "stallNumber");
                if (!string.IsNullOrWhiteSpace(destination)) destinations.Add(destination);
            }
            catch (JsonException) { }
        }

        Dictionary<string, PalletDestinationPresentation> presentation;
        try
        {
            presentation = await PalletDestinationPresentationStore.ResolveAsync(db, destinations, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            db.ChangeTracker.Clear();
            degraded = true;
            logger.LogWarning(ex, "Pallet Control Site Master presentation could not be resolved; using raw destination names.");
            presentation = destinations.ToDictionary(
                destination => destination,
                destination => new PalletDestinationPresentation("Other", destination, null, false),
                StringComparer.OrdinalIgnoreCase);
        }

        var map = destinations.ToDictionary(
            destination => destination,
            destination => presentation.GetValueOrDefault(destination)?.Region ?? "Other",
            StringComparer.OrdinalIgnoreCase);
        var labels = destinations.ToDictionary(
            destination => destination,
            destination => presentation.GetValueOrDefault(destination)?.DisplayName ?? destination,
            StringComparer.OrdinalIgnoreCase);
        var siteCodes = destinations.ToDictionary(
            destination => destination,
            destination => presentation.GetValueOrDefault(destination)?.SiteCode,
            StringComparer.OrdinalIgnoreCase);

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["North"] = 0,
            ["Midlands"] = 1,
            ["East"] = 2,
            ["London"] = 3,
            ["South East"] = 4,
            ["South West"] = 5,
            ["West / Wales"] = 6,
            ["Other"] = 7
        };
        var ordered = destinations
            .OrderBy(destination => rank.TryGetValue(map.GetValueOrDefault(destination, "Other"), out var value) ? value : 99)
            .ThenBy(destination => labels.GetValueOrDefault(destination, destination), StringComparer.OrdinalIgnoreCase)
            .ThenBy(destination => destination, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Ok(new
        {
            date,
            destinations = ordered,
            destinationRegions = map,
            destinationLabels = labels,
            destinationSiteCodes = siteCodes,
            unmatchedDestinations = presentation.Count(item => !item.Value.MasterMatched),
            temperatureConflicts = temperatureSync.Conflicts,
            temperatureUpdatedLoads = temperatureSync.UpdatedLoads,
            temperatureUpdatedOrders = temperatureSync.UpdatedOrders,
            degraded
        });
    }

    private static string? Text(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            foreach (var property in root.EnumerateObject())
                if (Normalise(property.Name) == Normalise(name))
                    return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : property.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? property.Value.ToString() : null;
        return null;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
