using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
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

        var controller = new PlannerAutosaveController(db, new PlanningStopTimingService(db, new SiteTimingRuleStore(db, NullLogger<SiteTimingRuleStore>.Instance)));
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

        var controller = new PlannerAutosaveController(db, new PlanningStopTimingService(db, new SiteTimingRuleStore(db, NullLogger<SiteTimingRuleStore>.Instance)));
        var result = await controller.UpdateStops(loadId, [], CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        var saved = await db.Loads.Include(load => load.Stops).SingleAsync(load => load.Id == loadId);
        Assert.Single(saved.Stops);
    }

    [Fact]
    public async Task Stale_If_Match_header_returns_409_Conflict()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var loadId = Guid.NewGuid();
        var serverVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 42 };
        db.Loads.Add(new Load
        {
            Id = loadId,
            Reference = "RUN-CONCURRENCY-01",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.Draft,
            RowVersion = serverVersion,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Deliver · A"
                }
            ]
        });
        await db.SaveChangesAsync();

        var controller = new PlannerAutosaveController(db, new PlanningStopTimingService(db, new SiteTimingRuleStore(db, NullLogger<SiteTimingRuleStore>.Instance)));

        // Simulate an HTTP context with a stale If-Match ETag
        var staleVersion = Convert.ToBase64String(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.IfMatch = $"\"{staleVersion}\"";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.UpdateStops(loadId, [], CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("changed by another session", conflict.Value?.ToString());
        // Verify the server's current ETag is returned in the response header
        Assert.True(httpContext.Response.Headers.ContainsKey("ETag"));
    }

    [Fact]
    public async Task Matching_If_Match_header_allows_save()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var loadId = Guid.NewGuid();
        var serverVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 42 };
        db.Loads.Add(new Load
        {
            Id = loadId,
            Reference = "RUN-CONCURRENCY-02",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.Draft,
            RowVersion = serverVersion,
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Deliver · A"
                }
            ]
        });
        await db.SaveChangesAsync();

        var controller = new PlannerAutosaveController(db, new PlanningStopTimingService(db, new SiteTimingRuleStore(db, NullLogger<SiteTimingRuleStore>.Instance)));

        // Send If-Match with the CORRECT version — save should succeed
        var matchingVersion = Convert.ToBase64String(serverVersion);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.IfMatch = $"\"{matchingVersion}\"";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.UpdateStops(loadId, [], CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        var saved = await db.Loads.Include(load => load.Stops).SingleAsync(load => load.Id == loadId);
        Assert.Empty(saved.Stops);
    }

    [Fact]
    public async Task No_If_Match_header_skips_concurrency_check()
    {
        // Existing callers that don't send If-Match should still save normally
        // (backward compatibility). This test uses a direct call without HTTP context
        // — same pattern as the original tests above.
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var loadId = Guid.NewGuid();
        db.Loads.Add(new Load
        {
            Id = loadId,
            Reference = "RUN-CONCURRENCY-03",
            PlanningDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Status = LoadStatus.Draft,
            RowVersion = new byte[] { 0, 0, 0, 0, 0, 0, 0, 99 },
            Stops =
            [
                new LoadStop
                {
                    Id = Guid.NewGuid(),
                    LoadId = loadId,
                    Sequence = 1,
                    Name = "Deliver · A"
                }
            ]
        });
        await db.SaveChangesAsync();

        // No HTTP context — Request is null, so concurrency check is skipped
        var controller = new PlannerAutosaveController(db, new PlanningStopTimingService(db, new SiteTimingRuleStore(db, NullLogger<SiteTimingRuleStore>.Instance)));
        var result = await controller.UpdateStops(loadId, [], CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }
}
