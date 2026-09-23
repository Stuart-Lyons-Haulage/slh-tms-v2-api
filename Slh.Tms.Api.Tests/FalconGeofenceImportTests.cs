using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class FalconGeofenceImportTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public FalconGeofenceImportTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Preview_classifies_exact_site_match_and_unmatched_row()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Sites.Add(new Site { ExternalCode = "SITE001", Name = "Absolute Taste (Bicester)", Active = true });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync("/api/v1/geofences/import-falcon/preview", Export(
            Fence("  absolute   taste (bicester)  ", [[-1.13, 51.89], [-1.12, 51.89], [-1.12, 51.90]]),
            Fence("Different DOT Name", [[0.10, 52.10], [0.11, 52.10], [0.11, 52.11]])));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var rows = json.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal("Matched", rows[0].GetProperty("status").GetString());
        Assert.Equal("SITE001", rows[0].GetProperty("suggestedSiteCode").GetString());
        Assert.Equal("NeedsLinking", rows[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Commit_reimports_geometry_and_timing_without_losing_manual_site_link()
    {
        Guid siteId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var site = new Site { ExternalCode = "SITE023", Name = "Barfoots Sefter", Active = true };
            var fence = new SiteGeofence
            {
                Name = "Selsey Despatch",
                NormalizedName = "SELSEY DESPATCH",
                Category = "Delivery",
                MaxWaitMinutes = 60,
                SiteId = site.Id,
                SiteNumber = site.ExternalCode,
                PolygonJson = "[[0,0],[1,0],[0,1]]",
                Active = true
            };
            db.AddRange(site, fence);
            await db.SaveChangesAsync();
            siteId = site.Id;
        }

        var export = Export(Fence("Selsey Despatch", [[-0.79, 50.73], [-0.78, 50.73], [-0.78, 50.74]], maxWait: 120));
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync("/api/v1/geofences/import-falcon/commit", new
        {
            sourceFileName = "geofence-category-Delivery.json",
            export,
            decisions = new[] { new { clientKey = "SELSEY DESPATCH", siteId, skip = false } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var stored = Assert.Single(await finalDb.SiteGeofences.ToListAsync());
        Assert.Equal(siteId, stored.SiteId);
        Assert.Equal("SITE023", stored.SiteNumber);
        Assert.Equal(120, stored.MaxWaitMinutes);
        Assert.Equal("[[-0.79,50.73],[-0.78,50.73],[-0.78,50.74]]", stored.PolygonJson);
    }

    [Theory]
    [InlineData("other.format", 1)]
    [InlineData("falcon.geofence", 2)]
    public async Task Preview_rejects_unsupported_file_contract(string format, int version)
    {
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync("/api/v1/geofences/import-falcon/preview", new
        {
            format,
            version,
            category = "Delivery",
            geofences = new[] { Fence("Example", [[0.0, 51.0], [0.1, 51.0], [0.1, 51.1]]) }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Commit_rejects_unresolved_row_without_persisting_any_selected_rows()
    {
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var export = Export(
            Fence("First", [[0.0, 51.0], [0.1, 51.0], [0.1, 51.1]]),
            Fence("Unresolved", [[1.0, 52.0], [1.1, 52.0], [1.1, 52.1]]));
        var response = await client.PostAsJsonAsync("/api/v1/geofences/import-falcon/commit", new
        {
            sourceFileName = "delivery.json",
            export,
            decisions = new[]
            {
                new { clientKey = "FIRST", siteId = (Guid?)null, skip = true },
                new { clientKey = "UNRESOLVED", siteId = (Guid?)null, skip = false }
            }
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Empty(await db.SiteGeofences.ToListAsync());
    }

    [Fact]
    public async Task Commit_allows_an_invalid_row_to_be_explicitly_skipped()
    {
        Guid siteId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var site = new Site { ExternalCode = "SITE100", Name = "Valid Fence", Active = true };
            db.Sites.Add(site);
            await db.SaveChangesAsync();
            siteId = site.Id;
        }

        var export = Export(
            Fence("Valid Fence", [[0.0, 51.0], [0.1, 51.0], [0.1, 51.1]]),
            Fence("Invalid Fence", [[0.0, 51.0], [0.1, 51.0]]));
        var client = _factory.CreateClientWithUser("planner@lyonshaulage.com");
        var response = await client.PostAsJsonAsync("/api/v1/geofences/import-falcon/commit", new
        {
            sourceFileName = "delivery.json",
            export,
            decisions = new[]
            {
                new { clientKey = "VALID FENCE", siteId = (Guid?)siteId, skip = false },
                new { clientKey = "INVALID FENCE", siteId = (Guid?)null, skip = true }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        Assert.Equal("Valid Fence", Assert.Single(await finalDb.SiteGeofences.ToListAsync()).Name);
    }

    private static object Export(params object[] geofences) => new
    {
        format = "falcon.geofence",
        version = 1,
        category = "Delivery",
        category_max_wait_time = (int?)null,
        geofences
    };

    private static object Fence(string name, double[][] points, int maxWait = 60) => new
    {
        name,
        colour = "EEA014",
        max_wait_time = maxWait,
        pending_entry_minutes = 0,
        pending_exit_minutes = 0,
        site_no = "1",
        area_rec_id = (string?)null,
        points,
        attributes = Array.Empty<object>()
    };
}
