using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterRetryHandlerTests
{
    [Fact]
    public async Task Retries_transient_500_on_authenticated_data_endpoint()
    {
        var upstream = new SequencedHandler(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        var retry = new TachoMasterRetryHandler(NullLogger<TachoMasterRetryHandler>.Instance)
        {
            InnerHandler = upstream
        };
        using var client = new HttpClient(retry) { BaseAddress = new Uri("https://api-v1-alpha.roadtech.co.uk") };

        using var response = await client.PostAsync("/api/Member/GetMembersLong", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, upstream.CallCount);
    }

    [Fact]
    public async Task Does_not_retry_login_500_because_client_uses_alternate_password_format()
    {
        var upstream = new SequencedHandler(HttpStatusCode.InternalServerError, HttpStatusCode.OK);
        var retry = new TachoMasterRetryHandler(NullLogger<TachoMasterRetryHandler>.Instance)
        {
            InnerHandler = upstream
        };
        using var client = new HttpClient(retry) { BaseAddress = new Uri("https://api-v1-alpha.roadtech.co.uk") };

        using var response = await client.PostAsync("/api/auth/login", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, upstream.CallCount);
    }

    [Fact]
    public async Task Does_not_retry_rate_limit_response()
    {
        var upstream = new SequencedHandler(
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.OK);
        var retry = new TachoMasterRetryHandler(NullLogger<TachoMasterRetryHandler>.Instance)
        {
            InnerHandler = upstream
        };
        using var client = new HttpClient(retry) { BaseAddress = new Uri("https://api-v1-alpha.roadtech.co.uk") };

        var exception = await Assert.ThrowsAsync<TachoMasterRateLimitException>(() =>
            client.PostAsync("/api/Duty/GetDutyTransactions", new StringContent("{}")));

        Assert.Contains("429", exception.Message);
        Assert.Equal(1, upstream.CallCount);
    }

    private sealed class SequencedHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int index;
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = statuses[Math.Min(index++, statuses.Length - 1)];
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status.ToString())
            });
        }
    }
}
