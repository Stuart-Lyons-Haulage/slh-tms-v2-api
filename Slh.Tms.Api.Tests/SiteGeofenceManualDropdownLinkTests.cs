using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SiteGeofenceManualDropdownLinkTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public SiteGeofenceManualDropdownLinkTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Manual_dropdown_link_accepts_selected_site_when_names_do_not_match()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SITE023", Name = "Barfoots Sefter", Active = true };
        var fence = new SiteGeofence
        {
            Name = "Selsey Despatch",
            NormalizedName = "SELSEY DESPATCH",
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        };
        db.Sites.Add(site);
        db.SiteGeofences.Add(fence);
        await db.SaveChangesAsync();

        var status = await SiteGeofenceMasterSync.LinkGeofenceAsync(db, fence.Id, site.ExternalCode, CancellationToken.None);

        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal(site.ExternalCode, fence.SiteNumber);
        Assert.True(status.GeofenceLinked);
        Assert.False(status.NeedsReview);
    }

    [Fact]
    public async Task Sync_sites_preserves_explicit_manual_dropdown_link()
    {
        await using var db = CreateDb();
        var site = new Site { ExternalCode = "SITE023", Name = "Barfoots Sefter", Active = true };
        var fence = new SiteGeofence
        {
            Name = "Selsey Despatch",
            NormalizedName = "SELSEY DESPATCH",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = "[[0,0],[1,0],[0,1]]",
            Active = true
        };
        db.Sites.Add(site);
        db.SiteGeofences.Add(fence);
        await db.SaveChangesAsync();

        var result = await SiteGeofenceMasterSync.SyncAsync(db, CancellationToken.None);

        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal(site.ExternalCode, fence.SiteNumber);
        Assert.Equal(0, result.GeofencesUnlinked);
        Assert.Contains(result.Sites, x => x.SiteId == site.Id && x.GeofenceLinked && !x.NeedsReview);
    }

    [Fact]
    public async Task Manual_dropdown_link_accepts_embedded_geofence_integrity_id_without_existing_row()
    {
        await using var db = CreateDb();
        var embedded = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name == "Selsey Despatch"));
        var site = new Site { ExternalCode = "SITE010", Name = "Aldi-Atherstone", Active = true };
        db.Sites.Add(site);
        await db.SaveChangesAsync();

        var status = await SiteGeofenceMasterSync.LinkGeofenceAsync(db, embedded.Id, site.ExternalCode, CancellationToken.None);

        var fence = Assert.Single(db.SiteGeofences.Where(x => x.NormalizedName == NormalizeName(embedded.Name)));
        Assert.Equal(embedded.Id, fence.Id);
        Assert.True(fence.Active);
        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal("SITE010", fence.SiteNumber);
        Assert.True(status.GeofenceLinked);
        Assert.False(status.NeedsReview);
    }

    [Fact]
    public async Task Link_endpoint_accepts_geofence_integrity_embedded_id()
    {
        var embedded = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(x => x.Name == "Selsey Despatch"));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { ExternalCode = "SITE010", Name = "Aldi-Atherstone", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync($"/api/v1/site-geofence-sync/geofences/{embedded.Id}/link", new { siteCode = "SITE010" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var site = Assert.Single(finalDb.Sites.Where(x => x.ExternalCode == "SITE010"));
        var fence = Assert.Single(finalDb.SiteGeofences.Where(x => x.NormalizedName == NormalizeName(embedded.Name)));
        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal("SITE010", fence.SiteNumber);
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"site-geofence-manual-dropdown-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }

    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
