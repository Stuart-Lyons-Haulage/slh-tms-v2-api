using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/master-data/timing-rules")]
public sealed class SiteTimingRuleSeedController(TmsDbContext db, ILogger<SiteTimingRuleSeedController> logger) : ControllerBase
{
    /// <summary>
    /// Seeds all site timing rules from the embedded JSON data file.
    /// POST api/v1/master-data/timing-rules/seed?commit=true
    /// Without commit=true it previews what would be upserted.
    /// </summary>
    [HttpPost("seed")]
    public async Task<IActionResult> SeedTimingRules([FromQuery] bool commit = false, CancellationToken ct = default)
    {
        var assembly = typeof(SiteTimingRuleSeedController).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("site-timing-rules.json", StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            // Fall back to file on disk next to the assembly
            var assemblyDir = Path.GetDirectoryName(assembly.Location) ?? ".";
            var filePath = Path.Combine(assemblyDir, "Data", "SeedData", "site-timing-rules.json");
            if (!System.IO.File.Exists(filePath))
                return NotFound(new { code = "seed_file_missing", message = "site-timing-rules.json not found as embedded resource or on disk." });

            return await ProcessSeedFile(await System.IO.File.ReadAllTextAsync(filePath, ct), commit, ct);
        }

        await using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return await ProcessSeedFile(await reader.ReadToEndAsync(ct), commit, ct);
    }

    private async Task<IActionResult> ProcessSeedFile(string json, bool commit, CancellationToken ct)
    {
        var rules = JsonSerializer.Deserialize<List<SeedTimingRule>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (rules is null || rules.Count == 0)
            return BadRequest(new { code = "empty_seed", message = "No timing rules found in seed file." });

        var results = new List<object>();
        var upserted = 0;
        var skipped = 0;

        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.RouteCombination))
            {
                skipped++;
                continue;
            }

            var key = rule.Key ?? $"{Canonical(rule.CollectionContext)}:{Canonical(rule.RouteCombination)}:{Canonical(rule.PalletType)}";

            var payload = new
            {
                routeCombination = rule.RouteCombination,
                normalisedRouteKey = Canonical(rule.RouteCombination),
                collectionContext = rule.CollectionContext,
                collectionKey = rule.CollectionKey,
                deliveryKey = rule.DeliveryKey,
                palletType = rule.PalletType,
                lastDespatch = rule.LastDespatch,
                collectFrom = rule.CollectFrom,
                collectTo = rule.CollectTo,
                latestCollectionTime = rule.LatestCollectionTime ?? rule.CollectTo ?? rule.CollectFrom,
                depotDeadline = rule.DepotDeadline,
                dispatchPlanningMode = rule.DispatchPlanningMode ?? "Manual planned start may be earlier; latestCollectionTime is the wall-board risk/deadline until first geofence hit updates live ETO/ETA.",
                firstGeofenceResetsLiveEtos = rule.FirstGeofenceResetsLiveEtos
            };

            if (commit)
            {
                try
                {
                    var payloadJson = JsonSerializer.Serialize(payload);
                    var actor = User.Identity?.Name ?? User.FindFirst("preferred_username")?.Value ?? "seed";
                    await MasterDetailStore.SaveAsync(db, "sitetimingrule", key, payloadJson, "SLH dispatch timing seed", actor, ct);
                    upserted++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to seed timing rule {Route}", rule.RouteCombination);
                    results.Add(new { route = rule.RouteCombination, status = "failed", error = ex.Message });
                    continue;
                }
            }

            results.Add(new
            {
                route = rule.RouteCombination,
                context = rule.CollectionContext,
                palletType = rule.PalletType,
                status = commit ? "upserted" : "ready",
                hasTimings = !string.IsNullOrWhiteSpace(rule.LastDespatch) ||
                             !string.IsNullOrWhiteSpace(rule.CollectFrom) ||
                             !string.IsNullOrWhiteSpace(rule.CollectTo) ||
                             !string.IsNullOrWhiteSpace(rule.DepotDeadline)
            });
        }

        return Ok(new
        {
            total = rules.Count,
            upserted,
            skipped,
            commit,
            rules = results
        });
    }

    private static string Canonical(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private sealed record SeedTimingRule(
        string RouteCombination,
        string? NormalisedRouteKey,
        string? CollectionContext,
        string? CollectionKey,
        string? DeliveryKey,
        string? PalletType,
        string? LastDespatch,
        string? CollectFrom,
        string? CollectTo,
        string? LatestCollectionTime,
        string? DepotDeadline,
        string? DispatchPlanningMode,
        bool FirstGeofenceResetsLiveEtos = true,
        string? Key = null);
}
