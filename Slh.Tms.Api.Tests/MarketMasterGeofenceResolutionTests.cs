using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MarketMasterGeofenceResolutionTests
{
    [Fact]
    public async Task Covent_market_label_and_market_customer_resolve_to_same_physical_market_geofence()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var site = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = "MKT-COVENT",
            Name = "New Covent Garden Market",
            DriverTextName = "New Covent Garden Market",
            Active = true
        };
        var customer = new MarketContact
        {
            Id = Guid.NewGuid(),
            Market = "Covent",
            Name = "Test Covent Trader",
            StandOrLocation = "Stand D12",
            Active = true
        };
        var geofence = new SiteGeofence
        {
            Id = Guid.NewGuid(),
            Name = "New Covent Garden Market",
            NormalizedName = "NEW COVENT GARDEN MARKET",
            SiteId = site.Id,
            SiteNumber = site.ExternalCode,
            PolygonJson = "[]",
            Active = true
        };

        db.Sites.Add(site);
        db.MarketContacts.Add(customer);
        db.SiteGeofences.Add(geofence);
        await db.SaveChangesAsync();

        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, CancellationToken.None);

        var market = resolver.Resolve("COVENTGARDEN");
        var trader = resolver.Resolve(customer.Name);
        var stall = resolver.Resolve(customer.StandOrLocation);

        Assert.True(market.SiteMatched);
        Assert.True(market.GeofenceLinked);
        Assert.Equal(site.Id, market.SiteId);
        Assert.Equal(geofence.Id, market.GeofenceId);

        Assert.Equal(site.Id, trader.SiteId);
        Assert.Equal(geofence.Id, trader.GeofenceId);
        Assert.Equal(site.Id, stall.SiteId);
        Assert.Equal(geofence.Id, stall.GeofenceId);
    }
}
