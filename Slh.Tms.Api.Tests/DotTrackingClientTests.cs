using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class DotTrackingClientTests
{
    [Theory]
    [InlineData("https://api-v1-alpha.roadtech.co.uk", "https://api-v1-alpha.roadtech.co.uk/api/")]
    [InlineData("https://api-v1-alpha.roadtech.co.uk/api", "https://api-v1-alpha.roadtech.co.uk/api/")]
    [InlineData("https://api-v1-alpha.roadtech.co.uk/api/", "https://api-v1-alpha.roadtech.co.uk/api/")]
    public void RoadTech_base_url_is_normalised_to_api_root(string input, string expected)
    {
        Assert.Equal(expected, DotTrackingClient.NormaliseBaseUrl(input));
    }

    [Fact]
    public async Task RoadTech_client_calls_documented_falcon_paths()
    {
        var handler = new CapturingHandler();
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret",
            CompanyCode = "SLH"
        }, NullLogger<DotTrackingClient>.Instance);

        await client.GetLatestVehicleEventsAsync();

        Assert.Equal(new[]
        {
            "https://api-v1-alpha.roadtech.co.uk/api/auth/login",
            "https://api-v1-alpha.roadtech.co.uk/api/Falcon/GetCurrentTelemetry"
        }, handler.Requests.Select(request => request.RequestUri!.ToString()));

        var telemetryJson = handler.Bodies[1];
        using var payload = JsonDocument.Parse(telemetryJson);
        Assert.Equal(0x05, payload.RootElement.GetProperty("DataMask").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("OnlyLive").GetInt32());
    }

    [Fact]
    public async Task RoadTech_history_uses_documented_historical_telemetry_endpoint_with_requested_day()
    {
        var handler = new CapturingHandler();
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret",
            CompanyCode = "SLH"
        }, NullLogger<DotTrackingClient>.Instance);

        await client.GetHistoricalVehicleEventsAsync(new DateOnly(2026, 8, 25));

        Assert.Equal(new[]
        {
            "https://api-v1-alpha.roadtech.co.uk/api/auth/login",
            "https://api-v1-alpha.roadtech.co.uk/api/Falcon/GetHistoricalTelemetry"
        }, handler.Requests.Select(request => request.RequestUri!.ToString()));

        using var payload = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("2026-08-25", payload.RootElement.GetProperty("T").GetString());
        Assert.Equal(0, payload.RootElement.GetProperty("OnlyLive").GetInt32());
        Assert.Equal(0, payload.RootElement.GetProperty("Offset").GetInt32());
    }

    [Fact]
    public async Task RoadTech_login_retries_with_sha1_password_when_plain_password_is_rejected()
    {
        var handler = new CapturingHandler(rejectFirstLogin: true);
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret",
            CompanyCode = "SLH"
        }, NullLogger<DotTrackingClient>.Instance);

        await client.GetLatestVehicleEventsAsync();

        Assert.Equal(3, handler.Requests.Count);
        using var firstLogin = JsonDocument.Parse(handler.Bodies[0]);
        using var secondLogin = JsonDocument.Parse(handler.Bodies[1]);
        Assert.Equal("secret", firstLogin.RootElement.GetProperty("Pass").GetString());
        Assert.Equal("e5e9fa1ba31ecd1ae84f75caaa474f3a663f05f4", secondLogin.RootElement.GetProperty("Pass").GetString());
    }

    [Theory]
    [InlineData("01a036dbdb381bd1009ae2d604eeaaca", 2)]
    [InlineData("e5e9fa1ba31ecd1ae84f75caaa474f3a663f05f4", 2)]
    public async Task RoadTech_login_does_not_rehash_provider_supplied_hashes(string password, int expectedRequests)
    {
        var handler = new CapturingHandler();
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = password,
            CompanyCode = "SLH"
        }, NullLogger<DotTrackingClient>.Instance);

        await client.GetLatestVehicleEventsAsync();

        Assert.Equal(expectedRequests, handler.Requests.Count);
        using var login = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(password, login.RootElement.GetProperty("Pass").GetString());
    }

    [Theory]
    [InlineData("{\"Token\":\"sid-123\"}")]
    [InlineData("{\"Result\":{\"SessionId\":\"sid-123\"}}")]
    public async Task RoadTech_login_accepts_common_sid_response_shapes(string loginResponse)
    {
        var handler = new CapturingHandler(loginResponse: loginResponse);
        var client = new DotTrackingClient(new HttpClient(handler), new DotTrackingOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret",
            CompanyCode = "SLH"
        }, NullLogger<DotTrackingClient>.Instance);

        await client.GetLatestVehicleEventsAsync();

        Assert.Equal("sid-123", handler.Requests[1].Headers.GetValues("SID").Single());
    }

    [Fact]
    public void RoadTech_record_reads_documented_falcon_live_fields()
    {
        var item = JsonSerializer.Deserialize<RoadTechTelemetryItem>("""
            {
              "vehCode": "AB12 CDE",
              "vehRtid": 1234567,
              "Ign": true,
              "Moving": true,
              "dataGps": {
                "Time": "2026-08-13T08:30:00Z",
                "Lat": 54.957858,
                "Long": -1.653757,
                "KmH": 42,
                "Head": 331
              }
            }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var record = DotTelemetryRecord.FromProvider(item);

        Assert.Equal("AB12 CDE", record.VehicleIdentifier);
        Assert.Equal(54.957858m, record.Latitude);
        Assert.Equal(-1.653757m, record.Longitude);
        Assert.Equal(42m, record.SpeedKph);
        Assert.True(record.IgnitionOn);
        Assert.True(record.IsMoving);
    }

    [Fact]
    public void RoadTech_record_treats_timezone_less_falcon_time_as_utc()
    {
        var item = JsonSerializer.Deserialize<RoadTechTelemetryItem>("""
            {
              "vehCode": "AB12 CDE",
              "dataGps": { "Time": "2026-08-13T08:30:00", "Lat": 54.957858, "Long": -1.653757 }
            }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var record = DotTelemetryRecord.FromProvider(item);

        Assert.Equal(new DateTimeOffset(2026, 8, 13, 8, 30, 0, TimeSpan.Zero), record.EventTimeUtc);
    }

    [Fact]
    public void RoadTech_record_preserves_historical_top_level_event_time()
    {
        var item = JsonSerializer.Deserialize<RoadTechTelemetryItem>("""
            {
              "vehCode": "AB12 CDE",
              "EventTimeUtc": "2026-08-25T05:42:17Z",
              "dataGps": { "Lat": 50.7581, "Long": -0.7794 }
            }
            """, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var record = DotTelemetryRecord.FromProvider(item);

        Assert.Equal(new DateTimeOffset(2026, 8, 25, 5, 42, 17, TimeSpan.Zero), record.EventTimeUtc);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly bool _rejectFirstLogin;
        private readonly string _loginResponse;
        private int _loginCount;
        public CapturingHandler(bool rejectFirstLogin = false, string loginResponse = "{\"token\":\"sid-123\"}")
        {
            _rejectFirstLogin = rejectFirstLogin;
            _loginResponse = loginResponse;
        }
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(request);
            var isLogin = request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase);
            if (isLogin && _rejectFirstLogin && _loginCount++ == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Internal Server Error") };
            }
            var content = isLogin
                ? _loginResponse
                : "{\"moreData\":false,\"recordOffset\":0,\"recordCount\":0,\"data\":[]}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
