using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MasterDetailStoreTests
{
    [Fact]
    public async Task DriverWorkbookFieldsAreRetainedAndEnriched()
    {
        await using var db = CreateDb();
        var driver = new Driver { EmployeeNumber = "D-17", DisplayName = "Test Driver" };
        db.Drivers.Add(driver);
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "driver", "D-17", """{"employeeNumber":"D-17","coding":"FT","agencyName":"SLH","northEligible":true,"preloadEligible":false,"drivingLicenceNumber":"LIC17","licenceExpiry":"2027-03-04","licenceStatus":"Valid","tachoMasterDriverId":"TM17","tachoCardNumber":"CARD17","tachoDriveAvailableTodayMinutes":238,"tachoDriveAvailableWeekMinutes":1260,"tachoWorkAvailableWeekMinutes":1800,"lastTachoSyncUtc":"2026-08-16T20:00:00Z","notes":"Master note"}""", "test", "tester", CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Drivers.AsNoTracking().ToListAsync();
        await MasterDetailStore.EnrichDriversAsync(db, rows, CancellationToken.None);

        var enriched = Assert.Single(rows);
        Assert.Equal("FT", enriched.Coding);
        Assert.Equal("SLH", enriched.AgencyName);
        Assert.True(enriched.NorthEligible is true);
        Assert.True(enriched.PreloadEligible is false);
        Assert.Equal("LIC17", enriched.DrivingLicenceNumber);
        Assert.Equal(new DateOnly(2027, 3, 4), enriched.LicenceExpiry);
        Assert.Equal("TM17", enriched.TachoMasterDriverId);
        Assert.Equal("CARD17", enriched.TachoCardNumber);
        Assert.Equal(238, enriched.TachoDriveAvailableTodayMinutes);
        Assert.Equal(1260, enriched.TachoDriveAvailableWeekMinutes);
        Assert.Equal(1800, enriched.TachoWorkAvailableWeekMinutes);
        Assert.Equal(DateTimeOffset.Parse("2026-08-16T20:00:00Z"), enriched.LastTachoSyncUtc);
    }

    [Fact]
    public async Task Sparse_refresh_does_not_erase_previously_retained_master_fields()
    {
        await using var db = CreateDb();
        await MasterDetailStore.SaveAsync(db, "driver", "D-17", """{"employeeNumber":"D-17","notes":"Keep this note","tachoMasterDriverId":"TM17"}""", "test", "tester", CancellationToken.None);
        await MasterDetailStore.SaveAsync(db, "driver", "D-17", """{"employeeNumber":"D-17","notes":"","tachoMasterDriverId":null,"email":"driver@example.com"}""", "test", "tester", CancellationToken.None);

        using var document = System.Text.Json.JsonDocument.Parse((await db.StagedImports.SingleAsync()).PayloadJson);
        Assert.Equal("Keep this note", document.RootElement.GetProperty("notes").GetString());
        Assert.Equal("TM17", document.RootElement.GetProperty("tachoMasterDriverId").GetString());
        Assert.Equal("driver@example.com", document.RootElement.GetProperty("email").GetString());
    }

    [Fact]
    public async Task FleetioPlaceholderVehiclesAreQuarantined()
    {
        await using var db = CreateDb();
        db.Vehicles.AddRange(
            new Vehicle { Registration = "C123456", Active = true },
            new Vehicle { Registration = "CU23ABC", Active = true });
        await db.SaveChangesAsync();

        var changed = await MasterDetailStore.QuarantineFleetioPlaceholdersAsync(db, CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.False((await db.Vehicles.SingleAsync(vehicle => vehicle.Registration == "C123456")).Active);
        Assert.True((await db.Vehicles.SingleAsync(vehicle => vehicle.Registration == "CU23ABC")).Active);
    }

    [Fact]
    public async Task SiteMapPointsAreRetainedAndEnriched()
    {
        await using var db = CreateDb();
        db.Sites.Add(new Site { ExternalCode = "SITE-17", Name = "Test Depot" });
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "site", "SITE-17", """{"externalCode":"SITE-17","aliases":"Test Yard","latitude":51.507351,"longitude":-0.127758}""", "test", "tester", CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Sites.AsNoTracking().ToListAsync();
        await MasterDetailStore.EnrichSitesAsync(db, rows, CancellationToken.None);

        var enriched = Assert.Single(rows);
        Assert.Equal("Test Yard", enriched.Aliases);
        Assert.Equal(51.507351m, enriched.Latitude);
        Assert.Equal(-0.127758m, enriched.Longitude);
    }

    [Fact]
    public async Task DuplicateSiteCodesDoNotBreakLookupEnrichment()
    {
        await using var db = CreateDb();
        db.Sites.AddRange(
            new Site { ExternalCode = "DUP", Name = "Duplicate Depot A" },
            new Site { ExternalCode = "DUP", Name = "Duplicate Depot B" });
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(db, "site", "DUP", """{"externalCode":"DUP","aliases":"Shared depot","latitude":52.1,"longitude":-1.2}""", "test", "tester", CancellationToken.None);

        db.ChangeTracker.Clear();
        var rows = await db.Sites.AsNoTracking().OrderBy(site => site.Name).ToListAsync();
        await MasterDetailStore.EnrichSitesAsync(db, rows, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, site =>
        {
            Assert.Equal("Shared depot", site.Aliases);
            Assert.Equal(52.1m, site.Latitude);
            Assert.Equal(-1.2m, site.Longitude);
        });
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase($"master-detail-{Guid.NewGuid()}").Options;
        return new TmsDbContext(options);
    }
}
