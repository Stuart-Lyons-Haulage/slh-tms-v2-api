using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeRouteRuleMatcherRegressionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public OrderIntakeRouteRuleMatcherRegressionTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Single_sql_route_rule_fills_missing_collection_and_destination_without_blocking_review()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        if (!db.Database.IsRelational()) return;

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customerCode = $"CUS{suffix}".ToUpperInvariant();
        var collection = new Site { ExternalCode = $"COL{suffix}", Name = $"Collection {suffix}", Active = true };
        var delivery = new Site { ExternalCode = $"DEL{suffix}", Name = $"Delivery {suffix}", Active = true };
        db.Sites.AddRange(collection, delivery);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync($@"
            INSERT INTO dbo.OrderIntakeRouteRules
                (Id, CustomerCode, OriginSiteCode, OriginSiteName, DestinationSiteCode, DestinationName, Priority, ConfidenceScore, Active, CreatedAtUtc, UpdatedAtUtc)
            VALUES
                ('{Guid.NewGuid()}', '{customerCode}', '{collection.ExternalCode}', '{collection.Name}', '{delivery.ExternalCode}', '{delivery.Name}', 1, 90, 1, SYSUTCDATETIME(), SYSUTCDATETIME())");

        var parsed = new EmailIntakeParseResult([new ParsedEmailOrder("one", "one",
            JsonSerializer.SerializeToElement(new { customerCode, pallets = 10, plannerReady = true }), [])], [], null);

        var result = await OrderIntakeRouteRuleMatcher.ApplyAsync(db, parsed, CancellationToken.None);
        var payload = result.Orders.Single().Payload;

        Assert.Equal(collection.ExternalCode, payload.GetProperty("collectionSiteCode").GetString());
        Assert.Equal(collection.Name, payload.GetProperty("sellerName").GetString());
        Assert.Equal(delivery.ExternalCode, payload.GetProperty("deliverySiteCode").GetString());
        Assert.Equal(delivery.Name, payload.GetProperty("stallNumber").GetString());
        Assert.False(payload.GetProperty("orderIntakeRouteRequiresReview").GetBoolean());
    }
}
