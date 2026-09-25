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

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            responder(request);
    }
}
