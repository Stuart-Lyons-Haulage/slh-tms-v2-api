using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning")]
[Authorize]
public sealed class PlannerSiteReconciliationController(TmsDbContext db, ILogger<PlannerSiteReconciliationController> logger) : ControllerBase
{
    [HttpPost("reconcile-sites/{date:datetime}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> ReconcileSites(DateTime date, CancellationToken ct)
    {
        var planningDate = DateOnly.FromDateTime(date);
        var warnings = new List<string>();

        List<Site> sites;
        try
        {
            sites = await ReadSitesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Site Master could not be read while reconciling planner stops for {PlanningDate}.", planningDate);
            db.ChangeTracker.Clear();
            sites = [];
            warnings.Add("Site Master is temporarily unavailable; the planner import remains valid and no stop addresses were changed.");
        }

        if (sites.Count > 0)
        {
            try
            {
                await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Master-detail aliases are optional enrichment. Duplicate/legacy detail rows,
                // schema lag or other enrichment defects must never turn a successful planner
                // import into HTTP 500. Core Site name/code/address matching can continue safely.
                logger.LogWarning(ex, "Optional Site Master detail enrichment was skipped for {PlanningDate}.", planningDate);
                db.ChangeTracker.Clear();
                warnings.Add("Optional Site aliases/details could not be loaded; reconciliation continued using core Site Master names, codes and addresses.");
            }
        }

        List<Load> loads;
        var registerFallback = false;
        try
        {
            loads = await db.Loads.Include(x => x.Stops).Where(x => x.PlanningDate == planningDate).ToListAsync(ct);
        }
        catch (Exception ex) when (IsSchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            try
            {
                loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct);
                registerFallback = true;
                warnings.Add("Planning SQL schema is partially unavailable; Site reconciliation used the resilient planning register.");
            }
            catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
            {
                logger.LogWarning(fallbackEx, "Planning loads could not be read from SQL or resilient register for {PlanningDate}.", planningDate);
                db.ChangeTracker.Clear();
                return Ok(new
                {
                    planningDate,
                    loads = 0,
                    matchedStops = 0,
                    changedStops = 0,
                    unresolved = Array.Empty<string>(),
                    ambiguous = Array.Empty<string>(),
                    warnings = warnings.Append("Site reconciliation could not read planning loads. The planner import itself remains valid; retry reconciliation after deployment/schema recovery.").ToArray(),
                    reconciliationStatus = "Deferred"
                });
            }
        }

        var changedStops = 0;
        var matchedStops = 0;
        var unresolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var load in loads)
        {
            var loadChanged = false;
            foreach (var stop in load.Stops.OrderBy(x => x.Sequence))
            {
                var sourceName = ExtractSiteName(stop.Name);
                if (string.IsNullOrWhiteSpace(sourceName)) continue;

                var matches = MatchSites(sites, sourceName).Take(2).ToList();
                if (matches.Count == 0)
                {
                    unresolved.Add(sourceName);
                    continue;
                }
                if (matches.Count > 1)
                {
                    ambiguous.Add(sourceName);
                    continue;
                }

                matchedStops++;
                var site = matches[0];
                var beforeAddress = stop.Address;

                // User-facing planning/dispatch data is deliberately address/postcode + notes.
                // GPS coordinates remain internal to tracker/geofence evidence and are not copied
                // from Site Master into planner stops.
                if (!string.IsNullOrWhiteSpace(site.CollectionAddress))
                    stop.Address = MergeAddress(site.CollectionAddress, stop.Address);

                if (!string.Equals(beforeAddress, stop.Address, StringComparison.Ordinal))
                {
                    changedStops++;
                    loadChanged = true;
                }
            }

            if (!loadChanged) continue;
            if (registerFallback) await PlanningRegisterStore.SaveLoadAsync(db, load, User.Identity?.Name, ct);
            else await db.SaveChangesAsync(ct);
        }

        return Ok(new
        {
            planningDate,
            loads = loads.Count,
            matchedStops,
            changedStops,
            unresolved = unresolved.OrderBy(x => x).ToArray(),
            ambiguous = ambiguous.OrderBy(x => x).ToArray(),
            warnings = warnings.ToArray(),
            reconciliationStatus = sites.Count == 0 ? "Deferred" : "Completed"
        });
    }

    private async Task<List<Site>> ReadSitesAsync(CancellationToken ct)
    {
        try
        {
            return await db.Sites.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
        }
        catch (Exception ex) when (IsSchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
            // Restrict the fallback projection to the planner-maintained fields required for
            // postcode/address reconciliation. This avoids optional Site columns entirely.
            return await db.Sites.AsNoTracking()
                .Where(x => x.Active)
                .Select(x => new Site
                {
                    Id = x.Id,
                    ExternalCode = x.ExternalCode,
                    Name = x.Name,
                    DriverTextName = x.DriverTextName,
                    CollectionAddress = x.CollectionAddress,
                    Active = x.Active
                })
                .OrderBy(x => x.Name)
                .Take(5000)
                .ToListAsync(ct);
        }
    }

    internal static IEnumerable<Site> MatchSites(IEnumerable<Site> sites, string value)
    {
        var needle = Normalize(value);
        if (string.IsNullOrWhiteSpace(needle)) return [];

        return sites.Where(site =>
        {
            if (Normalize(site.ExternalCode) == needle || Normalize(site.Name) == needle || Normalize(site.DriverTextName) == needle)
                return true;

            return Aliases(site.Aliases).Any(alias => Normalize(alias) == needle);
        }).DistinctBy(site => site.Id);
    }

    private static IEnumerable<string> Aliases(string? aliases) => string.IsNullOrWhiteSpace(aliases)
        ? []
        : aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string ExtractSiteName(string? stopName)
    {
        if (string.IsNullOrWhiteSpace(stopName)) return string.Empty;
        var value = stopName.Trim();
        foreach (var prefix in new[] { "Collect · ", "Deliver · ", "Collect - ", "Deliver - " })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value[prefix.Length..].Trim();
        return value;
    }

    private static string? MergeAddress(string masterAddress, string? operationalDetail)
    {
        var master = masterAddress.Trim();
        if (string.IsNullOrWhiteSpace(operationalDetail)) return Clip(master, 500);
        var detail = operationalDetail.Trim();
        if (Normalize(detail).Contains(Normalize(master), StringComparison.Ordinal)) return Clip(detail, 500);
        return Clip($"{master} | {detail}", 500);
    }

    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string? Clip(string? value, int length) => string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, length)];

    private static bool IsSchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase);
    }
}
