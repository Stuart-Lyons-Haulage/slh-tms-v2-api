using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceAutoSeedLinkPersistenceTests
{
    [Fact]
    public void Explicit_site_links_survive_provider_catalogue_refresh()
    {
        var draytonSiteId = Guid.NewGuid();
        var merstonSiteId = Guid.NewGuid();
        var drayton = Fence("Drayton (Natures Way)", draytonSiteId, "SITE328");
        var merston = Fence("Merston (Natures Way)", merstonSiteId, "SITE329");
        var locationOnly = Fence("Operational Yard Only", null, "LOCATION_ONLY");
        var unlinked = Fence("Unlinked Provider Fence", null, null);

        var preserved = GeofenceAutoSeed.CaptureExplicitLinks([drayton, merston, locationOnly, unlinked]);

        Assert.Equal(3, preserved.Count);
        Assert.DoesNotContain(unlinked.Id, preserved.Keys);

        // Simulate the legacy Falcon import behaviour, which refreshes provider identity
        // fields and can clear a manual SiteId when automatic name matching does not find it.
        drayton.SiteId = null;
        drayton.SiteNumber = "1";
        merston.SiteId = null;
        merston.SiteNumber = null;
        locationOnly.SiteId = Guid.NewGuid();
        locationOnly.SiteNumber = "SITE001";
        unlinked.SiteId = Guid.NewGuid();
        unlinked.SiteNumber = "SITE777";

        var restored = GeofenceAutoSeed.RestoreExplicitLinks([drayton, merston, locationOnly, unlinked], preserved);

        Assert.Equal(3, restored);
        Assert.Equal(draytonSiteId, drayton.SiteId);
        Assert.Equal("SITE328", drayton.SiteNumber);
        Assert.Equal(merstonSiteId, merston.SiteId);
        Assert.Equal("SITE329", merston.SiteNumber);
        Assert.Null(locationOnly.SiteId);
        Assert.Equal("LOCATION_ONLY", locationOnly.SiteNumber);

        // A fence that had no explicit operator override before the refresh is still free
        // to follow automatic provider/Site Master matching.
        Assert.NotNull(unlinked.SiteId);
        Assert.Equal("SITE777", unlinked.SiteNumber);
    }

    private static SiteGeofence Fence(string name, Guid? siteId, string? siteNumber) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        NormalizedName = Normalize(name),
        SiteId = siteId,
        SiteNumber = siteNumber,
        PolygonJson = "[[0,0],[1,0],[1,1]]",
        Active = true,
        UpdatedAtUtc = DateTimeOffset.UtcNow
    };

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
