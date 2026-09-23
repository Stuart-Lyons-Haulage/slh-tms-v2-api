using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceSitePromotionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public GeofenceSitePromotionTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Repair_links_promotes_coded_geofence_to_canonical_site_master()
    {
        var fenceId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.SiteGeofences.Add(new SiteGeofence
            {
                Id = fenceId,
                Name = "Aldi Cardiff",
                NormalizedName = "ALDI CARDIFF",
                SiteNumber = "9101",
                SiteId = null,
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsync("/api/v1/geofences/repair-links", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var site = Assert.Single(finalDb.Sites.Where(x => x.Name == "Aldi Cardiff"));
        Assert.True(site.Active);
        Assert.Equal("SITE001", site.ExternalCode);
        Assert.Equal("Aldi Cardiff", site.Name);
        Assert.Equal("Aldi Cardiff", site.DriverTextName);

        var fence = finalDb.SiteGeofences.Single(x => x.Id == fenceId);
        Assert.Equal(site.Id, fence.SiteId);
        Assert.Equal("SITE001", fence.SiteNumber);

        var auditProcessor = new AuditOutboxProcessor(finalDb, NullLogger<AuditOutboxProcessor>.Instance);
        await auditProcessor.ProcessPendingAsync(CancellationToken.None);
        Assert.Contains(finalDb.MasterDataAudits, x => x.EntityType == "Site" && x.EntityId == site.Id && x.Action == "CreatedFromOperationalGeofence");
        Assert.Contains(finalDb.MasterDataAudits, x => x.EntityType == "Geofence" && x.EntityId == fenceId && x.Action == "PromotedToSiteMaster");
    }

    [Fact]
    public async Task Repair_links_reuses_inactive_numeric_site_alias_without_duplicate()
    {
        var siteId = Guid.NewGuid();
        var fenceId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { Id = siteId, ExternalCode = "009102", Name = "Existing Delivery", Active = false });
            db.SiteGeofences.Add(new SiteGeofence
            {
                Id = fenceId,
                Name = "Existing Delivery Geofence",
                NormalizedName = "EXISTING DELIVERY GEOFENCE",
                SiteNumber = "9102",
                SiteId = null,
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsync("/api/v1/geofences/repair-links", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var site = finalDb.Sites.Single(x => x.Id == siteId);
        Assert.Equal(siteId, site.Id);
        Assert.True(site.Active);
        var fence = finalDb.SiteGeofences.Single(x => x.Id == fenceId);
        Assert.Equal(siteId, fence.SiteId);
        Assert.Equal("SITE001", site.ExternalCode);
        Assert.Equal("SITE001", fence.SiteNumber);
        Assert.Single(finalDb.Sites.Where(x => x.Id == siteId));
    }

    [Fact]
    public async Task Import_falcon_accepts_payload_as_site_sync_trigger_instead_of_request_failure()
    {
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        using var request = new StringContent("{\"geofences\":[]}", Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/v1/geofences/import-falcon", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("embedded_geofence_runtime", document.RootElement.GetProperty("code").GetString());
        Assert.True(document.RootElement.TryGetProperty("sitesMissingGeofence", out _));
    }

    [Fact]
    public async Task Assistant_safe_fix_syncs_geofence_site_links()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { ExternalCode = "ALDI-CHELMSFORD", Name = "Aldi Chelmsford", Active = true });
            db.SiteGeofences.Add(new SiteGeofence
            {
                Name = "Chelmsford (Aldi)",
                NormalizedName = "CHELMSFORD ALDI",
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsync("/api/v1/assistant/fix-safe-validations", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Contains(finalDb.SiteGeofences, row => row.Name == "Chelmsford (Aldi)" && row.SiteId != null);
    }
}
