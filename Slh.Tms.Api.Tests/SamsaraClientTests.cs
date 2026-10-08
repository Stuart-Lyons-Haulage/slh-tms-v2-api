using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SamsaraClientTests
{
    [Fact]
    public void Job_notes_are_capped_at_samsaras_two_thousand_character_limit()
    {
        var notes = new string('x', SamsaraNoteRules.MaximumJobNoteLength + 43);

        var result = SamsaraNoteRules.LimitJobNotes(notes);

        Assert.Equal(SamsaraNoteRules.MaximumJobNoteLength, result.Length);
        Assert.EndsWith("Samsara limit is 2,000 characters]", result);
    }

    [Fact]
    public async Task Connectivity_probe_uses_small_vehicle_and_driver_requests()
    {
        var paths = new List<string>();
        var handler = new StubHandler(request =>
        {
            paths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            return Task.FromResult(JsonResponse("""{"data":[],"pagination":{"hasNextPage":false}}"""));
        });

        var client = Client(handler);
        Assert.True(await client.CheckConnectivityAsync(CancellationToken.None));
        Assert.Contains("/fleet/vehicles?limit=1", paths);
        Assert.Contains("/fleet/drivers?limit=1", paths);
    }

    [Fact]
    public async Task Route_upsert_preserves_tms_sequence_and_schedule()
    {
        string? postedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/fleet/routes")
            {
                postedBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""
                {
                  "data": {
                    "id": "route-1",
                    "name": "SLH AM01",
                    "driver": { "id": "driver-1" },
                    "externalIds": { "slhTmsRun": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                    "stops": []
                  }
                }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var client = Client(handler);
        var runId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var firstStop = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var lastStop = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var start = new DateTimeOffset(2026, 9, 25, 18, 0, 0, TimeSpan.Zero);
        var finish = new DateTimeOffset(2026, 9, 25, 20, 0, 0, TimeSpan.Zero);

        await client.UpsertRouteAsync(
            new SamsaraRouteRequest(
                runId,
                "SLH AM01",
                "Planning authority: SLH TMS",
                "driver-1",
                null,
                [
                    new SamsaraRouteStopRequest(firstStop, 1, "SLH Depot", "address-1", "Depot", 50.0, -0.1, null, start, "Start"),
                    new SamsaraRouteStopRequest(lastStop, 2, "Customer", "address-2", "Customer", 51.0, -0.2, finish, finish, "Deliver")
                ]),
            CancellationToken.None);

        Assert.NotNull(postedBody);
        using var document = JsonDocument.Parse(postedBody!);
        var root = document.RootElement;

        Assert.True(root.GetProperty("recomputeScheduledTimes").GetBoolean());
        Assert.Equal("manual", root.GetProperty("settings").GetProperty("sequencingMethod").GetString());
        Assert.Equal("departFirstStop", root.GetProperty("settings").GetProperty("routeStartingCondition").GetString());
        Assert.Equal("departLastStop", root.GetProperty("settings").GetProperty("routeCompletionCondition").GetString());

        var stops = root.GetProperty("stops");
        Assert.Equal(1, stops[0].GetProperty("sequenceNumber").GetInt32());
        Assert.Equal("SLH Depot", stops[0].GetProperty("name").GetString());
        Assert.False(stops[0].TryGetProperty("scheduledArrivalTime", out _));
        Assert.True(stops[0].TryGetProperty("scheduledDepartureTime", out _));

        Assert.Equal(2, stops[1].GetProperty("sequenceNumber").GetInt32());
        Assert.True(stops[1].TryGetProperty("scheduledArrivalTime", out _));
        Assert.True(stops[1].TryGetProperty("scheduledDepartureTime", out _));
    }

    [Fact]
    public async Task Address_upsert_uses_site_external_id_and_geofence()
    {
        string? postedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/addresses")
            {
                postedBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""
                {
                  "data": {
                    "id": "address-99",
                    "name": "Greenhouse",
                    "formattedAddress": "Greenhouse Road",
                    "externalIds": { "slhTmsSite": "dddddddddddddddddddddddddddddddd" }
                  }
                }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var client = Client(handler);
        var siteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

        var result = await client.UpsertAddressAsync(
            new SamsaraAddressRequest(siteId, "SITE123", "Greenhouse", "Greenhouse Road", 50.84, -0.67),
            CancellationToken.None);

        Assert.Equal("address-99", result.AddressId);
        Assert.NotNull(postedBody);

        using var document = JsonDocument.Parse(postedBody!);
        var root = document.RootElement;
        Assert.Equal("SITE123", root.GetProperty("externalIds").GetProperty("slhTmsSite").GetString());
        Assert.Equal(300, root.GetProperty("geofence").GetProperty("circle").GetProperty("radiusMeters").GetInt32());
        Assert.Equal(50.84, root.GetProperty("latitude").GetDouble(), 2);
        Assert.Equal(-0.67, root.GetProperty("longitude").GetDouble(), 2);
    }

    [Fact]
    public async Task Asset_upsert_creates_a_vehicle_with_a_stable_external_id()
    {
        string? postedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse("{\"data\":[],\"pagination\":{\"hasNextPage\":false}}");

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/assets")
            {
                postedBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("{\"data\":{\"id\":\"asset-1\",\"type\":\"vehicle\",\"name\":\"BL70RHV\"}}");
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var client = Client(handler);
        var result = await client.UpsertAssetAsync(
            new SamsaraAssetRequest("vehicle", "Vehicle", "vehicle-123", "BL70RHV", "BL70RHV", "VIN-123", "SLH test vehicle", "slhTmsAsset"),
            CancellationToken.None);

        Assert.Equal("asset-1", result.Id);
        Assert.True(result.Created);
        using var document = JsonDocument.Parse(postedBody!);
        var root = document.RootElement;
        Assert.Equal("vehicle", root.GetProperty("type").GetString());
        Assert.Equal("BL70RHV", root.GetProperty("licensePlate").GetString());
        Assert.Equal("vehicle-123", root.GetProperty("externalIds").GetProperty("slhTmsAsset").GetString());
    }

    private static SamsaraClient Client(HttpMessageHandler handler)
    {
        var options = new SamsaraOptions
        {
            Enabled = true,
            BaseUrl = "https://api.eu.samsara.com",
            ApiToken = "test-token",
            ExternalIdKey = "slhTmsRun",
            StopExternalIdKey = "slhTmsStop",
            SiteExternalIdKey = "slhTmsSite",
            AssetExternalIdKey = "slhTmsAsset",
            RecomputeScheduledTimes = true,
            RouteStartingCondition = "departFirstStop",
            RouteCompletionCondition = "departLastStop",
            SequencingMethod = "manual",
            EnableAddressSync = true
        };

        return new SamsaraClient(new HttpClient(handler), options, NullLogger<SamsaraClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }
}
