using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Site Master location quality. Samsara requires a physical address and routable
/// coordinates for every route stop; geofences are deliberately not part of this check.
/// </summary>
[ApiController]
[Route("api/v1/health/site-locations")]
[AllowAnonymous]
public sealed class SiteLocationHealthController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);

        var missingAddress = sites.Where(site => string.IsNullOrWhiteSpace(site.CollectionAddress)).ToList();
        var missingCoordinates = sites.Where(site => site.Latitude is null || site.Longitude is null).ToList();
        var invalidCoordinates = sites.Where(site =>
            site.Latitude is < -90 or > 90 || site.Longitude is < -180 or > 180).ToList();
        var complete = sites.Count - missingAddress.Union(missingCoordinates).Union(invalidCoordinates).Count();

        return Ok(new
        {
            status = missingAddress.Count == 0 && missingCoordinates.Count == 0 && invalidCoordinates.Count == 0
                ? "healthy"
                : "attention",
            source = "Site Master",
            samsaraOwnsGeofences = true,
            activeSites = sites.Count,
            completeSites = complete,
            missingAddress = missingAddress.Count,
            missingCoordinates = missingCoordinates.Count,
            invalidCoordinates = invalidCoordinates.Count,
            checkedAtUtc = DateTimeOffset.UtcNow,
            issues = missingAddress
                .Concat(missingCoordinates)
                .Concat(invalidCoordinates)
                .DistinctBy(site => site.Id)
                .OrderBy(site => site.ExternalCode)
                .Take(200)
                .Select(site => new
                {
                    siteId = site.Id,
                    siteCode = site.ExternalCode,
                    siteName = site.Name,
                    address = site.CollectionAddress,
                    latitude = site.Latitude,
                    longitude = site.Longitude,
                    missingAddress = string.IsNullOrWhiteSpace(site.CollectionAddress),
                    missingCoordinates = site.Latitude is null || site.Longitude is null,
                    invalidCoordinates = site.Latitude is < -90 or > 90 || site.Longitude is < -180 or > 180
                })
        });
    }
}
