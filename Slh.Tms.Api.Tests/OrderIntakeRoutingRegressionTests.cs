using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OrderIntakeRoutingRegressionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public OrderIntakeRoutingRegressionTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public void Body_only_NISA_stages_with_explicit_collection_site()
    {
        var request = new MailboxEmailIntakeRequest(
            "regression-nisa-body", null, "info@lyonshaulage.com",
            "orders@barfoots.co.uk", "Barfoots",
            "NISA pallet booking 12/09/2026", DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
            "Collection point: Leythorne\nPlease book 4 pallets to Aylesford for delivery on 12/09/2026.",
            null, null, null);

        var result = new EmailOrderIntakeService().Parse(request, ["Barfoots Leythorne", "Aylesford"]);
        var order = Assert.Single(result.Orders);
        var payload = order.Payload;

        Assert.Equal("BARFOOTS LEYTHORNE", payload.GetProperty("sellerName").GetString()?.ToUpperInvariant());
        Assert.Equal(4, payload.GetProperty("pallets").GetInt32());
        Assert.Equal("Aylesford", payload.GetProperty("stallNumber").GetString());
        Assert.Contains("body.explicit", payload.GetProperty("intakeFieldSources").GetProperty("collectionSite").GetString());
    }

    [Fact]
    public void Barfoots_chained_waitrose_waves_inherit_collection_and_keep_wave_metadata()
    {
        var request = new MailboxEmailIntakeRequest(
            "regression-barfoots-waves", null, "info@lyonshaulage.com",
            "Agnieszka.Zawislan@barfoots.co.uk", "Agnieszka Zawislan",
            "Waitrose from Sefter & Leythorne for depot 09/09/26", DateTimeOffset.Parse("2026-09-08T09:49:54Z"),
            "Aylesford WAVE 1 from Sefter 2 pallets PO O78442 & Aylesford Wave 3 6 pallets PO O78431.\n" +
            "Leyland WAVE 1 from Sefter 1 pallet PO B78737 & Leyland Wave 3 2 pallets PO B78749.",
            null, null, null);

        var result = new EmailOrderIntakeService().Parse(request, ["Sefter", "Leythorne", "Aylesford", "Leyland"]);

        Assert.Equal(4, result.Orders.Count);
        Assert.All(result.Orders, order => Assert.Equal("Sefter", order.Payload.GetProperty("sellerName").GetString()));

        var waveOne = result.Orders.Where(order => order.Payload.GetProperty("wave").GetInt32() == 1).ToList();
        var waveThree = result.Orders.Where(order => order.Payload.GetProperty("wave").GetInt32() == 3).ToList();
        Assert.Equal(2, waveOne.Count);
        Assert.Equal(2, waveThree.Count);
        Assert.All(waveOne, order =>
        {
            Assert.False(order.Payload.GetProperty("overnightRoute").GetBoolean());
            Assert.Equal("SameDay", order.Payload.GetProperty("routeTiming").GetString());
        });
        Assert.All(waveThree, order =>
        {
            Assert.True(order.Payload.GetProperty("overnightRoute").GetBoolean());
            Assert.Equal("Overnight", order.Payload.GetProperty("routeTiming").GetString());
            Assert.Equal("17:00", order.Payload.GetProperty("requestedTime").GetString());
        });
    }

    [Fact]
    public async Task SummerBerry_COOP_route_and_site_alignment_never_fall_back_to_NWF_or_Drayton()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"ioana-{suffix}@summerberry.co.uk";
        var summerBerry = new Site
        {
            Id = Guid.NewGuid(), ExternalCode = $"SB-{suffix}", Name = "Summer Berry",
            DriverTextName = "Summer Berry", Active = true
        };
        var coop = new Site
        {
            Id = Guid.NewGuid(), ExternalCode = $"COOP-{suffix}", Name = $"TSBC CO-OP {suffix}",
            DriverTextName = $"TSBC CO-OP {suffix}", Active = true
        };
        var nwfDrayton = new Site
        {
            Id = Guid.NewGuid(), ExternalCode = $"NWF-{suffix}", Name = $"NWF Drayton {suffix}",
            DriverTextName = $"NWF Drayton {suffix}", Active = true
        };
        db.Sites.AddRange(summerBerry, coop, nwfDrayton);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = "COOP", SenderEmail = sender, DefaultSiteCode = summerBerry.ExternalCode,
            RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var request = new MailboxEmailIntakeRequest(
            $"summer-{suffix}", null, "info@lyonshaulage.com", sender, "Ioana",
            "TSBC COOP - 18.08.2026", DateTimeOffset.Parse("2026-08-17T09:53:57Z"),
            $"Please find attached pallet requirements. Total Pallets : 2 Collection time: 17:00 Transport at +3 degrees Collect from: Summer Berry\nDelivery to: {coop.Name}",
            null, null, null);

        var parsed = new EmailOrderIntakeService().Parse(request, [summerBerry.Name, coop.Name, nwfDrayton.Name]);
        var routed = await CustomerEmailRouteService.ApplyAsync(db, parsed, request, CancellationToken.None);
        var aligned = await EmailOrderSiteMasterAlignment.AlignAsync(db, routed, CancellationToken.None);
        var payload = Assert.Single(aligned.Orders).Payload;
        var json = payload.GetRawText();

        Assert.Equal("COOP", payload.GetProperty("customerCode").GetString());
        Assert.Equal("Summer Berry", payload.GetProperty("sellerName").GetString());
        Assert.False(json.Contains("NWF", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("Drayton", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Barfoots_sender_route_and_site_alignment_cannot_be_reassigned_to_NWF_Drayton()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var sender = $"orders-{suffix}@barfoots.co.uk";
        var leythorne = new Site
        {
            Id = Guid.NewGuid(), ExternalCode = $"LEY-{suffix}", Name = "Barfoots Leythorne",
            DriverTextName = "Barfoots Leythorne", Active = true
        };
        var aylesford = new Site
        {
            Id = Guid.NewGuid(), ExternalCode = $"AYL-{suffix}", Name = "Aylesford",
            DriverTextName = "Aylesford", Active = true
        };
        var nwfDrayton = new Site
        {
            Id = Guid.NewGuid(), ExternalCode = $"NWF-{suffix}", Name = $"NWF Drayton {suffix}",
            DriverTextName = $"NWF Drayton {suffix}", Active = true
        };
        db.Sites.AddRange(leythorne, aylesford, nwfDrayton);
        db.CustomerEmailRoutes.Add(new CustomerEmailRoute
        {
            CustomerCode = "BARFOOTS", SenderEmail = sender, DefaultSiteCode = leythorne.ExternalCode,
            RequiresReview = false, Active = true
        });
        await db.SaveChangesAsync();

        var request = new MailboxEmailIntakeRequest(
            $"barfoots-{suffix}", null, "info@lyonshaulage.com", sender, "Barfoots",
            "NISA pallet booking 12/09/2026", DateTimeOffset.Parse("2026-09-10T09:00:00Z"),
            "Collection point: Leythorne\nPlease book 4 pallets to Aylesford for delivery on 12/09/2026.",
            null, null, null);

        var parsed = new EmailOrderIntakeService().Parse(request, [leythorne.Name, aylesford.Name, nwfDrayton.Name]);
        var routed = await CustomerEmailRouteService.ApplyAsync(db, parsed, request, CancellationToken.None);
        var aligned = await EmailOrderSiteMasterAlignment.AlignAsync(db, routed, CancellationToken.None);
        var payload = Assert.Single(aligned.Orders).Payload;
        var json = payload.GetRawText();

        Assert.Equal("BARFOOTS", payload.GetProperty("customerCode").GetString());
        Assert.Equal("Barfoots Leythorne", payload.GetProperty("sellerName").GetString());
        Assert.False(json.Contains("NWF", StringComparison.OrdinalIgnoreCase));
        Assert.False(json.Contains("Drayton", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preview_endpoint_parses_verified_Barfoots_wave_format()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new MailboxEmailIntakeRequest(
            "historic-barfoots-20260909-preview", null, "info@lyonshaulage.com",
            "Agnieszka.Zawislan@barfoots.co.uk", "Agnieszka Zawislan",
            "Waitrose from Sefter & Leythorne for depot 09/09/26", DateTimeOffset.Parse("2026-09-08T09:49:54Z"),
            "Good morning,\nPlease see attached Waitrose confirmed pallet booking:\n" +
            "Aylesford WAVE 1 from Sefter 2 pallets PO O78442 & Aylesford Wave 3 6 pallets PO O78431.\n" +
            "Leyland WAVE 1 from Sefter 1 pallet PO B78737 & Leyland Wave 3 2 pallets PO B78749.",
            null, null, null);

        var response = await client.PostAsJsonAsync("/api/v1/order-intake/email/preview", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(root.GetProperty("ignored").GetBoolean());
        Assert.Equal(4, root.GetProperty("orderCount").GetInt32());
    }

    [Fact]
    public async Task Preview_endpoint_parses_verified_SummerBerry_coop_format()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        var request = new MailboxEmailIntakeRequest(
            "historic-summerberry-coop-20260913-preview", null, "info@lyonshaulage.com",
            "Martyn.Clarkson@summerberry.co.uk", "Martyn Clarkson",
            "TSBC  COOP - 13.09.2026", DateTimeOffset.Parse("2026-09-12T09:18:32Z"),
            "Good morning,\nPlease find attached pallet requirements\nTSBC COOP\n13.09.2026\n" +
            "Total Pallets: 3\nCollection time:17:00\nTransport at +3 degrees\nCollect from: TSBC, Chichester.",
            null, null, null);

        var response = await client.PostAsJsonAsync("/api/v1/order-intake/email/preview", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(root.GetProperty("ignored").GetBoolean());
        Assert.Equal(1, root.GetProperty("orderCount").GetInt32());
    }
}
