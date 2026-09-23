using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoContinuousEnrichmentTests
{
    [Fact]
    public async Task Falcon_identity_is_merged_when_TachoMaster_already_has_some_vehicle_duties()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var tachoHandler = new PartialDutyHandler(today);
        var falconHandler = new FalconHandler(now);

        var dotClient = new DotTrackingClient(
            new HttpClient(falconHandler),
            new DotTrackingOptions
            {
                Enabled = true,
                BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
                ApiKey = "test-key",
                Username = "planner",
                Password = "secret",
                CompanyCode = "SLH"
            },
            NullLogger<DotTrackingClient>.Instance);

        var client = new TachoMasterClient(
            new HttpClient(tachoHandler),
            new TachoMasterOptions
            {
                Enabled = true,
                BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
                ApiKey = "test-key",
                Username = "planner",
                Password = "secret"
            },
            NullLogger<TachoMasterClient>.Instance,
            dotClient);

        var statuses = await client.GetCurrentDriverStatusesByVehicleAsync(today);

        Assert.Equal(2, statuses.Count);
        Assert.Equal("Jane Duty", statuses["AB12CDE"].DriverName);
        Assert.Equal(90, statuses["AB12CDE"].DriveMinutes);
        Assert.Equal("Sam Falcon", statuses["XY34ZTT"].DriverName);
        Assert.Equal("CARD20000002", statuses["XY34ZTT"].CardNumber);
        Assert.Equal("FalconLiveCard", statuses["XY34ZTT"].EvidenceSource);
        Assert.Equal(0, statuses["XY34ZTT"].DriveMinutes);
        Assert.Equal(420, statuses["XY34ZTT"].DriveAvailableTodayMinutes);
        Assert.Contains("/api/Falcon/GetCurrentTelemetry", falconHandler.Paths);
    }

    private sealed class PartialDutyHandler(DateOnly today) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var start = today.ToDateTime(new TimeOnly(6, 0), DateTimeKind.Utc);
            var payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/login" => "{\"token\":\"sid-tacho\"}",
                "/api/Duty/GetDutyTransactions" => JsonSerializer.Serialize(new
                {
                    dutyNew = new
                    {
                        moreData = false,
                        recordCount = 1,
                        data = new[]
                        {
                            new
                            {
                                memCode = 1,
                                vehCode = "AB12 CDE",
                                dutyStart = start,
                                dutyEnd = (DateTimeOffset?)null,
                                timeWork = 30,
                                timeRest = 0,
                                timeAvailable = 0,
                                timeDrive = 90,
                                wtd = Array.Empty<object>()
                            }
                        }
                    }
                }),
                "/api/Member/GetMembersLong" => JsonSerializer.Serialize(new
                {
                    moreData = false,
                    recordCount = 2,
                    data = new[]
                    {
                        new { memCode = 1, cName = "Jane", sName = "Duty", cardNoShort = "CARD10000001", employeeNumber = "SLH-1" },
                        new { memCode = 2, cName = "Sam", sName = "Falcon", cardNoShort = "CARD20000002", employeeNumber = "SLH-2" }
                    }
                }),
                "/api/Member/GetMemberMetrics" => """
                    {"moreData":false,"recordCount":2,"data":[
                      {"memCode":1,"dateTimeWhenValid":"2026-08-24T06:00:00Z","dailyDriverPeriodsAvaiable":1,
                       "driveAvailableToday":300,"driveAvailableTomorrow":540,"driveAvailableWeek":1800,
                       "driveAvailableFortnight":4200,"longDaysWorkedThisWeek":0,"shortDailyRestTakenThisWeek":0,"workAvaiableWeek":2400},
                      {"memCode":2,"dateTimeWhenValid":"2026-08-24T06:00:00Z","dailyDriverPeriodsAvaiable":1,
                       "driveAvailableToday":420,"driveAvailableTomorrow":540,"driveAvailableWeek":2100,
                       "driveAvailableFortnight":4200,"longDaysWorkedThisWeek":0,"shortDailyRestTakenThisWeek":0,"workAvaiableWeek":2600}
                    ]}
                    """,
                _ => throw new InvalidOperationException($"Unexpected TachoMaster request {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class FalconHandler(DateTimeOffset now) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var payload = path switch
            {
                "/api/auth/login" => "{\"token\":\"sid-falcon\"}",
                "/api/Falcon/GetCurrentTelemetry" => JsonSerializer.Serialize(new
                {
                    moreData = false,
                    recordCount = 2,
                    data = new object[]
                    {
                        new
                        {
                            vehCode = "AB12 CDE",
                            Ign = true,
                            Moving = true,
                            driverName = "Jane Duty",
                            driverCardNumber = "CARD10000001",
                            dataGps = new { Time = now, Lat = 50.8, Long = -0.8, KmH = 45 }
                        },
                        new
                        {
                            vehCode = "XY34 ZTT",
                            Ign = true,
                            Moving = true,
                            driverName = "Sam Falcon",
                            driverCardNumber = "CARD20000002",
                            dataGps = new { Time = now, Lat = 51.0, Long = -1.0, KmH = 50 }
                        }
                    }
                }),
                _ => throw new InvalidOperationException($"Unexpected Falcon request {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
