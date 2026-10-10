using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunProgressLiveRefreshTests
{
    [Fact]
    public async Task Run_progress_uses_samsara_stop_arrival_and_departure_snapshots()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var planningDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var load = new Load
        {
            Reference = "SAMSARA-1",
            PlanningDate = planningDate,
            Status = LoadStatus.InProgress,
            Stops =
            [
                new LoadStop { Sequence = 1, Name = "Collection site" },
                new LoadStop { Sequence = 2, Name = "Delivery site" }
            ]
        };
        db.Loads.Add(load);
        db.IntegrationMappings.AddRange(
            ProgressMapping(load.Stops[0].Id, "stop-1", """{"state":"departed","arrivalTime":"2026-10-08T08:00:00Z","departureTime":"2026-10-08T08:30:00Z"}"""),
            ProgressMapping(load.Stops[1].Id, "stop-2", """{"state":"arrived","arrivalTime":"2026-10-08T09:00:00Z","eta":"2026-10-08T09:00:00Z"}"""));
        await db.SaveChangesAsync();

        var controller = new RunProgressController(db)
        {
            ControllerContext = new ControllerContext { HttpContext = LyonsContext() }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.Get(planningDate, CancellationToken.None));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var record = Assert.Single(document.RootElement.GetProperty("records").EnumerateArray());

        Assert.Equal("SamsaraRouteAuditFeed", document.RootElement.GetProperty("source").GetString());
        Assert.Equal("OnSiteConfirmed", record.GetProperty("runState").GetString());
        Assert.Equal(1, record.GetProperty("completedStops").GetInt32());
        Assert.Equal("Delivery site", record.GetProperty("currentVisit").GetProperty("siteName").GetString());
        Assert.Equal("Collection site", record.GetProperty("stopDwell")[0].GetProperty("stopName").GetString());
        Assert.Equal("Completed", record.GetProperty("stopDwell")[0].GetProperty("state").GetString());
    }

    private static IntegrationMapping ProgressMapping(Guid stopId, string externalId, string notes) => new()
    {
        Provider = "Samsara",
        ExternalKey = externalId,
        TmsEntityType = "LoadStop",
        TmsEntityId = stopId,
        Active = true,
        Notes = notes
    };

    private static DefaultHttpContext LyonsContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim("preferred_username", "planner@lyonshaulage.com")], "Test"));
        return context;
    }
}
