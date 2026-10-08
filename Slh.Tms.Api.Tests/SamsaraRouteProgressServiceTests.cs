using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class SamsaraRouteProgressServiceTests
{
    [Fact]
    public async Task Samsara_departures_record_stop_events_and_complete_the_run()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"samsara-complete-{Guid.NewGuid():N}")
            .Options;
        await using var db = new TmsDbContext(options);
        var load = new Load
        {
            Reference = "AM-SAM-1",
            PlanningDate = new DateOnly(2026, 10, 8),
            Status = LoadStatus.Dispatched,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collection", Address = "Site 1" },
                new LoadStop { Sequence = 2, Name = "Delivery", Address = "Site 2" }
            ]
        };
        db.Loads.Add(load);
        db.IntegrationMappings.AddRange(
            RouteMapping("route-complete", "Load", load.Id),
            RouteMapping("stop-a", "LoadStop", load.Stops[0].Id),
            RouteMapping("stop-b", "LoadStop", load.Stops[1].Id));
        await db.SaveChangesAsync();

        var handler = new StubHandler(_ => Task.FromResult(JsonResponse("""
        {
          "data": [{
            "time": "2026-10-08T12:00:00Z",
            "operation": "route updated",
            "route": { "id": "route-complete" },
            "changes": { "after": { "stops": [
              { "id": "stop-a", "state": "departed", "arrivalTime": "2026-10-08T09:00:00Z", "departureTime": "2026-10-08T09:30:00Z" },
              { "id": "stop-b", "state": "departed", "arrivalTime": "2026-10-08T11:00:00Z", "departureTime": "2026-10-08T11:30:00Z" }
            ] } }
          }],
          "pagination": { "endCursor": "complete-cursor", "hasNextPage": false }
        }
        """)));
        var samsara = new SamsaraClient(new HttpClient(handler), new SamsaraOptions
        {
            Enabled = true,
            BaseUrl = "https://api.samsara.com",
            ApiToken = "test-token"
        }, NullLogger<SamsaraClient>.Instance);

        var result = await new SamsaraRouteProgressService(db, samsara, NullLogger<SamsaraRouteProgressService>.Instance)
            .PollAsync(CancellationToken.None);

        Assert.Equal(2, result.StopsUpdated);
        Assert.Equal(LoadStatus.Completed, (await db.Loads.SingleAsync()).Status);
        Assert.Equal(2, await db.DriverStatusLogs.CountAsync(log => log.Status == "SamsaraStopDeparted"));
        Assert.True(await db.DriverStatusLogs.AnyAsync(log => log.LoadId == load.Id && log.Status == "RunCompleted"));
    }

    [Fact]
    public async Task Poll_persists_only_latest_stop_progress_and_cursor()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase($"samsara-progress-{Guid.NewGuid():N}")
            .Options;

        await using var db = new TmsDbContext(options);
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        db.IntegrationMappings.AddRange(
            new IntegrationMapping
            {
                Provider = "Samsara",
                ExternalKey = "route-1",
                ExternalLabel = "AM01",
                TmsEntityType = "Load",
                TmsEntityId = loadId,
                Active = true,
                MappingKind = "ProviderIdentity"
            },
            new IntegrationMapping
            {
                Provider = "Samsara",
                ExternalKey = "stop-1",
                ExternalLabel = "Waitrose Bracknell",
                TmsEntityType = "LoadStop",
                TmsEntityId = stopId,
                Active = true,
                MappingKind = "ProviderIdentity"
            });
        await db.SaveChangesAsync();

        var handler = new StubHandler(request =>
        {
            Assert.Equal("/fleet/routes/audit-logs/feed", request.RequestUri?.AbsolutePath);
            return Task.FromResult(JsonResponse("""
            {
              "data": [
                {
                  "time": "2026-09-25T12:55:00Z",
                  "type": "route tracking",
                  "source": "automatic",
                  "operation": "stop arrived",
                  "route": { "id": "route-1", "name": "SLH AM01" },
                  "changes": {
                    "after": {
                      "stops": [
                        {
                          "id": "stop-1",
                          "state": "arrived",
                          "arrivalTime": "2026-09-25T12:54:40Z",
                          "eta": "2026-09-25T12:54:00Z",
                          "liveSharingUrl": "https://example.invalid/route"
                        }
                      ]
                    }
                  }
                }
              ],
              "pagination": {
                "endCursor": "cursor-2",
                "hasNextPage": false
              }
            }
            """));
        });

        var samsaraOptions = new SamsaraOptions
        {
            Enabled = true,
            BaseUrl = "https://api.samsara.com",
            ApiToken = "test-token",
            EnableRouteProgressSync = true
        };
        var client = new SamsaraClient(new HttpClient(handler), samsaraOptions, NullLogger<SamsaraClient>.Instance);
        var service = new SamsaraRouteProgressService(db, client, NullLogger<SamsaraRouteProgressService>.Instance);

        var result = await service.PollAsync(CancellationToken.None);

        Assert.True(result.Configured);
        Assert.Equal(1, result.EventsRead);
        Assert.Equal(1, result.StopsUpdated);
        Assert.Equal("cursor-2", result.EndCursor);

        var stopMapping = await db.IntegrationMappings.SingleAsync(item =>
            item.Provider == "Samsara" &&
            item.TmsEntityType == "LoadStop" &&
            item.TmsEntityId == stopId);
        var progress = SamsaraRouteProgressService.ReadProgress(stopMapping.Notes);

        Assert.NotNull(progress);
        Assert.Equal("arrived", progress!.State);
        Assert.Equal("stop arrived", progress.Operation);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 54, 40, TimeSpan.Zero), progress.ArrivalTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 25, 12, 54, 0, TimeSpan.Zero), progress.EstimatedArrivalTime);

        var cursor = await db.IntegrationMappings.SingleAsync(item =>
            item.Provider == "Samsara" &&
            item.TmsEntityType == "SyncCursor" &&
            item.MappingKind == "RouteAuditCursor");
        Assert.Equal("cursor-2", cursor.ExternalKey);

        // The route audit JSON is not retained as a separate operational record.
        Assert.Equal(3, await db.IntegrationMappings.CountAsync());
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static IntegrationMapping RouteMapping(string externalKey, string entityType, Guid entityId) => new()
    {
        Provider = "Samsara",
        ExternalKey = externalKey,
        TmsEntityType = entityType,
        TmsEntityId = entityId,
        Active = true,
        MappingKind = "ProviderIdentity"
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }
}
