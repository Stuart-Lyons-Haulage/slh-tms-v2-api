using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class NaturesWayGeofenceImportRegressionTests
{
    [Fact]
    public void Selsey_and_Merston_are_not_treated_as_renames()
    {
        Assert.False(FalconGeofenceImportService.NamesSupportRename(
            "Selsey (Natures Way)",
            "Merston (Natures Way)"));
    }

    [Fact]
    public async Task Importing_Selsey_with_same_boundary_preserves_Merston_as_separate_geofence()
    {
        await using var db = CreateDb();
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = "NWF",
            Name = "Natures Way Foods",
            Active = true
        };
        var merston = new SiteGeofence
        {
            Id = Guid.NewGuid(),
            Name = "Merston (Natures Way)",
            NormalizedName = "MERSTON (NATURES WAY)",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = PolygonJson(),
            Active = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        db.Sites.Add(site);
        db.SiteGeofences.Add(merston);
        await db.SaveChangesAsync();

        var export = Export("Selsey (Natures Way)");
        var preview = await FalconGeofenceImportService.PreviewAsync(db, export, CancellationToken.None);
        var row = Assert.Single(preview.Rows);

        Assert.Equal("NeedsLinking", row.Status);
        Assert.Null(row.PossibleRenameGeofenceId);
        Assert.Contains("remain a separate record", row.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var result = await FalconGeofenceImportService.CommitAsync(
            db,
            new FalconGeofenceCommitRequest(
                "natures-way.json",
                export,
                [new FalconGeofenceImportDecision(row.ClientKey, site.Id)]),
            "regression-test",
            CancellationToken.None);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);

        var active = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync();
        Assert.Equal(2, active.Count);
        Assert.Contains(active, x => x.Id == merston.Id && x.Name == "Merston (Natures Way)");
        Assert.Contains(active, x => x.Name == "Selsey (Natures Way)" && x.Id != merston.Id);
        Assert.All(active, x => Assert.Equal(site.Id, x.SiteId));
    }

    [Fact]
    public void Minor_name_change_can_still_be_confirmed_as_a_rename()
    {
        Assert.True(FalconGeofenceImportService.NamesSupportRename(
            "Tesco Reading RDC",
            "Tesco Reading RDC Geofence"));
    }

    private static JsonElement Export(string name) => JsonSerializer.SerializeToElement(new
    {
        format = "falcon.geofence",
        version = 1,
        category = "Customer",
        geofences = new[]
        {
            new
            {
                name,
                points = new[]
                {
                    new[] { -0.7700, 50.8300 },
                    new[] { -0.7690, 50.8300 },
                    new[] { -0.7690, 50.8310 },
                    new[] { -0.7700, 50.8310 }
                }
            }
        }
    });

    private static string PolygonJson() => JsonSerializer.Serialize(new[]
    {
        new[] { -0.7700, 50.8300 },
        new[] { -0.7690, 50.8300 },
        new[] { -0.7690, 50.8310 },
        new[] { -0.7700, 50.8310 }
    });

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"natures-way-geofence-{Guid.NewGuid()}")
            .Options;
        return new TmsDbContext(options);
    }
}
