using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
namespace Slh.Tms.Api.Tests;
public sealed class PlannerIncidentRegressionTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;
    public PlannerIncidentRegressionTests(CustomWebFactory factory) => this.factory = factory;
    [Fact]
    public async Task Portal_can_create_a_run_and_read_it_back()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var date = "2027-02-01";
        var response = await client.PostAsJsonAsync("/api/v1/runs", new { reference = $"REGRESSION-{Guid.NewGuid():N}", planningDate = date,
            stops = new[] { new { name = "Collection" }, new { name = "Delivery" } }, palletSpacesUsed = 4, totalPalletSpaces = 26 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var runs = await client.GetFromJsonAsync<JsonElement>($"/api/v1/runs?date={date}");
        Assert.Contains(runs.EnumerateArray(), run => run.GetProperty("id").GetGuid() == created.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task New_run_endpoint_still_requires_a_reason_on_a_locked_day()
    {
        var date = new DateOnly(2027, 2, 2);
        using (var scope = factory.Services.CreateScope())
            await PlanLockStore.LockAsync(scope.ServiceProvider.GetRequiredService<TmsDbContext>(), date, "test", CancellationToken.None);
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.PostAsJsonAsync("/api/v1/runs", new { reference = "LOCKED-REGRESSION", planningDate = date,
            stops = new[] { new { name = "Collection" } } });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("PLAN_LOCKED:", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Active_subcontractor_is_visible_in_Driver_Dispatch_without_Sage_or_Tacho_identity()
    {
        var name = $"Bannisters Test {Guid.NewGuid():N}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.Add(new Driver
            {
                EmployeeNumber = $"SUB-{Guid.NewGuid():N}"[..24],
                DisplayName = name,
                DriverType = "Subcontractor",
                DriverGroup = "Bannisters",
                Active = true
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var response = await client.GetAsync("/api/v1/driver-dispatch?date=2027-02-03");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        var driver = payload.GetProperty("drivers").EnumerateArray().Single(item => item.GetProperty("displayName").GetString() == name);
        Assert.Equal("Subcontractor", driver.GetProperty("driverType").GetString());
        Assert.Equal("Bannisters", driver.GetProperty("driverGroup").GetString());
    }

    [Fact]
    public async Task Subcontractor_without_SLH_Tacho_identity_can_acknowledge_external_compliance_at_dispatch()
    {
        var date = new DateOnly(2027, 2, 4);
        Guid loadId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var driver = new Driver
            {
                EmployeeNumber = $"SUB-{Guid.NewGuid():N}"[..24],
                DisplayName = $"External Haulier {Guid.NewGuid():N}"[..28],
                DriverType = "Subcontractor",
                DriverGroup = "Bannisters",
                Active = true
            };
            var vehicle = new Vehicle { Registration = $"SUB{Random.Shared.Next(1000, 9999)}", Active = true };
            var trailer = new Trailer { TrailerNumber = $"EXT{Random.Shared.Next(1000, 9999)}", Active = true };
            var load = new Load
            {
                Reference = $"SUB-{Guid.NewGuid():N}"[..20],
                PlanningDate = date,
                Status = LoadStatus.Planned,
                DriverId = driver.Id,
                VehicleId = vehicle.Id,
                TrailerId = trailer.Id,
                Stops =
                [
                    new LoadStop { Sequence = 1, Name = "Collect · NWF-Runcton", Latitude = 50.80m, Longitude = -0.74m },
                    new LoadStop { Sequence = 2, Name = "Deliver · Aldi-Darlington", Latitude = 54.52m, Longitude = -1.55m }
                ]
            };
            db.Drivers.Add(driver);
            db.Vehicles.Add(vehicle);
            db.Trailers.Add(trailer);
            db.Loads.Add(load);
            await db.SaveChangesAsync();
            await RunOperationalStore.SaveAsync(db, load, new RunOperationalValues(18, 26, "Standard pallets", null, null, null), "test", CancellationToken.None);
            loadId = load.Id;
        }

        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        var warningResponse = await client.PostAsJsonAsync($"/api/v1/loads/{loadId}/dispatch-readiness", new { routeDrivingMinutes = 120, acknowledgeUnverified = false });
        Assert.Equal(HttpStatusCode.OK, warningResponse.StatusCode);
        var warning = await warningResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(warning.GetProperty("canDispatch").GetBoolean());
        Assert.Equal("Unverified", warning.GetProperty("status").GetString());
        Assert.Contains("subcontractor", warning.GetProperty("explanation").GetString()!, StringComparison.OrdinalIgnoreCase);

        var acknowledgedResponse = await client.PostAsJsonAsync($"/api/v1/loads/{loadId}/dispatch-readiness", new { routeDrivingMinutes = 120, acknowledgeUnverified = true });
        Assert.Equal(HttpStatusCode.OK, acknowledgedResponse.StatusCode);
        var acknowledged = await acknowledgedResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(acknowledged.GetProperty("canDispatch").GetBoolean());
        Assert.Equal("UnverifiedAcknowledged", acknowledged.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Employed", "Office", null, false)]
    [InlineData("Employed", null, null, false)]
    [InlineData("Driver Manager", "Office", null, false)]
    [InlineData("Employed", "Day Drivers", null, true)]
    [InlineData("Agency", null, null, true)]
    [InlineData("Subcontractor", "Bannisters", null, true)]
    [InlineData("Employed", "Operating Centre", "DRIVER-CARD", true)]
    public void Member_number_does_not_make_an_office_worker_a_driver(string type, string? group, string? card, bool expected)
    {
        Assert.Equal(expected, DriverPopulationRules.IsDriver(new Driver { EmployeeNumber = "TEST", DisplayName = "Test Employee", DriverType = type, DriverGroup = group,
            TachoMasterDriverId = "12345", TachoCardNumber = card }));
    }
    [Theory]
    [InlineData("Office", "Driver Manager", false)]
    [InlineData("Office", "Non-driver", false)]
    [InlineData("Office", "Administrator", false)]
    [InlineData("Drivers", "HGV Driver", true)]
    [InlineData("Transport", "HGV Driver", true)]
    public void Sage_import_requires_a_driving_role(string team, string position, bool expected)
    {
        Assert.Equal(expected, DriverPopulationRules.IsSageDriver(new SageHrEmployee(1, "1", "Test", "Employee", team, position, null), "Drivers", "Driver"));
    }
}