using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class AssistantVerifiedFixTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public AssistantVerifiedFixTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Duplicate_order_fix_only_reports_success_after_cancelled_status_is_re_read()
    {
        var date = new DateOnly(2031, 3, 14);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var reference = $"VERIFY-{Guid.NewGuid():N}"[..20];

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.TransportOrders.AddRange(
                new TransportOrder
                {
                    Id = firstId,
                    Reference = reference,
                    CustomerCode = "VERIFY",
                    CollectionDate = date,
                    DeliveryDate = date.AddDays(1),
                    Pallets = 4,
                    SellerName = "Verified Seller",
                    MarketName = "Verified Market",
                    StallNumber = "V1",
                    Status = OrderStatus.ReadyToPlan,
                    CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
                },
                new TransportOrder
                {
                    Id = secondId,
                    Reference = reference,
                    CustomerCode = "VERIFY",
                    CollectionDate = date,
                    DeliveryDate = date.AddDays(1),
                    Pallets = 4,
                    SellerName = "Verified Seller",
                    MarketName = "Verified Market",
                    StallNumber = "V1",
                    Status = OrderStatus.ReadyToPlan,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsync($"/api/v1/assistant/order-duplicates/fix?date={date:yyyy-MM-dd}", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, body.RootElement.GetProperty("attempted").GetInt32());
        Assert.Equal(1, body.RootElement.GetProperty("applied").GetInt32());
        Assert.True(body.RootElement.GetProperty("verified").GetBoolean());
        Assert.Contains("Cancelled exact duplicate order", body.RootElement.GetProperty("changes")[0].GetString());

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var active = verifyDb.TransportOrders.Count(order =>
            (order.Id == firstId || order.Id == secondId) && order.Status != OrderStatus.Cancelled);
        var cancelled = verifyDb.TransportOrders.Count(order =>
            (order.Id == firstId || order.Id == secondId) && order.Status == OrderStatus.Cancelled);
        Assert.Equal(1, active);
        Assert.Equal(1, cancelled);
    }

    [Fact]
    public async Task Safe_master_fix_does_not_merge_same_market_trader_at_different_stands()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var uniqueName = $"Different Stand {Guid.NewGuid():N}";

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.MarketContacts.AddRange(
                new MarketContact
                {
                    Id = firstId,
                    Market = "Covent",
                    Name = uniqueName,
                    StandOrLocation = "A12",
                    Active = true
                },
                new MarketContact
                {
                    Id = secondId,
                    Market = "Covent",
                    Name = uniqueName,
                    StandOrLocation = "B27",
                    Active = true
                });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser);
        var response = await client.PostAsync("/api/v1/assistant/fix-safe-validations", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var rows = verifyDb.MarketContacts.Where(contact => contact.Id == firstId || contact.Id == secondId).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.True(row.Active));
        Assert.Contains(rows, row => row.StandOrLocation == "A12");
        Assert.Contains(rows, row => row.StandOrLocation == "B27");
    }
}