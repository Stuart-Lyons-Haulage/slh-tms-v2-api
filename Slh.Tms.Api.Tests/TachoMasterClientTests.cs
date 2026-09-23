using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class TachoMasterClientTests
{
    [Fact]
    public async Task Transient_provider_failure_is_retried_but_auth_failure_is_not_retried_as_a_5xx()
    {
        var handler = new RetryAndAuthHandler();
        var client = new TachoMasterClient(new HttpClient(handler), new TachoMasterOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret"
        }, NullLogger<TachoMasterClient>.Instance);

        var profiles = await client.GetDriverProfilesAsync();
        Assert.Single(profiles);
        Assert.Equal(2, handler.LoginAttempts);

        var authHandler = new RetryAndAuthHandler { AlwaysRejectLogin = true };
        var authClient = new TachoMasterClient(new HttpClient(authHandler), new TachoMasterOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret"
        }, NullLogger<TachoMasterClient>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => authClient.GetDriverProfilesAsync());
        Assert.Equal(2, authHandler.LoginAttempts); // two credential forms, no 5xx retry loop
    }

    [Fact]
    public async Task Current_vehicle_status_uses_documented_duty_member_and_metric_fields()
    {
        var handler = new TachoMasterHandler();
        var client = new TachoMasterClient(new HttpClient(handler), new TachoMasterOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret"
        }, NullLogger<TachoMasterClient>.Instance);

        var statuses = await client.GetCurrentDriverStatusesByVehicleAsync(new DateOnly(2026, 8, 14));

        var status = Assert.Single(statuses).Value;
        Assert.Equal("AB12CDE", Assert.Single(statuses).Key);
        Assert.Equal("Jane Driver", status.DriverName);
        Assert.Equal("DB123456789", status.CardNumber);
        Assert.Equal("SLH-42", status.EmployeeNumber);
        Assert.Equal(75, status.DriveMinutes);
        Assert.Equal(30, status.RestMinutes);
        Assert.Equal(1, status.BreakCount);
        Assert.Equal(30, status.BreakMinutes);
        Assert.Equal(285, status.DriveAvailableTodayMinutes);
        Assert.Equal(1560, status.DriveAvailableWeekMinutes);

        Assert.Equal(new[]
        {
            "/api/auth/login",
            "/api/Duty/GetDutyTransactions",
            "/api/Member/GetMembersLong",
            "/api/Member/GetMemberMetrics"
        }, handler.Paths);
        using var dutyRequest = JsonDocument.Parse(handler.BodiesByPath["/api/Duty/GetDutyTransactions"]);
        Assert.True(dutyRequest.RootElement.EnumerateObject().Single(property => string.Equals(property.Name, "WithWtd", StringComparison.OrdinalIgnoreCase)).Value.GetBoolean());
    }

    [Fact]
    public async Task Two_drivers_on_one_vehicle_both_surface_via_GetAllDriverStatusesByVehicleAsync()
    {
        var handler = new TwoDriverOneVehicleHandler();
        var client = new TachoMasterClient(new HttpClient(handler), new TachoMasterOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret"
        }, NullLogger<TachoMasterClient>.Instance);

        var date = new DateOnly(2026, 8, 14);

        // The existing "current occupant" method must keep collapsing to the latest driver only —
        // other callers rely on that behaviour and must not change.
        var current = await client.GetCurrentDriverStatusesByVehicleAsync(date);
        var currentStatus = Assert.Single(current).Value;
        Assert.Equal("Night Driver", currentStatus.DriverName);

        // The new method must expose both drivers who used the vehicle that day.
        var all = await client.GetAllDriverStatusesByVehicleAsync(date);
        var vehicleStatuses = Assert.Single(all).Value;
        Assert.Equal(2, vehicleStatuses.Count);
        Assert.Contains(vehicleStatuses, status => status.DriverName == "Day Driver" && status.DriveMinutes == 200);
        Assert.Contains(vehicleStatuses, status => status.DriverName == "Night Driver" && status.DriveMinutes == 150);

        // The driver-aware matcher must pick the specific planned driver's own duty, not just
        // whichever driver most recently used the vehicle.
        var dayDriver = new Driver { EmployeeNumber = "SLH-1", DisplayName = "Day Driver" };
        var nightDriver = new Driver { EmployeeNumber = "SLH-2", DisplayName = "Night Driver" };
        var unmatchedDriver = new Driver { EmployeeNumber = "SLH-99", DisplayName = "Different Driver" };
        var aliases = new[] { "AB12CDE" };

        var matchedForDay = ExecutionIdentityResolver.MatchTachoForDriver(aliases, dayDriver, all);
        Assert.NotNull(matchedForDay);
        Assert.Equal("Day Driver", matchedForDay!.DriverName);

        var matchedForNight = ExecutionIdentityResolver.MatchTachoForDriver(aliases, nightDriver, all);
        Assert.NotNull(matchedForNight);
        Assert.Equal("Night Driver", matchedForNight!.DriverName);

        // A specific planned driver with no matching duty must fail closed. Falling back to the
        // latest occupant would attach another driver's legal-hours evidence to this load.
        Assert.Null(ExecutionIdentityResolver.MatchTachoForDriver(aliases, unmatchedDriver, all));

        // No driver supplied falls back to the most recent duty, matching MatchTacho's behaviour.
        var matchedWithNoDriver = ExecutionIdentityResolver.MatchTachoForDriver(aliases, null, all);
        Assert.NotNull(matchedWithNoDriver);
        Assert.Equal("Night Driver", matchedWithNoDriver!.DriverName);
    }

    [Fact]
    public async Task Open_vehicle_statuses_exclude_completed_duties_but_daily_history_keeps_them()
    {
        // Regression: a completed day-driver duty must not be treated as the live driver
        // merely because it overlaps the same operating day as the current run.
        var handler = new TwoDriverOneVehicleHandler();
        var client = Client(handler);
        var date = new DateOnly(2026, 8, 14);

        var open = await client.GetOpenDriverStatusesByVehicleAsync(date);
        var openStatuses = Assert.Single(open).Value;
        var live = Assert.Single(openStatuses);
        Assert.Equal("Night Driver", live.DriverName);
        Assert.Null(live.DutyEndUtc);

        var history = await client.GetAllDriverStatusesByVehicleAsync(date);
        Assert.Equal(2, Assert.Single(history).Value.Count);
    }

    [Fact]
    public async Task Health_reports_successful_poll_separately_from_stale_legal_hours_metrics()
    {
        // Regression: a four-hour-old open duty start must not make a successful live API
        // response look disconnected; legal-hours metric age is a separate readiness signal.
        var controller = new TachoMasterHealthController(
            Client(new TachoMasterHandler()),
            NullLogger<TachoMasterHealthController>.Instance);

        var response = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(response.Value));
        var root = json.RootElement;

        Assert.Equal("healthy", root.GetProperty("status").GetString());
        Assert.Equal("live", root.GetProperty("connectionFreshness").GetString());
        Assert.True(root.GetProperty("lastSuccessfulPollUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        Assert.Equal(1, root.GetProperty("openVehicleDuties").GetInt32());
        Assert.Equal("stale", root.GetProperty("metricsFreshness").GetString());
        Assert.True(root.GetProperty("metricsStale").GetBoolean());
    }

    private static TachoMasterClient Client(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        new TachoMasterOptions
        {
            Enabled = true,
            BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
            ApiKey = "test-key",
            Username = "planner",
            Password = "secret"
        },
        NullLogger<TachoMasterClient>.Instance);

    private sealed class TwoDriverOneVehicleHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/login" => "{\"token\":\"sid-123\"}",
                "/api/Duty/GetDutyTransactions" => """
                    {"dutyNew":{"moreData":false,"recordOffset":0,"recordCount":2,"data":[
                      {"memCode":1,"vehCode":"AB12 CDE","dutyStart":"2026-08-14T05:00:00Z","dutyEnd":"2026-08-14T13:00:00Z",
                       "timeWork":220,"timeRest":20,"timeAvailable":0,"timeDrive":200,"wtd":[]},
                      {"memCode":2,"vehCode":"AB12 CDE","dutyStart":"2026-08-14T18:00:00Z","dutyEnd":null,
                       "timeWork":170,"timeRest":20,"timeAvailable":0,"timeDrive":150,"wtd":[]}
                    ]}}
                    """,
                "/api/Member/GetMembersLong" => """
                    {"moreData":false,"recordOffset":0,"recordCount":2,"data":[
                      {"memCode":1,"cName":"Day","sName":"Driver","cardNoShort":"DB1","employeeNumber":"SLH-1"},
                      {"memCode":2,"cName":"Night","sName":"Driver","cardNoShort":"DB2","employeeNumber":"SLH-2"}
                    ]}
                    """,
                "/api/Member/GetMemberMetrics" => """
                    {"moreData":false,"recordOffset":0,"recordCount":2,"data":[
                      {"memCode":1,"dateTimeWhenValid":"2026-08-14T13:00:00Z","dailyDriverPeriodsAvaiable":1,
                       "driveAvailableToday":250,"driveAvailableTomorrow":540,"driveAvailableWeek":1500,
                       "driveAvailableFortnight":4200,"longDaysWorkedThisWeek":1,"shortDailyRestTakenThisWeek":0,"workAvaiableWeek":2000},
                      {"memCode":2,"dateTimeWhenValid":"2026-08-14T21:00:00Z","dailyDriverPeriodsAvaiable":1,
                       "driveAvailableToday":300,"driveAvailableTomorrow":540,"driveAvailableWeek":1500,
                       "driveAvailableFortnight":4200,"longDaysWorkedThisWeek":1,"shortDailyRestTakenThisWeek":0,"workAvaiableWeek":2000}
                    ]}
                    """,
                _ => throw new InvalidOperationException($"Unexpected test request {request.RequestUri}")
            };
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class TachoMasterHandler : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        public Dictionary<string, string> BodiesByPath { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Paths)
            {
                Paths.Add(path);
                BodiesByPath[path] = body;
            }
            var payload = request.RequestUri.AbsolutePath switch
            {
                "/api/auth/login" => "{\"token\":\"sid-123\"}",
                "/api/Duty/GetDutyTransactions" => """
                    {"dutyNew":{"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"vehCode":"AB12 CDE","dutyStart":"2026-08-14T06:00:00Z","dutyEnd":null,
                      "timeWork":45,"timeRest":30,"timeAvailable":15,"timeDrive":75,
                      "wtd":[{"wtdEvent":"wtdBreak","timeStart":"2026-08-14T08:00:00Z","timeEnd":"2026-08-14T08:30:00Z"}]
                    }]}}
                    """,
                "/api/Member/GetMembersLong" => """
                    {"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"cName":"Jane","sName":"Driver","cardNoShort":"DB123456789","employeeNumber":"SLH-42"
                    }]}
                    """,
                "/api/Member/GetMemberMetrics" => """
                    {"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"dateTimeWhenValid":"2026-08-14T09:00:00Z","dailyDriverPeriodsAvaiable":1,
                      "driveAvailableToday":285,"driveAvailableTomorrow":600,"driveAvailableWeek":1560,
                      "driveAvailableFortnight":4260,"longDaysWorkedThisWeek":1,"shortDailyRestTakenThisWeek":0,
                      "workAvaiableWeek":2100
                    }]}
                    """,
                _ => throw new InvalidOperationException($"Unexpected test request {request.RequestUri}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class RetryAndAuthHandler : HttpMessageHandler
    {
        public int LoginAttempts { get; private set; }
        public bool AlwaysRejectLogin { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login")
            {
                LoginAttempts++;
                if (AlwaysRejectLogin) return Task.FromResult(Response(HttpStatusCode.Unauthorized, "invalid credentials"));
                if (LoginAttempts == 1) return Task.FromResult(Response(HttpStatusCode.BadGateway, "temporary provider outage"));
                return Task.FromResult(Response(HttpStatusCode.OK, "{\"token\":\"sid-123\"}"));
            }

            var payload = path switch
            {
                "/api/Member/GetMembersLong" => "{\"moreData\":false,\"recordCount\":1,\"data\":[{\"memCode\":42,\"cName\":\"Jane\",\"sName\":\"Driver\",\"cardNoShort\":\"DB123456789\"}]}",
                "/api/Member/GetMemberMetrics" => "{\"moreData\":false,\"recordCount\":0,\"data\":[]}",
                _ => throw new InvalidOperationException($"Unexpected test request {request.RequestUri}")
            };
            return Task.FromResult(Response(HttpStatusCode.OK, payload));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}
