using System.Net;
using System.Text;
using Azure.Core;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class AzureMapsMatrixServiceTests
{
    private static readonly HgvVehicleProfile Profile = new();

    [Fact]
    public async Task ComputeMatrixAsync_ReturnsTravelTimesAndDistances()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(Json(HttpStatusCode.OK, """
            {
              "matrix": [
                [
                  { "statusCode": 200, "response": { "routeSummary": { "travelTimeInSeconds": 3600, "lengthInMeters": 80000 } } },
                  { "statusCode": 200, "response": { "routeSummary": { "travelTimeInSeconds": 1800, "lengthInMeters": 40000 } } }
                ],
                [
                  { "statusCode": 200, "response": { "routeSummary": { "travelTimeInSeconds": 900, "lengthInMeters": 15000 } } },
                  { "statusCode": 404, "response": {} }
                ]
              ]
            }
            """)));
        var service = CreateService(handler);

        var result = await service.ComputeMatrixAsync(
            [new(52.1m, -1.2m), new(53.0m, -2.0m)],
            [new(50.84m, -0.56m), new(51.5m, -1.0m)],
            Profile,
            CancellationToken.None);

        Assert.Equal(60, result[0, 0]!.TravelTimeMinutes);
        Assert.Equal(80_000, result[0, 0]!.DistanceMetres);
        Assert.Equal(15, result[1, 0]!.TravelTimeMinutes);
        Assert.Null(result[1, 1]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ComputeMatrixAsync_PollsUntilTimeoutAndIncludesRequestId()
    {
        var handler = new StubHandler((request, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") };
            response.Headers.Location = new Uri("https://atlas.microsoft.com/route/matrix/requests/matrix-123");
            return Task.FromResult(response);
        });
        var service = CreateService(handler, new AzureMapsMatrixOptions
        {
            PollTimeout = TimeSpan.FromMilliseconds(12),
            InitialPollDelay = TimeSpan.FromMilliseconds(1),
            MaxPollDelay = TimeSpan.FromMilliseconds(2),
            CacheTtl = TimeSpan.FromMinutes(20)
        });

        var error = await Assert.ThrowsAsync<MatrixTimeoutException>(() => service.ComputeMatrixAsync(
            [new(52.1m, -1.2m)], [new(50.84m, -0.56m)], Profile, CancellationToken.None));

        Assert.Equal("matrix-123", error.RequestId);
        Assert.True(handler.CallCount >= 2);
    }

    [Fact]
    public async Task ComputeMatrixAsync_UsesCachedCompletedMatrixForSameCoordinateSet()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(Json(HttpStatusCode.OK, """
            { "matrix": [[{ "statusCode": 200, "response": { "routeSummary": { "travelTimeInSeconds": 600, "lengthInMeters": 9000 } } }]] }
            """)));
        var service = CreateService(handler);
        var origins = new[] { new MatrixPoint(52.1m, -1.2m) };
        var destinations = new[] { new MatrixPoint(50.84m, -0.56m) };

        var first = await service.ComputeMatrixAsync(origins, destinations, Profile, CancellationToken.None);
        var second = await service.ComputeMatrixAsync(origins, destinations, Profile, CancellationToken.None);

        Assert.Equal(first[0, 0], second[0, 0]);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ComputeMatrixAsync_RejectsMalformedMatrixDimensions()
    {
        var handler = new StubHandler((request, _) => Task.FromResult(Json(HttpStatusCode.OK, "{\"matrix\":[[]]}")));
        var service = CreateService(handler);

        var error = await Assert.ThrowsAsync<MatrixResponseException>(() => service.ComputeMatrixAsync(
            [new(52.1m, -1.2m)], [new(50.84m, -0.56m)], Profile, CancellationToken.None));

        Assert.Contains("dimensions", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static AzureMapsMatrixService CreateService(StubHandler handler, AzureMapsMatrixOptions? options = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://atlas.microsoft.com") };
        var factory = new StubHttpClientFactory(client);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Maps:ClientId"] = "test-client-id"
        }).Build();
        return new AzureMapsMatrixService(
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            new StubCredential(),
            configuration,
            Options.Create(options ?? new AzureMapsMatrixOptions()),
            TestTelemetry.Client,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureMapsMatrixService>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubCredential : TokenCredential
    {
        private static readonly AccessToken Token = new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => Token;
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(Token);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return callback(request, cancellationToken);
        }
    }
}
