using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningRegisterLookupTests : IClassFixture<CustomWebFactory>
{
    private readonly CustomWebFactory _factory;

    public PlanningRegisterLookupTests(CustomWebFactory factory)
    {
        _factory = factory;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.Database.EnsureDeleted();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Single_run_lookup_uses_identity_and_does_not_depend_on_recent_register_window()
    {
        var targetId = Guid.NewGuid();
        var target = new Load
        {
            Id = targetId,
            Reference = "PLAN-20260907-10",
            PlanningDate = new DateOnly(2026, 9, 7),
            Status = LoadStatus.Draft
        };

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "planningload",
            IdempotencyKey = $"planningload:{targetId:N}",
            PayloadJson = JsonSerializer.Serialize(target),
            Status = StagingStatus.Promoted,
            ReceivedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
            ReviewedAtUtc = DateTimeOffset.UtcNow.AddDays(-2)
        });

        var newer = DateTimeOffset.UtcNow;
        db.StagedImports.AddRange(Enumerable.Range(0, 10_000).Select(index =>
        {
            var id = Guid.NewGuid();
            return new StagedImport
            {
                EntityType = "planningload",
                IdempotencyKey = $"planningload:{id:N}",
                PayloadJson = JsonSerializer.Serialize(new Load { Id = id, Reference = $"NOISE-{index}", PlanningDate = new DateOnly(2026, 9, 7) }),
                Status = StagingStatus.Promoted,
                ReceivedAtUtc = newer.AddSeconds(index),
                ReviewedAtUtc = newer.AddSeconds(index)
            };
        }));
        await db.SaveChangesAsync();

        var result = await PlanningRegisterStore.GetLoadAsync(db, targetId, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(targetId, result.Id);
    }
}
