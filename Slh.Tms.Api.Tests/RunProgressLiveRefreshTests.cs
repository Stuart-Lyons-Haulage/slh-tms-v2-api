using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunProgressLiveRefreshTests
{
    [Fact]
    public void Planned_allocated_run_remains_planned_after_its_start_time_without_live_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var load = new Load
        {
            Reference = "AM-STARTED",
            PlanningDate = UkDate(now),
            Status = LoadStatus.Planned,
            VehicleId = Guid.NewGuid(),
            Stops =
            [
                new LoadStop
                {
                    LoadId = Guid.NewGuid(),
                    Sequence = 1,
                    Name = "NWF Selsey",
                    PlannedArrivalUtc = now.AddMinutes(-30)
                }
            ]
        };

        var state = RunProgressController.InferredRunState(load, load.Stops, now);

        Assert.Equal("Planned", state);
    }

    [Fact]
    public async Task Operations_progress_refreshes_the_same_live_falcon_evidence_as_the_tv_route_board()
    {
        // Regression: the Hisense route board refreshed Falcon before deriving visits,
        // while Operations read SQL only and therefore showed zero hit/progressing runs.
        // This fixture deliberately has no Site Master linkage, so configuration linkage
        // remains zero while the Falcon hit evidence must still be visible independently.
        var fence = Assert.Single(EmbeddedGeofenceEngine.ApprovedFences.Where(item => item.Name.Trim() == "Swindon (Aldi)"));
        var longitude = fence.Points.Average(point => point.Longitude);
        var latitude = fence.Points.Average(point => point.Latitude);
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true });
        var load = new Load
        {
            Id = loadId,
            Reference = "OPS-LIVE-1",
            PlanningDate = planningDate,
            Status = LoadStatus.InProgress,
            VehicleId = vehicleId,
            Stops =
            [
                new LoadStop
                {
                    Id = stopId,
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Aldi Swindon",
                    PlannedArrivalUtc = now.AddMinutes(-5)
                }
            ]
        };
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "planningload",
            IdempotencyKey = $"planningload:{loadId:N}",
            PayloadJson = JsonSerializer.Serialize(load, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Source = "test planning register",
            Status = StagingStatus.Promoted
        });
        await db.SaveChangesAsync();

        var tracking = new DotTrackingClient(
            new HttpClient(new FalconCurrentHandler(now, latitude, longitude)),
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
        var store = new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance);
        var controller = new RunProgressController(
            db,
            tracking,
            store,
            DisabledTachoMaster(),
            NullLogger<RunProgressController>.Instance,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = LyonsContext() }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.Get(null, planningDate, CancellationToken.None));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("geofenceLinkedRuns").GetInt32());
        Assert.Equal(1, root.GetProperty("geofenceHitRuns").GetInt32());
        Assert.True(root.GetProperty("trackingEventCount").GetInt32() >= 1);
        var record = Assert.Single(root.GetProperty("records").EnumerateArray());
        Assert.Equal(loadId, record.GetProperty("loadId").GetGuid());
        Assert.NotEqual(JsonValueKind.Null, record.GetProperty("currentVisit").ValueKind);
    }

    [Fact]
    public async Task Operations_progress_returns_explicit_tachomaster_sign_on_evidence()
    {
        var now = DateTimeOffset.UtcNow;
        var planningDate = UkDate(now);
        var vehicleId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var loadId = Guid.NewGuid();
        var stopId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.Vehicles.Add(new Vehicle { Id = vehicleId, Registration = "RK69 FZL", Active = true });
        db.Drivers.Add(new Driver { Id = driverId, EmployeeNumber = "SLH-42", DisplayName = "Marius Paun", TachoName = "Marius Paun", Active = true });
        var load = new Load
        {
            Id = loadId,
            Reference = "RUN 9 PM",
            PlanningDate = planningDate,
            Status = LoadStatus.Planned,
            VehicleId = vehicleId,
            DriverId = driverId,
            Stops =
            [
                new LoadStop
                {
                    Id = stopId,
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "First delivery",
                    PlannedArrivalUtc = now.AddMinutes(30)
                }
            ]
        };
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "planningload",
            IdempotencyKey = $"planningload:{loadId:N}",
            PayloadJson = JsonSerializer.Serialize(load, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Source = "test planning register",
            Status = StagingStatus.Promoted
        });
        await db.SaveChangesAsync();

        var tracking = new DotTrackingClient(new HttpClient(new FalconCurrentHandler(now, 50.8, -1.1)), new DotTrackingOptions { Enabled = false }, NullLogger<DotTrackingClient>.Instance);
        var tacho = new TachoMasterClient(
            new HttpClient(new SingleDutyTachoHandler()),
            new TachoMasterOptions
            {
                Enabled = true,
                BaseUrl = "https://api-v1-alpha.roadtech.co.uk",
                ApiKey = "test-key",
                Username = "planner",
                Password = "secret"
            },
            NullLogger<TachoMasterClient>.Instance);
        var controller = new RunProgressController(
            db,
            tracking,
            new DotTrackingTelemetryStore(db, NullLogger<DotTrackingTelemetryStore>.Instance),
            tacho,
            NullLogger<RunProgressController>.Instance,
            new ConfigurationBuilder().Build())
        {
            ControllerContext = new ControllerContext { HttpContext = LyonsContext() }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.Get(null, planningDate, CancellationToken.None));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var record = Assert.Single(document.RootElement.GetProperty("records").EnumerateArray());
        var evidence = record.GetProperty("tacho");

        Assert.Equal("Matched", evidence.GetProperty("status").GetString());
        Assert.Equal("Marius Paun", evidence.GetProperty("driverName").GetString());
        Assert.Equal("RK69FZL", evidence.GetProperty("vehicleCode").GetString());
        Assert.NotEqual(JsonValueKind.Null, evidence.GetProperty("signOnUtc").ValueKind);
    }

    private static DefaultHttpContext LyonsContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("preferred_username", "planner@lyonshaulage.com")
        ], "Test"));
        return context;
    }

    private static DateOnly UkDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }

    private static TachoMasterClient DisabledTachoMaster() => new(
        new HttpClient(new EmptyTachoHandler()),
        new TachoMasterOptions { Enabled = false },
        NullLogger<TachoMasterClient>.Instance);

    private sealed class FalconCurrentHandler(DateTimeOffset now, double latitude, double longitude) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri!.AbsolutePath.EndsWith("/auth/login", StringComparison.OrdinalIgnoreCase)
                ? "{\"token\":\"sid-123\"}"
                : JsonSerializer.Serialize(new
                {
                    moreData = false,
                    recordOffset = 0,
                    recordCount = 1,
                    data = new[]
                    {
                        new
                        {
                            vehCode = "AB12CDE",
                            Ign = true,
                            Moving = false,
                            dataGps = new { Time = now, Lat = latitude, Long = longitude, KmH = 0 }
                        }
                    }
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class EmptyTachoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
    }

    private sealed class SingleDutyTachoHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var payload = request.RequestUri!.AbsolutePath switch
            {
                "/api/auth/login" => "{\"token\":\"sid-123\"}",
                "/api/Duty/GetDutyTransactions" => """
                    {"dutyNew":{"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"vehCode":"RK69 FZL","dutyStart":"2026-08-24T05:25:00Z","dutyEnd":null,
                      "timeWork":45,"timeRest":0,"timeAvailable":0,"timeDrive":20,"wtd":[]
                    }]}}
                    """,
                "/api/Member/GetMembersLong" => """
                    {"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"cName":"Marius","sName":"Paun","cardNoShort":"GB123456789","employeeNumber":"SLH-42"
                    }]}
                    """,
                "/api/Member/GetMemberMetrics" => """
                    {"moreData":false,"recordOffset":0,"recordCount":1,"data":[{
                      "memCode":42,"dateTimeWhenValid":"2026-08-24T06:00:00Z","dailyDriverPeriodsAvaiable":1,
                      "driveAvailableToday":480,"driveAvailableTomorrow":600,"driveAvailableWeek":2400,
                      "driveAvailableFortnight":5400,"longDaysWorkedThisWeek":0,"shortDailyRestTakenThisWeek":0,
                      "workAvaiableWeek":3000
                    }]}
                    """,
                _ => throw new InvalidOperationException($"Unexpected test request {request.RequestUri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }
    }
}
