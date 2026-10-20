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
        var notes = new string('x', SamsaraNoteRules.MaximumJobNoteBytes + 43);

        var result = SamsaraNoteRules.LimitJobNotes(notes);

        Assert.Equal(SamsaraNoteRules.MaximumJobNoteBytes, result.Length);
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
                    new SamsaraRouteStopRequest(firstStop, 1, "SLH Depot", null, "Depot", 50.0, -0.1, 250, null, start, "Start"),
                    new SamsaraRouteStopRequest(lastStop, 2, "Customer", "address-2", "Customer", 51.0, -0.2, 250, finish, finish, "Deliver")
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
        Assert.Equal(50.0, stops[0].GetProperty("singleUseLocation").GetProperty("latitude").GetDouble());
        Assert.Equal(-0.1, stops[0].GetProperty("singleUseLocation").GetProperty("longitude").GetDouble());
        Assert.Equal("Depot", stops[0].GetProperty("singleUseLocation").GetProperty("address").GetString());
    }

    [Fact]
    public async Task Route_payload_omits_zero_zero_placeholder_coordinates()
    {
        string? postedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            postedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""
            { "data": { "id": "route-1", "stops": [] } }
            """);
        });

        var client = Client(handler);
        await client.UpsertRouteAsync(
            new SamsaraRouteRequest(Guid.NewGuid(), "SLH route", "plan", null, null,
            [
                new SamsaraRouteStopRequest(Guid.NewGuid(), 1, "Site A", null, "Site A address", 0, 0, 250, null, DateTimeOffset.UtcNow, null),
                new SamsaraRouteStopRequest(Guid.NewGuid(), 2, "Site B", null, "Site B address", 50.8, -0.1, 250, DateTimeOffset.UtcNow.AddHours(1), null, null)
            ]),
            CancellationToken.None);

        Assert.NotNull(postedBody);
        using var document = JsonDocument.Parse(postedBody!);
        var stops = document.RootElement.GetProperty("stops");
        var placeholderLocation = stops[0].GetProperty("singleUseLocation");
        Assert.False(placeholderLocation.TryGetProperty("latitude", out _));
        Assert.False(placeholderLocation.TryGetProperty("longitude", out _));
        Assert.Equal(50.8, stops[1].GetProperty("singleUseLocation").GetProperty("latitude").GetDouble());
    }

    [Fact]
    public async Task Route_create_retries_without_stop_external_ids_when_samsara_rejects_duplicate_arrivals()
    {
        var postedBodies = new List<string>();
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var body = await request.Content!.ReadAsStringAsync();
            postedBodies.Add(body);
            if (postedBodies.Count == 1)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"message":"cannot assign external IDs to stops, when multiple stops have the same scheduled arrival time"}""", Encoding.UTF8, "application/json")
                };

            return JsonResponse("""
            {
              "data": {
                "id": "route-duplicate-fallback",
                "externalIds": { "slhTmsRun": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                "stops": [
                  { "id": "samsara-stop-1", "sequenceNumber": 1 },
                  { "id": "samsara-stop-2", "sequenceNumber": 2 }
                ]
              }
            }
            """);
        });

        var client = Client(handler);
        var runId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var arrival = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var result = await client.UpsertRouteAsync(
            new SamsaraRouteRequest(runId, "SLH duplicate", "plan", null, null,
            [
                new SamsaraRouteStopRequest(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 1, "Depot", null, "Depot", 50, -0.1, 250, null, arrival, null),
                new SamsaraRouteStopRequest(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), 2, "Customer", null, "Customer", 51, -0.2, 250, arrival, null, null)
            ]),
            CancellationToken.None);

        Assert.True(result.Created);
        Assert.Equal("route-duplicate-fallback", result.RouteId);
        Assert.Equal(2, postedBodies.Count);
        using var original = JsonDocument.Parse(postedBodies[0]);
        using var retry = JsonDocument.Parse(postedBodies[1]);
        Assert.True(original.RootElement.GetProperty("stops")[0].TryGetProperty("externalIds", out _));
        Assert.False(retry.RootElement.GetProperty("stops")[0].TryGetProperty("externalIds", out _));
        Assert.False(retry.RootElement.GetProperty("stops")[1].TryGetProperty("externalIds", out _));
    }

    [Fact]
    public async Task Route_update_clears_stale_times_and_never_sends_departure_before_arrival()
    {
        string? patchedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse("""
                {
                  "data": {
                    "id": "route-1",
                    "externalIds": { "slhTmsRun": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
                    "stops": [
                      { "id": "stop-1", "sequenceNumber": 1, "externalIds": { "slhTmsStop": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }, "scheduledArrivalTime": "2026-10-20T08:00:00Z", "scheduledDepartureTime": "2026-10-20T08:01:00Z" },
                      { "id": "stop-2", "sequenceNumber": 2, "externalIds": { "slhTmsStop": "cccccccccccccccccccccccccccccccc" }, "scheduledArrivalTime": "2026-10-20T09:00:00Z", "scheduledDepartureTime": "2026-10-20T09:30:00Z" }
                    ]
                  }
                }
                """);

            if (request.Method == HttpMethod.Patch)
            {
                patchedBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""
                { "data": { "id": "route-1", "externalIds": { "slhTmsRun": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, "stops": [] } }
                """);
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var client = Client(handler);
        var runId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var firstStop = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var secondStop = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var arrival = new DateTimeOffset(2026, 10, 20, 8, 30, 0, TimeSpan.Zero);
        await client.UpsertRouteAsync(
            new SamsaraRouteRequest(runId, "SLH update", "plan", null, null,
            [
                new SamsaraRouteStopRequest(firstStop, 1, "Depot", null, "Depot", 50, -0.1, 250, null, null, null),
                new SamsaraRouteStopRequest(secondStop, 2, "Customer", null, "Customer", 51, -0.2, 250, arrival.AddHours(1), arrival.AddMinutes(30), null)
            ]),
            CancellationToken.None);

        Assert.NotNull(patchedBody);
        using var document = JsonDocument.Parse(patchedBody!);
        var stops = document.RootElement.GetProperty("stops");
        Assert.Equal("stop-1", stops[0].GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, stops[0].GetProperty("scheduledArrivalTime").ValueKind);
        Assert.Equal(JsonValueKind.Null, stops[0].GetProperty("scheduledDepartureTime").ValueKind);
        Assert.Equal("2026-10-20T09:30:00.0000000Z", stops[1].GetProperty("scheduledArrivalTime").GetString());
        Assert.Equal("2026-10-20T09:30:00.0000000Z", stops[1].GetProperty("scheduledDepartureTime").GetString());
    }

    [Fact]
    public async Task Address_upsert_uses_site_external_id_and_geofence()
    {
        string? postedBody = null;
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return request.RequestUri?.AbsolutePath == "/addresses"
                    ? JsonResponse("""{"data":[],"pagination":{"hasNextPage":false}}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);

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
            new SamsaraAddressRequest(siteId, "SITE123", "Greenhouse", "Greenhouse Road", 50.84, -0.67, 300),
            CancellationToken.None);

        Assert.Equal("address-99", result.AddressId);
        Assert.NotNull(postedBody);

        using var document = JsonDocument.Parse(postedBody!);
        var root = document.RootElement;
        Assert.Equal("SITE123", root.GetProperty("externalIds").GetProperty("slhTmsSite").GetString());
        Assert.Equal(300, root.GetProperty("geofence").GetProperty("circle").GetProperty("radiusMeters").GetInt32());
        Assert.False(root.TryGetProperty("latitude", out _));
        Assert.False(root.TryGetProperty("longitude", out _));
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


    [Fact]
    public async Task Address_sync_adopts_existing_numeric_id_and_uses_guid_for_invalid_site_reference()
    {
        var siteId = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        string? patchedBody = null;
        var writes = new List<string>();
        var handler = new StubHandler(async request =>
        {
            if (request.Method == HttpMethod.Get)
                return request.RequestUri!.AbsolutePath == "/addresses"
                    ? JsonResponse("""{"data":[{"id":"1000001352300","name":"ALDI DARLINGTON","formattedAddress":"Faverdale Industrial Estate, DL3 0UW","externalIds":{}}],"pagination":{"hasNextPage":false}}""")
                    : new HttpResponseMessage(HttpStatusCode.NotFound);
            writes.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            patchedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"data":{"id":"1000001352300","name":"ALDI DARLINGTON"}}""");
        });

        var result = await Client(handler).UpsertAddressAsync(
            new SamsaraAddressRequest(siteId, "Site 155", "Aldi - Darlington",
                "Faverdale Industrial Estate, DL3 0UW", null, null, 250), CancellationToken.None);

        Assert.Equal("1000001352300", result.AddressId);
        Assert.True(result.Updated);
        Assert.False(result.Created);
        Assert.Equal(new[] { "PATCH /addresses/1000001352300" }, writes);
        using var body = JsonDocument.Parse(patchedBody!);
        Assert.Equal(siteId.ToString("N"), body.RootElement.GetProperty("externalIds").GetProperty("slhTmsSite").GetString());
    }

    [Fact]
    public async Task Address_sync_refuses_to_relink_another_master_sites_saved_address()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return Task.FromResult(request.RequestUri!.AbsolutePath == "/addresses"
                ? JsonResponse("""{"data":[{"id":"saved-1","name":"Aldi Darlington","externalIds":{"slhTmsSite":"OTHER"}}],"pagination":{"hasNextPage":false}}""")
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).UpsertAddressAsync(
            new SamsaraAddressRequest(Guid.NewGuid(), "Site 155", "Aldi Darlington", "DL3 0UW", null, null, 250),
            CancellationToken.None));
    }

    [Fact]
    public void Site_match_uses_unique_name_before_shared_address_and_keeps_ambiguous_addresses_unmatched()
    {
        var runcton = new Slh.Tms.Api.Models.Site { ExternalCode = "SITE184", Name = "NWF-Runcton", CollectionAddress = "Shared food park" };
        var merston = new Slh.Tms.Api.Models.Site { ExternalCode = "SITE183", Name = "NWF Merston", CollectionAddress = "Shared food park" };
        var sites = new[] { runcton, merston };
        Assert.Same(runcton, SamsaraStopSiteMatcher.FindSite(
            new Slh.Tms.Api.Models.LoadStop { Name = "Collect NWF-Runcton", Address = "Shared food park" }, sites));
        Assert.Null(SamsaraStopSiteMatcher.FindSite(
            new Slh.Tms.Api.Models.LoadStop { Name = "Unknown site", Address = "Shared food park" }, sites));
    }

    [Fact]
    public void Site_match_prefers_delivery_destination_over_supplier_and_shared_address()
    {
        var source = new Slh.Tms.Api.Models.Site { ExternalCode = "SRC", Name = "NWF", CollectionAddress = "Source Road" };
        var destination = new Slh.Tms.Api.Models.Site { ExternalCode = "SITE167", Name = "Morrisons-Stockton", Aliases = "Morrisons STOCKTON 994", CollectionAddress = "TS18 2SZ" };
        Assert.Same(destination, SamsaraStopSiteMatcher.FindSite(
            new Slh.Tms.Api.Models.LoadStop { Name = "Deliver · NWF · Morrisons STOCKTON 994", Address = "Source Road" },
            new[] { source, destination }));
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
