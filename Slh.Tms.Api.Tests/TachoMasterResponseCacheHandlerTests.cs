using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterResponseCacheHandlerTests
{
    [Fact]
    public async Task Reuses_same_successful_snapshot_without_second_upstream_call()
    {
        var upstream = new CountingHandler();
        var cache = new TachoMasterResponseCacheHandler(NullLogger<TachoMasterResponseCacheHandler>.Instance)
        {
            InnerHandler = upstream
        };
        using var client = new HttpClient(cache) { BaseAddress = new Uri("https://api-v1-alpha.roadtech.co.uk") };
        var payload = $"{{\"test\":\"{Guid.NewGuid():N}\"}}";

        using var first = await client.PostAsync("/api/Member/GetMembersLong", new StringContent(payload));
        using var second = await client.PostAsync("/api/Member/GetMembersLong", new StringContent(payload));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, upstream.CallCount);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Does_not_cache_failed_response()
    {
        var upstream = new CountingHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var cache = new TachoMasterResponseCacheHandler(NullLogger<TachoMasterResponseCacheHandler>.Instance)
        {
            InnerHandler = upstream
        };
        using var client = new HttpClient(cache) { BaseAddress = new Uri("https://api-v1-alpha.roadtech.co.uk") };
        var payload = $"{{\"test\":\"{Guid.NewGuid():N}\"}}";

        using var first = await client.PostAsync("/api/Member/GetMemberMetrics", new StringContent(payload));
        using var second = await client.PostAsync("/api/Member/GetMemberMetrics", new StringContent(payload));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, upstream.CallCount);
    }

    private sealed class CountingHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int index;
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = statuses.Length == 0 ? HttpStatusCode.OK : statuses[Math.Min(index++, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent($"response-{CallCount}")
            });
        }
    }
}
