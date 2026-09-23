using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/site-planning-profiles")]
[Authorize]
public sealed class SitePlanningProfilesController(TmsDbContext db) : ControllerBase
{
    private const string ProfileType = "siteplanningprofile";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(ct);
        var rows = await db.StagedImports.AsNoTracking().Where(x => x.EntityType == ProfileType && x.Status == StagingStatus.Promoted)
            .OrderByDescending(x => x.ReviewedAtUtc ?? x.ReceivedAtUtc).Take(5000).ToListAsync(ct);
        var profiles = new Dictionary<Guid, SitePlanningProfile>();
        foreach (var row in rows)
        {
            try
            {
                var profile = JsonSerializer.Deserialize<SitePlanningProfile>(row.PayloadJson, JsonOptions);
                if (profile is not null && !profiles.ContainsKey(profile.SiteId)) profiles[profile.SiteId] = profile;
            }
            catch (JsonException) { }
        }
        var regions = await SitePlanningProfileStore.ResolveRegionsAsync(db, sites.Select(x => x.Name), ct);
        return Ok(sites.Select(site =>
        {
            profiles.TryGetValue(site.Id, out var profile);
            return new
            {
                siteId = site.Id,
                site.ExternalCode,
                site.Name,
                site.CollectionAddress,
                defaultTemperatureC = profile?.DefaultTemperatureC,
                region = profile?.Region ?? regions.GetValueOrDefault(site.Name, "Other"),
                source = profile?.DefaultTemperatureC is null ? "No temperature default" : "TMS site profile"
            };
        }));
    }

    [HttpPut("{siteId:guid}"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> Update(Guid siteId, SitePlanningProfileUpdate request, CancellationToken ct)
    {
        var site = await db.Sites.AsNoTracking().SingleOrDefaultAsync(x => x.Id == siteId, ct);
        if (site is null) return NotFound(new { message = "Site not found." });
        if (request.DefaultTemperatureC is < -30 or > 30) return BadRequest(new { message = "Default temperature must be between -30°C and +30°C." });
        var region = string.IsNullOrWhiteSpace(request.Region) ? "Other" : request.Region.Trim();
        var profile = new SitePlanningProfile(site.Id, site.ExternalCode, site.Name, request.DefaultTemperatureC, region, site.CollectionAddress);
        var key = $"{ProfileType}:{site.Id:N}";
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport { EntityType = ProfileType, IdempotencyKey = key, PayloadJson = "{}", Source = "TMS site planning profile" };
            db.StagedImports.Add(row);
        }
        row.PayloadJson = JsonSerializer.Serialize(profile, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = User.Identity?.Name;
        row.ReviewNote = "Site temperature/region profile updated in Master Data.";
        await db.SaveChangesAsync(ct);
        return Ok(profile);
    }
}

public sealed record SitePlanningProfileUpdate(decimal? DefaultTemperatureC, string? Region);
