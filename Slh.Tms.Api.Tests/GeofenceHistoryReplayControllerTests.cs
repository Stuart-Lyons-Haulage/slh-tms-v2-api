using System.Net;
using System.Net.Http.Json;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class GeofenceHistoryReplayControllerTests(CustomWebFactory factory) : IClassFixture<CustomWebFactory>
{
    [Fact]
    public async Task Rebuild_today_requires_authentication()
    {
        using var client = factory.CreateClient();
        var response = await client.PostAsync("/api/v1/geofence-history/rebuild-today", content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
