using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class OperationalResilienceEndToEndTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory factory;

    public OperationalResilienceEndToEndTests(CustomWebFactory factory) => this.factory = factory;

    [Fact]
    public async Task Sunday_2330_run_is_a_Monday_run_on_wallboard_reporting_and_completion_evidence()
    {
        var client = factory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Write");
        client.DefaultRequestHeaders.Add("X-TV-Display-Key", "test-tv-wallboard-key-20260824");

        var monday = new DateOnly(2026, 9, 7);
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var loadId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Drivers.Add(new Driver
            {
                Id = driverId,
                EmployeeNumber = $"E2E-{suffix}",
                DisplayName = $"E2E Driver {suffix}",
                Active = true
            });
            db.Vehicles.Add(new Vehicle
            {
                Id = vehicleId,
                Registration = $"E2E{suffix[..4]}",
                Active = true
            });
            db.Loads.Add(new Load
            {
                Id = loadId,
                Reference = $"OVERNIGHT-{suffix}",
                PlanningDate = monday,
                DriverId = driverId,
                VehicleId = vehicleId,
                Status = LoadStatus.InProgress,
                Stops =
                [
                    new LoadStop
                    {
                        LoadId = loadId,
                        Sequence = 1,
                        Name = "Collect · Sunday night",
                        PlannedArrivalUtc = new DateTimeOffset(2026, 9, 6, 22, 30, 0, TimeSpan.Zero)
                    },
                    new LoadStop
                    {
                        LoadId = loadId,
                        Sequence = 2,
                        Name = "Deliver · Monday morning",
                        PlannedArrivalUtc = new DateTimeOffset(2026, 9, 7, 1, 30, 0, TimeSpan.Zero)
                    }
                ]
            });
            await db.SaveChangesAsync();
        }

        var wallboard = await client.GetAsync($"/api/v1/tv-display/live-runs?date={monday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, wallboard.StatusCode);
        var before = await Json(wallboard);
        Assert.Equal("2026-09-07", before.GetProperty("planningDate").GetString());
        Assert.Contains(before.GetProperty("runs").EnumerateArray(), row => row.GetProperty("id").GetGuid() == loadId);

        var completionAtUtc = new DateTimeOffset(2026, 9, 7, 1, 45, 0, TimeSpan.Zero);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.DriverStatusLogs.Add(new DriverStatusLog
            {
                LoadId = loadId,
                DriverId = driverId,
                Status = RunCompletionPersistenceGuard.CompletionEvidenceStatus,
                Notes = "E2E final geofence departure evidence",
                CapturedBy = "e2e",
                CapturedAtUtc = completionAtUtc
            });
            await db.SaveChangesAsync();
        }

        var complete = await client.PutAsJsonAsync($"/api/v1/loads/{loadId}/status", new { status = "Completed" });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            var persisted = await db.Loads.SingleAsync(x => x.Id == loadId);
            var evidence = await db.DriverStatusLogs.SingleAsync(x =>
                x.LoadId == loadId && x.Status == RunCompletionPersistenceGuard.CompletionEvidenceStatus);
            var london = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var completionOperatingDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(evidence.CapturedAtUtc, london).DateTime);

            Assert.Equal(monday, persisted.PlanningDate);
            Assert.Equal(monday, completionOperatingDate);
            Assert.Equal(LoadStatus.Completed, persisted.Status);
        }

        var afterResponse = await client.GetAsync($"/api/v1/tv-display/live-runs?date={monday:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, afterResponse.StatusCode);
        var after = await Json(afterResponse);
        Assert.DoesNotContain(after.GetProperty("runs").EnumerateArray(), row => row.GetProperty("id").GetGuid() == loadId);
    }

    [Fact]
    public async Task RoadTech_503_reports_Unavailable_without_turning_planned_runs_into_zero_runs()
    {
        await using var unavailableFactory = new RoadTechUnavailableWebFactory();
        var client = unavailableFactory.CreateClientWithUser("planner@lyonshaulage.com", "Tms.Access");
        client.DefaultRequestHeaders.Add("X-TV-Display-Key", "test-tv-wallboard-key-20260824");

        var date = new DateOnly(2026, 9, 10);
        var loadId = Guid.NewGuid();

        using (var scope = unavailableFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
            db.Loads.Add(new Load
            {
                Id = loadId,
                Reference = $"ROADTECH-DOWN-{Guid.NewGuid():N}"[..28],
                PlanningDate = date,
                Status = LoadStatus.Planned,
                Stops =
                [
                    new LoadStop { LoadId = loadId, Sequence = 1, Name = "Collect · resilient planner" },
                    new LoadStop { LoadId = loadId, Sequence = 2, Name = "Deliver · resilient planner" }
                ]
            });
            await db.SaveChangesAsync();
        }

        var planner = await client.GetAsync($"/api/v1/loads?date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, planner.StatusCode);
        var plannerRows = await Json(planner);
        Assert.Contains(plannerRows.EnumerateArray(), row => row.GetProperty("id").GetGuid() == loadId);

        var stateResponse = await client.GetAsync("/api/v1/tracking/state");
        Assert.Equal(HttpStatusCode.OK, stateResponse.StatusCode);
        var state = await Json(stateResponse);
        Assert.Equal("Unavailable", state.GetProperty("trackingState").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("recordCount").ValueKind);

        var progress = await client.GetAsync($"/api/v1/run-progress?date={date:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, progress.StatusCode);
        var progressJson = await Json(progress);
        Assert.True(progressJson.GetProperty("count").GetInt32() > 0);
        Assert.Contains(progressJson.GetProperty("records").EnumerateArray(),
            row => row.GetProperty("loadId").GetGuid() == loadId);
        Assert.Contains("unavailable", progressJson.GetProperty("warning").GetString() ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private sealed class RoadTechUnavailableWebFactory : CustomWebFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(configuration =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TvWallboard:AccessKey"] = "test-tv-wallboard-key-20260824"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new DotTrackingOptions
                {
                    Enabled = true,
                    BaseUrl = "https://roadtech.test",
                    ApiKey = "test",
                    CompanyCode = "SLH"
                });
                services.AddHttpClient<DotTrackingClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FixedStatusHandler(HttpStatusCode.ServiceUnavailable));
            });
        }
    }

    private sealed class FixedStatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new StringContent("RoadTech unavailable")
            });
    }
}
