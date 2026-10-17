using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class CustomerEmailRouteServiceTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public CustomerEmailRouteServiceTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Approved_exact_sender_mapping_supplies_customer_and_missing_route_defaults()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"orders-{suffix}@customer.example";
        var site = new Site { ExternalCode = $"SITE-{suffix}", Name = $"Farm {suffix}", Active = true };
        db.Sites.Add(site);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = $"CUS-{suffix}", SenderEmail = sender, DefaultSiteCode = site.ExternalCode,
            MarketKey = $"market-{suffix}", RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var parsed = new EmailIntakeParseResult(
            [new ParsedEmailOrder("one", "one", JsonSerializer.SerializeToElement(new
            {
                poNumber = "PO-1", customerCode = "UNKNOWN", collectionDate = "2026-09-13",
                deliveryDate = "2026-09-13", deliverySite = "Depot", pallets = 12, plannerReady = true
            }), [])], [], null);

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, Request(sender, "New order"), CancellationToken.None);
        var payload = result.Orders.Single().Payload;

        Assert.Equal($"CUS-{suffix}".ToUpperInvariant(), payload.GetProperty("customerCode").GetString());
        Assert.Equal(site.ExternalCode, payload.GetProperty("collectionSiteCode").GetString());
        Assert.Equal(site.Name, payload.GetProperty("sellerName").GetString());
        Assert.False(payload.TryGetProperty("marketKey", out _));
        Assert.True(payload.GetProperty("emailRouteMatched").GetBoolean());
        Assert.False(payload.GetProperty("emailRouteIdentityOnly").GetBoolean());
        Assert.False(payload.GetProperty("emailRouteRequiresReview").GetBoolean());
    }

    [Fact]
    public async Task Subject_specific_mapping_can_retain_legacy_site_defaults()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"orders-subject-{suffix}@customer.example";
        var collection = new Site { ExternalCode = $"COL-{suffix}", Name = $"Collection {suffix}", Active = true };
        var delivery = new Site { ExternalCode = $"DEL-{suffix}", Name = $"Delivery {suffix}", Active = true };
        db.Sites.AddRange(collection, delivery);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = $"CUS-{suffix}", SenderEmail = sender, SubjectContains = "Waitrose",
            DefaultSiteCode = collection.ExternalCode, DefaultDeliverySiteCode = delivery.ExternalCode,
            RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var parsed = new EmailIntakeParseResult([new ParsedEmailOrder("one", "one",
            JsonSerializer.SerializeToElement(new { customerCode = "UNKNOWN", pallets = 17 }), [])], [], null);

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, Request(sender, "Waitrose order"), CancellationToken.None);
        var payload = result.Orders.Single().Payload;

        Assert.Equal(collection.ExternalCode, payload.GetProperty("collectionSiteCode").GetString());
        Assert.Equal(delivery.ExternalCode, payload.GetProperty("deliverySiteCode").GetString());
        Assert.False(payload.GetProperty("emailRouteIdentityOnly").GetBoolean());
    }

    [Fact]
    public async Task Conflicting_sender_routes_do_not_modify_the_customer_and_force_review()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"shared-{suffix}@customer.example";
        db.CustomerEmailRoutes.AddRange(
            new CustomerEmailRoute { CustomerCode = $"A-{suffix}", SenderEmail = sender, RequiresReview = false, Active = true },
            new CustomerEmailRoute { CustomerCode = $"B-{suffix}", SenderEmail = sender, RequiresReview = false, Active = true });
        await db.SaveChangesAsync();
        var payload = JsonSerializer.SerializeToElement(new { customerCode = "UNKNOWN", plannerReady = true });
        var parsed = new EmailIntakeParseResult([new ParsedEmailOrder("one", "one", payload, [])], [], null);

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, Request(sender, "Order"), CancellationToken.None);
        var resultPayload = result.Orders.Single().Payload;

        Assert.Equal("UNKNOWN", resultPayload.GetProperty("customerCode").GetString());
        Assert.False(resultPayload.GetProperty("plannerReady").GetBoolean());
        Assert.True(resultPayload.GetProperty("emailRouteRequiresReview").GetBoolean());
        Assert.Contains(result.Warnings, warning => warning.Contains("conflicting CRM routes", StringComparison.OrdinalIgnoreCase));
        Assert.False(await CustomerEmailRouteService.HasApprovedRouteAsync(db, Request(sender, "Order"), CancellationToken.None));
    }

    [Fact]
    public async Task Approved_domain_mapping_identifies_customer_and_fills_missing_route_defaults()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var domain = $"mapped-{suffix}.example";
        var collection = new Site { ExternalCode = $"COL-{suffix}", Name = $"Collection {suffix}", Active = true };
        var delivery = new Site { ExternalCode = $"DEL-{suffix}", Name = $"Delivery {suffix}", Active = true };
        db.Sites.AddRange(collection, delivery);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = $"CUS-{suffix}", SenderDomain = domain,
            DefaultSiteCode = collection.ExternalCode, DefaultDeliverySiteCode = delivery.ExternalCode,
            RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var request = Request($"planner@{domain}", "Daily pallets");
        Assert.True(await CustomerEmailRouteService.HasApprovedRouteAsync(db, request, CancellationToken.None));
        var parsed = new EmailIntakeParseResult([new ParsedEmailOrder("one", "same-movement",
            JsonSerializer.SerializeToElement(new { customerCode = "UNKNOWN", pallets = 17 }), [])], [], null);

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, request, CancellationToken.None);
        var payload = result.Orders.Single().Payload;

        Assert.Equal($"CUS-{suffix}".ToUpperInvariant(), payload.GetProperty("customerCode").GetString());
        Assert.Equal(collection.ExternalCode, payload.GetProperty("collectionSiteCode").GetString());
        Assert.Equal(delivery.ExternalCode, payload.GetProperty("deliverySiteCode").GetString());
        Assert.Equal(delivery.Name, payload.GetProperty("stallNumber").GetString());
        Assert.False(payload.GetProperty("emailRouteIdentityOnly").GetBoolean());
    }

    [Fact]
    public async Task Approved_sender_mapping_does_not_overwrite_parsed_collection_or_destination()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"no-overwrite-{suffix}@customer.example";
        var collection = new Site { ExternalCode = $"COL-{suffix}", Name = $"Collection {suffix}", Active = true };
        var delivery = new Site { ExternalCode = $"DEL-{suffix}", Name = $"Delivery {suffix}", Active = true };
        db.Sites.AddRange(collection, delivery);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = $"CUS-{suffix}", SenderEmail = sender,
            DefaultSiteCode = collection.ExternalCode, DefaultDeliverySiteCode = delivery.ExternalCode,
            RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var parsed = new EmailIntakeParseResult([new ParsedEmailOrder("one", "one",
            JsonSerializer.SerializeToElement(new
            {
                customerCode = $"CUS-{suffix}".ToUpperInvariant(), sellerName = "Email Farm",
                stallNumber = "Email Depot", pallets = 17, plannerReady = true
            }), [])], [], null);

        var result = await CustomerEmailRouteService.ApplyAsync(db, parsed, Request(sender, "Daily pallets"), CancellationToken.None);
        var payload = result.Orders.Single().Payload;

        Assert.Equal("Email Farm", payload.GetProperty("sellerName").GetString());
        Assert.Equal("Email Depot", payload.GetProperty("stallNumber").GetString());
        Assert.False(payload.TryGetProperty("collectionSiteCode", out _));
        Assert.False(payload.TryGetProperty("deliverySiteCode", out _));
        Assert.False(payload.GetProperty("emailRouteRequiresReview").GetBoolean());
    }

    [Fact]
    public async Task Approved_orders_learn_sender_customer_only_without_clearing_route_defaults()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"repeat-{suffix}@customer.example";
        var customer = $"CUS-{suffix}";

        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = customer.ToUpperInvariant(), SenderEmail = sender,
            DefaultSiteCode = $"OLD-{suffix}", DefaultDeliverySiteCode = $"DEL-{suffix}",
            MarketKey = $"market-{suffix}", RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        await CustomerEmailRouteService.LearnFromApprovedOrderAsync(db,
            JsonSerializer.SerializeToElement(new { sourceSender = sender, collectionSiteCode = $"ONE-{suffix}" }),
            customer, CancellationToken.None);
        await db.SaveChangesAsync();

        var route = db.CustomerEmailRoutes.Single(item => item.SenderEmail == sender);
        Assert.Equal(customer.ToUpperInvariant(), route.CustomerCode);
        Assert.Equal($"OLD-{suffix}", route.DefaultSiteCode);
        Assert.Equal($"DEL-{suffix}", route.DefaultDeliverySiteCode);
        Assert.Equal($"market-{suffix}", route.MarketKey);
        Assert.False(route.RequiresReview);
    }

    private static MailboxEmailIntakeRequest Request(string sender, string subject) => new(
        Guid.NewGuid().ToString("N"), null, "info@lyonshaulage.com", sender, null, subject,
        DateTimeOffset.UtcNow, null, null, null, []);
}
