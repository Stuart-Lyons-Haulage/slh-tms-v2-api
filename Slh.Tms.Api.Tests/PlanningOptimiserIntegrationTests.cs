using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningOptimiserIntegrationTests
{
    [Fact]
    public async Task Generate_conserves_remaining_source_pallets_respects_locked_capacity_and_round_trips_without_live_mutation()
    {
        // Integration gate protected:
        // - promoted pallet allocations are subtracted exactly per source line;
        // - locked live work remains fixed and its trailer is unavailable to new work;
        // - proposal persistence/retrieval preserves the exact allocation evidence;
        // - generating/retrieving a proposal never creates or alters live Loads/Stops.
        var date = new DateOnly(2026, 8, 28);
        var lockedLoadId = Guid.NewGuid();
        var lockedTrailerId = Guid.NewGuid();
        var availableTrailerId = Guid.NewGuid();
        var firstMovementId = Guid.NewGuid();
        var secondMovementId = Guid.NewGuid();
        var firstRevisionId = Guid.NewGuid();
        var secondRevisionId = Guid.NewGuid();
        var firstSourceLineId = Guid.NewGuid();
        var secondSourceLineId = Guid.NewGuid();
        var firstStagedId = Guid.NewGuid();
        var secondStagedId = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);

        db.Trailers.AddRange(
            new Trailer
            {
                Id = lockedTrailerId,
                TrailerNumber = "LOCKED-20",
                Type = "Refrigerated",
                StandardCapacity = 20,
                EuroCapacity = 28,
                Active = true
            },
            new Trailer
            {
                Id = availableTrailerId,
                TrailerNumber = "FREE-20",
                Type = "Refrigerated",
                StandardCapacity = 20,
                EuroCapacity = 28,
                Active = true
            });

        db.Loads.Add(new Load
        {
            Id = lockedLoadId,
            Reference = "LOCKED-PLAN-01",
            PlanningDate = date,
            Status = LoadStatus.Planned,
            TrailerId = lockedTrailerId,
            Stops =
            [
                new LoadStop
                {
                    LoadId = lockedLoadId,
                    Sequence = 1,
                    Name = "Locked collection"
                }
            ]
        });

        db.StagedImports.AddRange(
            new StagedImport
            {
                Id = firstStagedId,
                EntityType = "order",
                IdempotencyKey = "optimiser-integration-order-1",
                PayloadJson = "{}",
                Status = StagingStatus.Approved
            },
            new StagedImport
            {
                Id = secondStagedId,
                EntityType = "order",
                IdempotencyKey = "optimiser-integration-order-2",
                PayloadJson = "{}",
                Status = StagingStatus.Approved
            },
            new StagedImport
            {
                EntityType = "planningpalletallocation",
                IdempotencyKey = "optimiser-integration-allocation-1",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    sourceLineId = firstSourceLineId,
                    loadId = Guid.NewGuid(),
                    pallets = 4,
                    date
                }),
                Status = StagingStatus.Promoted
            });

        db.OrderMovements.AddRange(
            new OrderMovement
            {
                Id = firstMovementId,
                CustomerCode = "ALDI",
                StableMovementKey = "ALDI:OPT-INT-1",
                CurrentRevisionId = firstRevisionId,
                LifecycleStatus = OrderMovementStatus.PlannerReady
            },
            new OrderMovement
            {
                Id = secondMovementId,
                CustomerCode = "ALDI",
                StableMovementKey = "ALDI:OPT-INT-2",
                CurrentRevisionId = secondRevisionId,
                LifecycleStatus = OrderMovementStatus.PlannerReady
            });

        db.OrderRevisions.AddRange(
            new OrderRevision
            {
                Id = firstRevisionId,
                MovementId = firstMovementId,
                StagedImportId = firstStagedId,
                RevisionNumber = 1,
                PayloadJson = "{}"
            },
            new OrderRevision
            {
                Id = secondRevisionId,
                MovementId = secondMovementId,
                StagedImportId = secondStagedId,
                RevisionNumber = 1,
                PayloadJson = "{}"
            });

        db.OrderSourceLines.AddRange(
            new OrderSourceLine
            {
                Id = firstSourceLineId,
                RevisionId = firstRevisionId,
                SourceRowKey = "Sheet1!2",
                CollectionSite = "Collection A",
                DeliverySite = "Delivery A",
                CollectionDate = date,
                DeliveryDate = date,
                CollectionTimeFrom = new TimeOnly(3, 0),
                PalletType = "Standard",
                Pallets = 12,
                PayloadJson = "{}"
            },
            new OrderSourceLine
            {
                Id = secondSourceLineId,
                RevisionId = secondRevisionId,
                SourceRowKey = "Sheet1!3",
                CollectionSite = "Collection B",
                DeliverySite = "Delivery B",
                CollectionDate = date,
                DeliveryDate = date,
                CollectionTimeFrom = new TimeOnly(4, 0),
                PalletType = "Standard",
                Pallets = 10,
                PayloadJson = "{}"
            });

        await db.SaveChangesAsync();
        await PlanLockStore.LockAsync(db, date, "planner", CancellationToken.None);

        var service = new PlanningOptimiserService(db, NullLogger<PlanningOptimiserService>.Instance);
        var generated = await service.GenerateAsync(
            new GeneratePlanProposalRequest(date, "AM"),
            "planner",
            CancellationToken.None);

        var locked = Assert.Single(generated.Runs.Where(run => run.IsLocked));
        Assert.Equal(lockedLoadId, locked.LiveLoadId);
        Assert.Equal(lockedTrailerId, locked.TrailerId);
        Assert.Equal("LOCKED-PLAN-01", locked.Reference);

        var proposed = Assert.Single(generated.Runs.Where(run => !run.IsLocked));
        Assert.Equal(availableTrailerId, proposed.TrailerId);
        Assert.Equal(20, proposed.CapacityPallets);
        Assert.Equal(18, proposed.PlannedPallets);

        var proposedBySource = proposed.Allocations
            .GroupBy(allocation => allocation.SourceLineId)
            .ToDictionary(group => group.Key, group => group.Sum(allocation => allocation.Pallets));
        Assert.Equal(8, proposedBySource[firstSourceLineId]);
        Assert.Equal(10, proposedBySource[secondSourceLineId]);
        Assert.Equal(18, proposedBySource.Values.Sum());
        Assert.All(proposed.Allocations, allocation => Assert.True(allocation.CollectionSequence < allocation.DeliverySequence));

        var liveBeforeRead = Assert.Single(await db.Loads.Include(load => load.Stops).ToListAsync());
        Assert.Equal(lockedLoadId, liveBeforeRead.Id);
        Assert.Equal(lockedTrailerId, liveBeforeRead.TrailerId);
        Assert.Equal("LOCKED-PLAN-01", liveBeforeRead.Reference);
        Assert.Single(liveBeforeRead.Stops);

        db.ChangeTracker.Clear();
        var roundTrip = await service.GetAsync(generated.Id, CancellationToken.None);
        Assert.NotNull(roundTrip);
        Assert.Equal(generated.InputHash, roundTrip!.InputHash);
        Assert.Equal(generated.Version, roundTrip.Version);

        var roundTripProposed = Assert.Single(roundTrip.Runs.Where(run => !run.IsLocked));
        Assert.Equal(availableTrailerId, roundTripProposed.TrailerId);
        Assert.Equal(18, roundTripProposed.PlannedPallets);
        Assert.Equal(
            proposed.Allocations
                .OrderBy(allocation => allocation.SourceLineId)
                .ThenBy(allocation => allocation.CollectionSequence)
                .Select(allocation => (allocation.SourceLineId, allocation.Pallets, allocation.CollectionSequence, allocation.DeliverySequence)),
            roundTripProposed.Allocations
                .OrderBy(allocation => allocation.SourceLineId)
                .ThenBy(allocation => allocation.CollectionSequence)
                .Select(allocation => (allocation.SourceLineId, allocation.Pallets, allocation.CollectionSequence, allocation.DeliverySequence)));

        var liveAfterRead = Assert.Single(await db.Loads.Include(load => load.Stops).ToListAsync());
        Assert.Equal(lockedLoadId, liveAfterRead.Id);
        Assert.Equal(lockedTrailerId, liveAfterRead.TrailerId);
        Assert.Equal("LOCKED-PLAN-01", liveAfterRead.Reference);
        Assert.Single(liveAfterRead.Stops);
        Assert.Equal("Locked collection", liveAfterRead.Stops[0].Name);
    }
}
