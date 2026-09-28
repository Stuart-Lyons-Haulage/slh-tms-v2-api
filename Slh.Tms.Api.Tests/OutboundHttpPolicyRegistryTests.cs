using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OutboundHttpPolicyRegistryTests
{
    [Fact]
    public async Task Samsara_does_not_retry_route_post_after_gateway_timeout()
    {
        var registry = new OutboundHttpPolicyRegistry(NullLoggerFactory.Instance);
        var policy = registry.Get("Samsara");
        var attempts = 0;

        var response = await policy.ExecuteAsync(() =>
        {
            attempts++;
            var result = new HttpResponseMessage(HttpStatusCode.GatewayTimeout)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Post, "https://api.samsara.com/fleet/routes")
            };
            return Task.FromResult(result);
        });

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(1, attempts);
    }
}
