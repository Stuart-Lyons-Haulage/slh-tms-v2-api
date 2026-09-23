using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Middleware;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class RunCompletionPersistenceGuardTests
{
    [Fact]
    public async Task Direct_completion_without_RunCompleted_evidence_is_rejected()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        await db.SaveChangesAsync();

        load.Status = LoadStatus.Completed;

        var exception = await Assert.ThrowsAsync<RunCompletionEvidenceException>(() => db.SaveChangesAsync());
        Assert.Equal("RUN_COMPLETION_EVIDENCE_REQUIRED", exception.Code);
        Assert.Contains("evidence-controlled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completion_with_RunCompleted_evidence_is_allowed()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        await db.SaveChangesAsync();

        load.Status = LoadStatus.Completed;
        db.DriverStatusLogs.Add(new DriverStatusLog
        {
            LoadId = load.Id,
            Status = "RunCompleted",
            Notes = "All planned stops have confirmed geofence departures.",
            CapturedBy = "RoadTech Geofence Engine"
        });

        await db.SaveChangesAsync();

        Assert.Equal(LoadStatus.Completed, (await db.Loads.SingleAsync()).Status);
        Assert.True(await db.DriverStatusLogs.AnyAsync(log => log.LoadId == load.Id && log.Status == "RunCompleted"));
    }

    [Fact]
    public async Task Persisted_RunCompleted_evidence_allows_idempotent_completion_transition()
    {
        await using var db = CreateDb();
        var load = NewLoad();
        db.Loads.Add(load);
        db.DriverStatusLogs.Add(new DriverStatusLog
        {
            LoadId = load.Id,
            Status = "RunCompleted",
            CapturedBy = "RoadTech Geofence Engine"
        });
        await db.SaveChangesAsync();

        load.Status = LoadStatus.Completed;
        await db.SaveChangesAsync();

        Assert.Equal(LoadStatus.Completed, (await db.Loads.SingleAsync()).Status);
    }

    [Fact]
    public async Task Completion_evidence_failure_is_exposed_as_conflict_not_server_error()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/api/v1/operations/loads/00000000-0000-0000-0000-000000000001/driver-status";
        context.Response.Body = new MemoryStream();

        var middleware = new PlanLockMiddleware(_ => throw new RunCompletionEvidenceException(
            "RUN_COMPLETION_EVIDENCE_REQUIRED",
            "Run completion is evidence-controlled."));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal("RUN_COMPLETION_EVIDENCE_REQUIRED", document.RootElement.GetProperty("code").GetString());
    }

    private static TmsDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TmsDbContext(options);
    }

    private static Load NewLoad() => new()
    {
        Reference = $"RUN-{Guid.NewGuid():N}",
        PlanningDate = new DateOnly(2026, 8, 23),
        Status = LoadStatus.InProgress,
        Stops = [new LoadStop { Sequence = 1, Name = "Delivery" }]
    };
}
