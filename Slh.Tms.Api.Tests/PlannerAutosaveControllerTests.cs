using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlannerAutosaveControllerTests
{
    [Fact]
    public async Task Draft_run_can_clear_its_last_stop_without_manual_save()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var loadId = Guid.NewGuid();
        db.Loads.Add(new Load
        {
            Id = loadId,
            Reference = "RUN-AUTOSAVE-01",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.Draft,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Deliver · Test"
                }
            ]
        });
        await db.SaveChangesAsync();

        var controller = new PlannerAutosaveController(db);
        var result = await controller.UpdateStops(loadId, [], CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.Loads.Include(load => load.Stops).SingleAsync(load => load.Id == loadId);
        Assert.Empty(saved.Stops);
    }

    [Fact]
    public async Task Non_draft_run_cannot_be_cleared_to_zero_stops()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var loadId = Guid.NewGuid();
        db.Loads.Add(new Load
        {
            Id = loadId,
            Reference = "RUN-AUTOSAVE-02",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.Planned,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Deliver · Test"
                }
            ]
        });
        await db.SaveChangesAsync();

        var controller = new PlannerAutosaveController(db);
        var result = await controller.UpdateStops(loadId, [], CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var saved = await db.Loads.Include(load => load.Stops).SingleAsync(load => load.Id == loadId);
        Assert.Single(saved.Stops);
    }
}
