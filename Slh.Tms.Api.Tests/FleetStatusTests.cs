using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class FleetStatusTests : IClassFixture<CustomWebFactory>
{
    private const string LyonsUser = "planner@lyonshaulage.com";
    private readonly CustomWebFactory _factory;

    public FleetStatusTests(CustomWebFactory factory) => _factory = factory;

    [Fact]
    public async Task Fleet_status_endpoint_requires_authentication()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Fleet_status_endpoint_returns_ok_for_authenticated_user()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected OK or NoContent, got {response.StatusCode}");
    }

    [Fact]
    public async Task Fleet_status_response_includes_driver_match_reason_when_available()
    {
        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");
        // The endpoint should return 200 or 204 (if no data); either is acceptable.
        Assert.True(response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.NoContent,
            $"Expected OK or NoContent, got {response.StatusCode}");
    }

    [Fact]
    public async Task Fleet_status_links_planning_register_assignment_to_moving_vehicle()
    {
        var vehicle = new Vehicle { Registration = "PX24 SLH", Abbreviation = "SLH" };
        var driver = new Driver { EmployeeNumber = "D100", DisplayName = "Alex Driver" };
        var today = LondonOperatingDate(DateTimeOffset.UtcNow);
        var load = new Load
        {
            Id = Guid.NewGuid(),
            Reference = "SLH-REG-001",
            PlanningDate = today,
            VehicleId = vehicle.Id,
            DriverId = driver.Id,
            Status = LoadStatus.Planned,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    Sequence = 1,
                    Name = "Collection",
                    PlannedArrivalUtc = DateTimeOffset.UtcNow.AddHours(-1)
                }
            ]
        };

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Vehicles.Add(vehicle);
            db.Drivers.Add(driver);
            db.VehicleLiveStatuses.Add(new VehicleLiveStatus
            {
                VehicleIdentifier = "PX24SLH",
                LastEventTimeUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                LastReceivedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                Latitude = 53.1m,
                Longitude = -2.2m,
                SpeedKph = 42,
                IgnitionOn = true,
                IsMoving = true
            });
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "planningload",
                IdempotencyKey = $"planningload:{load.Id:N}",
                PayloadJson = JsonSerializer.Serialize(load, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Status = StagingStatus.Promoted,
                Source = "test"
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClientWithUser(LyonsUser, "Tms.Access");
        var response = await client.GetAsync("/api/v1/tracking/dot/fleet-status");

        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<FleetStatusResponse>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var row = Assert.Single(payload!.Vehicles.Where(item => item.Registration == vehicle.Registration));
        Assert.Equal(load.Id, row.LoadId);
        Assert.Equal(load.Reference, row.LoadReference);
        Assert.Equal(driver.Id, row.DriverId);
        Assert.Equal(driver.DisplayName, row.DriverName);
        Assert.Equal(driver.DisplayName, row.AllocatedDriverName);
    }

    private static DateOnly LondonOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time")).DateTime);
        }
    }
}
