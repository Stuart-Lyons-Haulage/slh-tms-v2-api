using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningRegisterStoreRecencyTests
{
    [Fact]
    public async Task Current_plan_is_not_hidden_behind_older_promoted_register_rows()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        var today = new DateOnly(2026, 8, 27);
        var baseline = new DateTimeOffset(2026, 8, 27, 8, 0, 0, TimeSpan.Zero);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var rows = new List<StagedImport>();

        for (var index = 0; index < 2050; index++)
        {
            var load = new Load
            {
                Id = Guid.NewGuid(),
                Reference = $"OLD-{index:0000}",
                PlanningDate = today.AddDays(-30),
                Status = LoadStatus.Completed
            };
            rows.Add(RegisterRow(load, baseline.AddDays(-30).AddSeconds(index), jsonOptions));
        }

        for (var index = 1; index <= 34; index++)
        {
            var load = new Load
            {
                Id = Guid.NewGuid(),
                Reference = $"PLAN-20260827-RUN-{index:000}",
                PlanningDate = today,
                Status = LoadStatus.Planned,
                Stops = [new LoadStop { Sequence = 1, Name = $"Stop {index}" }]
            };
            rows.Add(RegisterRow(load, baseline.AddMinutes(index), jsonOptions));
        }

        db.StagedImports.AddRange(rows);
        await db.SaveChangesAsync();

        var loads = await PlanningRegisterStore.ReadLoadsAsync(db, today, CancellationToken.None);

        Assert.Equal(34, loads.Count);
        Assert.Equal(34, loads.Select(load => load.Reference).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(loads, load => Assert.Equal(today, load.PlanningDate));
    }

    private static StagedImport RegisterRow(Load load, DateTimeOffset receivedAtUtc, JsonSerializerOptions options) => new()
    {
        EntityType = "planningload",
        IdempotencyKey = $"planningload:{load.Id:N}",
        PayloadJson = JsonSerializer.Serialize(load, options),
        Status = StagingStatus.Promoted,
        Source = "test planning register",
        ReceivedAtUtc = receivedAtUtc,
        ReviewedAtUtc = receivedAtUtc
    };
}
