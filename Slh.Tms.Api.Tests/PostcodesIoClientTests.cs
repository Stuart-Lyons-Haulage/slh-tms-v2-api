using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PostcodesIoClientTests
{
    [Theory]
    [InlineData("po22 0aq", "PO22 0AQ")]
    [InlineData("PO220AQ", "PO22 0AQ")]
    [InlineData(" ec1a1bb ", "EC1A 1BB")]
    [InlineData("M1 1AA", "M1 1AA")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("NOTAPOSTCODE", null)]
    public void NormalisePostcode_canonicalises_or_rejects(string? input, string? expected)
        => Assert.Equal(expected, PostcodesIoClient.NormalisePostcode(input));

    [Fact]
    public async Task GeocodeAsync_returns_coordinates()
    {
        var client = Client((_, _) => Json(HttpStatusCode.OK, """
            {"status":200,"result":{"postcode":"PO22 0AQ","latitude":50.822,"longitude":-0.633}}
            """));

        var point = await client.GeocodeAsync("po22 0aq", CancellationToken.None);

        Assert.NotNull(point);
        Assert.Equal(50.822m, point!.Value.Latitude);
        Assert.Equal(-0.633m, point.Value.Longitude);
    }

    [Fact]
    public async Task GeocodeAsync_returns_null_for_unknown_postcode()
    {
        var client = Client((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{\"status\":404}", Encoding.UTF8, "application/json")
        });

        Assert.Null(await client.GeocodeAsync("ZZ99 9ZZ", CancellationToken.None));
    }

    [Fact]
    public async Task GeocodeAsync_returns_null_for_blank_postcode_without_calling_the_service()
    {
        var called = false;
        var client = Client((_, _) =>
        {
            called = true;
            return Json(HttpStatusCode.OK, "{}");
        });

        Assert.Null(await client.GeocodeAsync("   ", CancellationToken.None));
        Assert.False(called);
    }

    [Fact]
    public async Task BulkGeocodeAsync_maps_resolved_postcodes_and_skips_misses()
    {
        var client = Client((_, _) => Json(HttpStatusCode.OK, """
            {"status":200,"result":[
              {"query":"PO22 0AQ","result":{"latitude":50.822,"longitude":-0.633}},
              {"query":"ZZ99 9ZZ","result":null}
            ]}
            """));

        var map = await client.BulkGeocodeAsync(["po22 0aq", "zz99 9zz"], CancellationToken.None);

        Assert.True(map.ContainsKey("PO22 0AQ"));
        Assert.False(map.ContainsKey("ZZ99 9ZZ"));
        Assert.Equal(50.822m, map["PO22 0AQ"].Latitude);
    }

    private static PostcodesIoClient Client(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        var http = new HttpClient(new StubHandler(responder)) { BaseAddress = new Uri("https://api.postcodes.io/") };
        return new PostcodesIoClient(http, NullLogger<PostcodesIoClient>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) =>
        new(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }
}
