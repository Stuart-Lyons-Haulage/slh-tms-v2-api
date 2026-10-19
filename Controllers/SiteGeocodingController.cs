using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/master-data/geocoding")]
[Authorize]
public sealed class SiteGeocodingController(SiteGeocodingService geocoder, TmsDbContext db) : ControllerBase
{
    /// <summary>
    /// Geocodes active sites/depots that have a postcode but no coordinates, so their
    /// stops carry latitude/longitude for Samsara dispatch and the optimiser.
    /// <c>?force=true</c> re-geocodes everything with a postcode.
    /// </summary>
    [HttpPost("backfill")]
    public async Task<ActionResult<GeocodeBackfillResult>> Backfill([FromQuery] bool force, CancellationToken ct)
        => Ok(await geocoder.BackfillAsync(force, ct));

    /// <summary>Coverage snapshot: how many sites can currently be placed on a map.</summary>
    [HttpGet("status")]
    public async Task<ActionResult<GeocodeStatus>> Status(CancellationToken ct)
    {
        var sites = await db.MasterSites.Where(s => s.IsActive)
            .Select(s => new { s.Latitude, s.Longitude, s.Postcode })
            .ToListAsync(ct);
        var withCoordinates = sites.Count(s => s.Latitude is not null && s.Longitude is not null);
        var missingPostcode = sites.Count(s =>
            (s.Latitude is null || s.Longitude is null) && string.IsNullOrWhiteSpace(s.Postcode));
        return Ok(new GeocodeStatus(sites.Count, withCoordinates, sites.Count - withCoordinates, missingPostcode));
    }
}

public sealed record GeocodeStatus(int TotalSites, int WithCoordinates, int MissingCoordinates, int MissingPostcode);
