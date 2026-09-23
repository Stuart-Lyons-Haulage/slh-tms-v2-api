using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class EmailOrderSiteMasterAlignmentTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public EmailOrderSiteMasterAlignmentTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Unrecognised_generic_depot_uses_unambiguous_destination_site()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var code = $"SITE-{Guid.NewGuid():N}"[..20];
        var site = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = code,
            Name = "Morrisons Bridgwater Canonical",
            DriverTextName = "Morrisons Bridgwater",
            Active = true
        };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        await MasterDetailStore.SaveAsync(
            db,
            "site",
            code,
            JsonSerializer.Serialize(new { externalCode = code, aliases = "Morrisons FRUITBRIDGWATER 718" }),
            "test",
            "test",
            CancellationToken.None);

        var payload = JsonSerializer.SerializeToElement(new
        {
            poNumber = "TEST-1",
            customerCode = "NWF",
            sellerName = "NWF - Merston",
            marketName = "Morrisons",
            stallNumber = "Morrisons FRUITBRIDGWATER 718"
        });
        var parsed = new EmailIntakeParseResult(
            [new ParsedEmailOrder("test", "test", payload, [])],
            [],
            null);

        var aligned = await EmailOrderSiteMasterAlignment.AlignAsync(db, parsed, CancellationToken.None);
        var root = aligned.Orders.Single().Payload;

        Assert.Equal("Morrisons Bridgwater", root.GetProperty("stallNumber").GetString());
        Assert.Equal("Morrisons Bridgwater", root.GetProperty("marketName").GetString());
        Assert.Equal("Morrisons", root.GetProperty("sourceMarketName").GetString());
        Assert.Equal("Morrisons FRUITBRIDGWATER 718", root.GetProperty("sourceStallNumber").GetString());
        Assert.True(root.GetProperty("depotResolvedFromDestination").GetBoolean());
        Assert.Equal(site.Id.ToString(), root.GetProperty("deliverySiteId").GetString());
        Assert.True(root.GetProperty("masterDataAligned").GetBoolean());
    }

    [Fact]
    public async Task Recognised_depot_is_not_replaced_by_a_different_destination()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var depot = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = $"LAT-{suffix}",
            Name = $"Morrisons Latimer {suffix}",
            DriverTextName = $"Morrisons Latimer {suffix}",
            Active = true
        };
        var destination = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = $"BRI-{suffix}",
            Name = $"Morrisons Bridgwater {suffix}",
            DriverTextName = $"Morrisons Bridgwater {suffix}",
            Active = true
        };
        db.Sites.AddRange(depot, destination);
        await db.SaveChangesAsync();

        var payload = JsonSerializer.SerializeToElement(new
        {
            poNumber = "TEST-2",
            customerCode = "NWF",
            marketName = depot.Name,
            stallNumber = destination.Name
        });
        var parsed = new EmailIntakeParseResult(
            [new ParsedEmailOrder("test-2", "test-2", payload, [])],
            [],
            null);

        var aligned = await EmailOrderSiteMasterAlignment.AlignAsync(db, parsed, CancellationToken.None);
        var root = aligned.Orders.Single().Payload;

        Assert.Equal(depot.DriverTextName, root.GetProperty("marketName").GetString());
        Assert.Equal(destination.DriverTextName, root.GetProperty("stallNumber").GetString());
        Assert.False(root.GetProperty("depotResolvedFromDestination").GetBoolean());
        Assert.Equal(depot.Id.ToString(), root.GetProperty("depotSiteId").GetString());
        Assert.Equal(destination.Id.ToString(), root.GetProperty("deliverySiteId").GetString());
    }

    [Fact]
    public async Task Market_customer_keeps_stall_identity_while_delivery_site_becomes_physical_market()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var market = new Site
        {
            Id = Guid.NewGuid(),
            ExternalCode = $"COV-{suffix}",
            Name = $"New Covent Garden Market {suffix}",
            DriverTextName = $"New Covent Garden Market {suffix}",
            Active = true
        };
        var trader = new MarketContact
        {
            Id = Guid.NewGuid(),
            Market = "Covent",
            Name = $"Trader {suffix}",
            StandOrLocation = $"Stand D12-{suffix}",
            Active = true
        };
        db.Sites.Add(market);
        db.MarketContacts.Add(trader);
        await db.SaveChangesAsync();

        var payload = JsonSerializer.SerializeToElement(new
        {
            poNumber = $"MKT-{suffix}",
            customerCode = "MARKET",
            marketName = "COVENTGARDEN",
            stallNumber = trader.Name
        });
        var parsed = new EmailIntakeParseResult(
            [new ParsedEmailOrder($"market-{suffix}", $"market-{suffix}", payload, [])],
            [],
            null);

        var aligned = await EmailOrderSiteMasterAlignment.AlignAsync(db, parsed, CancellationToken.None);
        var root = aligned.Orders.Single().Payload;

        Assert.Equal(market.DriverTextName, root.GetProperty("marketName").GetString());
        Assert.Equal(market.DriverTextName, root.GetProperty("deliverySite").GetString());
        Assert.Equal(trader.Name, root.GetProperty("stallNumber").GetString());
        Assert.Equal(trader.Name, root.GetProperty("sourceStallNumber").GetString());
        Assert.Equal(market.Id.ToString(), root.GetProperty("deliverySiteId").GetString());
        Assert.Equal(market.Id.ToString(), root.GetProperty("depotSiteId").GetString());
    }
}
