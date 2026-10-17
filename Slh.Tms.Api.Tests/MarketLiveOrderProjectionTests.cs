using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class MarketLiveOrderProjectionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public MarketLiveOrderProjectionTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Saving_live_market_order_preserves_market_site_and_master_stand()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var reference = $"MARKET-{suffix}";

        db.TransportOrders.Add(new TransportOrder
        {
            Reference = reference,
            CustomerCode = "TEST",
            CollectionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            SellerName = "NWF Merston",
            MarketName = "incoming market wording",
            StallNumber = "incorrect flattened market site",
            DriverInstructions = "Collection site: NWF Merston · Market: New Covent Garden Market · Market customer: ABC Produce · Stall / stand: D12 · Salesman: Joe"
        });

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var saved = await db.TransportOrders.AsNoTracking().SingleAsync(order => order.Reference == reference);
        Assert.Equal("New Covent Garden Market", saved.MarketName);
        Assert.Equal("D12", saved.StallNumber);
        Assert.Contains("Market customer: ABC Produce", saved.DriverInstructions);
        Assert.Contains("Salesman: Joe", saved.DriverInstructions);
    }
}
