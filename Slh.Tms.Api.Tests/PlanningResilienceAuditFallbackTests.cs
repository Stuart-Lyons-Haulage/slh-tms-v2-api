using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Contracts;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningResilienceAuditFallbackTests
{
    [Fact]
    public async Task Full_imported_plan_is_recovered_when_only_one_live_run_survives()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var day = new DateOnly(2026, 8, 27);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        for (var number = 1; number <= 34; number++)
        {
            var run = new PlannerPlanRunRequest(
                $"RUN-{number:000}",
                $"Run {number}",
                number <= 20 ? "AM" : "PM",
                day,
                null,
                null,
                null,
                null,
                true,
                "Imported",
                null,
                [new PlannerPlanStopRequest(1, $"Collection {number}", $"Delivery {number}", 10, $"PO-{number:000}", "Standard", "06:00", "07:00", "17:00", number)]);

            db.StagedImports.Add(new StagedImport
            {
                EntityType = "plannerplanrun",
                IdempotencyKey = $"planimport:{day:yyyyMMdd}:{run.RunRef}",
                PayloadJson = JsonSerializer.Serialize(run, json),
                Status = StagingStatus.Promoted,
                Source = "Planner plan import",
                ReviewedAtUtc = new DateTimeOffset(2026, 8, 27, 4, 0, 0, TimeSpan.Zero).AddMinutes(number),
                ReviewNote = $"Imported as {PlannerPlanImportRules.TmsReference(day, run.RunRef)}."
            });
        }

        var liveRunId = Guid.NewGuid();
        var liveReference = PlannerPlanImportRules.TmsReference(day, "RUN-002");
        db.Loads.Add(new Load
        {
            Id = liveRunId,
            Reference = liveReference,
            PlanningDate = day,
            Status = LoadStatus.InProgress,
            Stops =
            [
                new LoadStop { Id = Guid.NewGuid(), LoadId = liveRunId, Sequence = 1, Name = "Collect · Runcton" },
                new LoadStop { Id = Guid.NewGuid(), LoadId = liveRunId, Sequence = 2, Name = "Deliver · Merston" }
            ]
        });
        await db.SaveChangesAsync();

        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, CancellationToken.None))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();

        Assert.Equal(34, loads.Count);
        Assert.Equal(34, loads.Select(load => load.Reference).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(liveRunId, loads.Single(load => load.Reference == liveReference).Id);
        Assert.Equal("Deliver · Merston", loads.Single(load => load.Reference == liveReference).Stops.OrderBy(stop => stop.Sequence).Last().Name);
        Assert.All(loads.Where(load => load.Id != liveRunId), load =>
        {
            Assert.NotEmpty(load.Stops);
            Assert.Contains(load.Stops, stop => stop.Name.StartsWith("Deliver · ", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task Source_line_import_recovers_all_runs_and_preserves_collection_lines()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var day = new DateOnly(2026, 8, 27);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        for (var number = 1; number <= 34; number++)
        {
            List<PlannerPlanStopRequest> sourceRows = number == 3
                ?
                [
                    new PlannerPlanStopRequest(1, "NWF-Runcton", "Merston", 8, "PO-003-A", "Standard", "06:00", "07:00", "17:00", 1),
                    new PlannerPlanStopRequest(2, "NWF-Selsey", "Merston", 6, "PO-003-B", "Standard", "07:00", "08:00", "17:00", 2)
                ]
                :
                [
                    new PlannerPlanStopRequest(1, $"Collection {number}", $"Delivery {number}", 10, $"PO-{number:000}", "Standard", "06:00", "07:00", "17:00", number)
                ];
            var run = new PlannerPlanRunRequest(
                $"RUN-{number:000}", $"Run {number}", number <= 20 ? "AM" : "PM", day,
                null, null, null, null, true, "Imported", null, sourceRows);

            db.StagedImports.Add(new StagedImport
            {
                EntityType = "plannerplansourcerun",
                IdempotencyKey = $"planimport-source:{day:yyyyMMdd}:{run.RunRef}",
                PayloadJson = JsonSerializer.Serialize(run, json),
                Status = StagingStatus.Promoted,
                Source = "Planner source-line import",
                ReviewedAtUtc = new DateTimeOffset(2026, 8, 27, 4, 0, 0, TimeSpan.Zero).AddMinutes(number)
            });
        }
        await db.SaveChangesAsync();

        var loads = await PlanningResilience.ReadLoadsAsync(db, day, CancellationToken.None);

        Assert.Equal(34, loads.Count);
        Assert.Equal(34, loads.Select(load => load.Reference).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var runThree = loads.Single(load => load.Reference == PlannerPlanImportRules.TmsReference(day, "RUN-003"));
        Assert.Equal(3, runThree.Stops.Count);
        Assert.Equal(new[] { "Collect · NWF-Runcton", "Collect · NWF-Selsey", "Deliver · Merston" },
            runThree.Stops.OrderBy(stop => stop.Sequence).Select(stop => stop.Name));
    }

    [Fact]
    public async Task Audit_projection_uses_stable_ids_across_refreshes()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        var day = new DateOnly(2026, 8, 27);
        var run = new PlannerPlanRunRequest(
            "RUN-034", "Run 34", "PM", day, null, null, null, null, true, "Imported", null,
            [new PlannerPlanStopRequest(1, "NWF", "Morrisons", 12, "PO-034", "Standard", "14:00", "15:00", "19:00", 34)]);
        var audit = new StagedImport
        {
            EntityType = "plannerplanrun",
            IdempotencyKey = $"planimport:{day:yyyyMMdd}:{run.RunRef}",
            PayloadJson = JsonSerializer.Serialize(run, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            Status = StagingStatus.Promoted,
            Source = "Planner plan import",
            ReviewedAtUtc = DateTimeOffset.UtcNow
        };
        db.StagedImports.Add(audit);
        await db.SaveChangesAsync();

        var first = await PlanningResilience.ReadLoadsAsync(db, day, CancellationToken.None);
        db.ChangeTracker.Clear();
        var second = await PlanningResilience.ReadLoadsAsync(db, day, CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Equal(first[0].Id, second[0].Id);
        Assert.Equal(first[0].Stops.Select(stop => stop.Id), second[0].Stops.Select(stop => stop.Id));
    }
}
