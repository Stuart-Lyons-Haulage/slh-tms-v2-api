using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Loads the stable Site Master slice needed by geofence matching, then enriches it
/// with the audited master-detail alias register. Only aliases that resolve to one
/// canonical Site are exposed to geofence matching; ambiguous aliases remain unlinked.
/// </summary>
public static class GeofenceSiteResolver
{
    public static async Task<List<Site>> LoadActiveSitesAsync(TmsDbContext db, CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking()
            .Where(x => x.Active && x.Name != null)
            .Select(x => new Site
            {
                Id = x.Id,
                ExternalCode = x.ExternalCode,
                Name = x.Name,
                DriverTextName = x.DriverTextName,
                Active = x.Active
            })
            .ToListAsync(ct);

        try
        {
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            // Alias enrichment is optional. Core code/name matching must remain available
            // even if the staged master-detail register is temporarily unavailable.
        }

        var aliasOwners = sites
            .SelectMany(site => Aliases(site.Aliases).Select(alias => new { site, alias, key = Normalize(alias) }))
            .Where(item => item.key.Length > 0)
            .GroupBy(item => item.key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.site.Id).Distinct().ToList(),
                StringComparer.OrdinalIgnoreCase);

        var resolved = new List<Site>(sites);
        foreach (var site in sites)
        {
            foreach (var alias in Aliases(site.Aliases))
            {
                if (string.Equals(alias, site.Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(alias, site.DriverTextName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var key = Normalize(alias);
                if (!aliasOwners.TryGetValue(key, out var owners) || owners.Count != 1 || owners[0] != site.Id)
                    continue;

                resolved.Add(new Site
                {
                    Id = site.Id,
                    ExternalCode = site.ExternalCode,
                    Name = site.Name,
                    DriverTextName = alias,
                    Active = true
                });
            }
        }

        return resolved;
    }

    private static IEnumerable<string> Aliases(string? aliases) => string.IsNullOrWhiteSpace(aliases)
        ? []
        : aliases.Split(new[] { ',', ';', '|', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
