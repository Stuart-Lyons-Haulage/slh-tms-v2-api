using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Read-only diagnostics: identifies runs whose stops have no planned arrival time
/// and reports the route combinations that could not be matched to a Site Master
/// timing rule. Use the output to fill gaps in the Master Data workbook.
/// </summary>
[ApiController]
[Route("api/v1/planning-control/timing-gaps")]
[Authorize]
public sealed class TimingDiagnosticsController(TmsDbContext db, SiteTimingRuleStore timingRuleStore) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);

        // Load active runs (non-cancelled) for the target date, with stops.
        var loads = await db.Loads
            .AsNoTracking()
            .Include(load => load.Stops)
            .Where(load => load.PlanningDate == targetDate && load.Status != LoadStatus.Cancelled)
            .OrderBy(load => load.Reference)
            .ToListAsync(ct);

        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); } catch { /* route names remain usable */ }
        var rules = await timingRuleStore.ReadAsync(ct);

        var runsWithGaps = new List<object>();
        var missingRouteCombinations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var coveredRoutes = 0;
        var uncoveredRoutes = 0;

        foreach (var load in loads)
        {
            var orderedStops = load.Stops.OrderBy(stop => stop.Sequence).ToList();
            var stopsWithoutTime = orderedStops.Where(stop => stop.PlannedArrivalUtc is null).ToList();
            if (stopsWithoutTime.Count == 0) continue;

            // Identify collection → delivery pairs to find which rules are missing.
            string? currentCollection = null;
            var routeGaps = new List<object>();

            foreach (var stop in orderedStops)
            {
                if (stop.Name.StartsWith("Collect", StringComparison.OrdinalIgnoreCase))
                {
                    currentCollection = CleanStopName(stop.Name);
                    if (stop.PlannedArrivalUtc is null)
                        routeGaps.Add(new { stop = stop.Name, type = "collection", sequence = stop.Sequence });
                    continue;
                }
                if (stop.PlannedArrivalUtc is not null)
                {
                    // Has a time — rule matched or order had a delivery window.
                    coveredRoutes++;
                    continue;
                }

                var deliveryName = CleanStopName(stop.Name);
                var matchedRule = rules.FirstOrDefault(rule =>
                    SiteTimingRuleMatcher.Match(rule, currentCollection, deliveryName, null, sites));

                if (matchedRule is not null)
                {
                    // Rule matched but still no time — the rule itself may be missing
                    // the DepotDeadline / CollectFrom / CollectTo fields.
                    coveredRoutes++;
                    routeGaps.Add(new
                    {
                        stop = stop.Name,
                        type = "delivery",
                        sequence = stop.Sequence,
                        matchedRule = matchedRule.RouteCombination,
                        issue = "Rule matched but has no usable time fields",
                        depotDeadline = matchedRule.DepotDeadline,
                        collectFrom = matchedRule.CollectFrom,
                        collectTo = matchedRule.CollectTo,
                        lastDespatch = matchedRule.LastDespatch
                    });
                }
                else
                {
                    uncoveredRoutes++;
                    var routeKey = $"{currentCollection ?? "(no collection)"} → {deliveryName}";
                    missingRouteCombinations[routeKey] = missingRouteCombinations.GetValueOrDefault(routeKey) + 1;
                    routeGaps.Add(new
                    {
                        stop = stop.Name,
                        type = "delivery",
                        sequence = stop.Sequence,
                        collection = currentCollection,
                        issue = "No timing rule found in Master data"
                    });
                }
            }

            if (routeGaps.Count > 0)
            {
                runsWithGaps.Add(new
                {
                    loadId = load.Id,
                    reference = load.Reference,
                    status = load.Status.ToString(),
                    totalStops = orderedStops.Count,
                    stopsWithoutTime = stopsWithoutTime.Count,
                    gaps = routeGaps
                });
            }
        }

        return Ok(new
        {
            date = targetDate,
            totalRuns = loads.Count,
            runsWithTimingGaps = runsWithGaps.Count,
            totalTimingRules = rules.Count,
            routesCoveredByRules = coveredRoutes,
            routesNotCoveredByRules = uncoveredRoutes,
            missingRouteCombinations = missingRouteCombinations
                .OrderByDescending(pair => pair.Value)
                .Select(pair => new { route = pair.Key, occurrences = pair.Value })
                .ToList(),
            runs = runsWithGaps
        });
    }

    private static string CleanStopName(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        var separator = text.IndexOf('·');
        return separator >= 0 ? text[(separator + 1)..].Trim() : text;
    }
}
