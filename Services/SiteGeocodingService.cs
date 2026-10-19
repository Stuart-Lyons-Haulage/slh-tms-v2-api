using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Populates Site and Depot coordinates from their postcode via <see cref="PostcodesIoClient"/>.
/// Samsara route stops require latitude/longitude on every stop, and the backhaul/consolidation
/// optimiser needs them to judge proximity; both read the same Site columns. Records without a
/// postcode are reported for manual attention rather than guessed.
/// </summary>
public sealed class SiteGeocodingService(TmsDbContext db, PostcodesIoClient postcodes, ILogger<SiteGeocodingService> logger)
{
    /// <summary>
    /// Geocodes active sites and depots that have a postcode but no coordinates.
    /// When <paramref name="force"/> is true, every record with a postcode is re-geocoded.
    /// </summary>
    public async Task<GeocodeBackfillResult> BackfillAsync(bool force, CancellationToken ct)
    {
        var sites = await db.MasterSites.Where(s => s.IsActive).ToListAsync(ct);
        var depots = await db.MasterDepots.Where(d => d.IsActive).ToListAsync(ct);

        var targets = sites
            .Select(s => new GeoTarget(s.SiteName, s.Postcode,
                () => s.Latitude is not null && s.Longitude is not null,
                (la, lo) => { s.Latitude = la; s.Longitude = lo; s.UpdatedAt = DateTime.UtcNow; }))
            .Concat(depots.Select(d => new GeoTarget(d.DepotName, d.Postcode,
                () => d.Latitude is not null && d.Longitude is not null,
                (la, lo) => { d.Latitude = la; d.Longitude = lo; d.UpdatedAt = DateTime.UtcNow; })))
            .ToList();

        var missingPostcode = new List<string>();
        var needsGeocode = new List<GeoTarget>();
        var alreadyHad = 0;
        foreach (var target in targets)
        {
            var hasCoords = target.HasCoordinates();
            if (string.IsNullOrWhiteSpace(target.Postcode))
            {
                if (hasCoords) alreadyHad++;
                else missingPostcode.Add(target.Name);
                continue;
            }
            if (hasCoords && !force) { alreadyHad++; continue; }
            needsGeocode.Add(target);
        }

        var located = await postcodes.BulkGeocodeAsync(needsGeocode.Select(t => t.Postcode), ct);
        var geocoded = 0;
        var unresolved = new List<string>();
        foreach (var target in needsGeocode)
        {
            var key = PostcodesIoClient.NormalisePostcode(target.Postcode);
            if (key is not null && located.TryGetValue(key, out var point))
            {
                target.Apply(point.Latitude, point.Longitude);
                geocoded++;
            }
            else
            {
                unresolved.Add($"{target.Name} ({target.Postcode})");
            }
        }

        if (geocoded > 0) await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Site geocoding backfill: {Geocoded} geocoded, {AlreadyHad} already had coordinates, {NoPostcode} without a postcode, {Unresolved} postcodes not found",
            geocoded, alreadyHad, missingPostcode.Count, unresolved.Count);

        return new GeocodeBackfillResult(geocoded, alreadyHad, missingPostcode, unresolved);
    }

    private sealed record GeoTarget(string Name, string? Postcode, Func<bool> HasCoordinates, Action<decimal, decimal> Apply);
}

public sealed record GeocodeBackfillResult(
    int Geocoded,
    int AlreadyHadCoordinates,
    IReadOnlyList<string> SitesWithoutPostcode,
    IReadOnlyList<string> PostcodesNotFound);
