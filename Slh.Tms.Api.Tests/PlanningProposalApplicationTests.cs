using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningProposalApplicationTests
{
    [Fact]
    public async Task Apply_creates_only_new_draft_work_preserves_locked_run_and_is_single_use()
    {
        var date = new DateOnly(2026, 8, 28);
        var proposalId = Guid.NewGuid();
        var lockedLoadId = Guid.NewGuid();
        var sourceLineId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var trailerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);

        db.Loads.Add(new Load
        {
            Id = lockedLoadId,
            Reference = "LOCKED-01",
            PlanningDate = date,
            Status = LoadStatus.Planned,
            Stops = [new LoadStop { LoadId = lockedLoadId, Sequence = 1, Name = "Existing locked stop" }]
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = Guid.NewGuid(),
            SourceRowKey = "Sheet1!2",
            CollectionDate = date,
            CollectionSite = "Collection A",
            DeliverySite = "Delivery A",
            PalletType = "Standard",
            Pallets = 18,
            PayloadJson = "{}"
        });
        db.PlanProposals.Add(new PlanProposal
        {
            Id = proposalId,
            PlanningDate = date,
            Period = "AM",
            Version = 1,
            Status = "Generated",
            Classification = "Recommended",
            InputHash = "hash",
            EvidenceJson = "{}",
            WarningsJson = "[]",
            Runs =
            [
                new PlanProposalRun
                {
                    ProposalId = proposalId,
                    Sequence = 1,
                    Reference = "LOCKED-01",
                    IsLocked = true,
                    LiveLoadId = lockedLoadId,
                    Classification = "Recommended",
                    PositionSource = "LockedPlan",
                    CapacityPallets = 0,
                    PlannedPallets = 0,
                    ScoreComponentsJson = "[]",
                    ExplanationJson = "[]"
                },
                new PlanProposalRun
                {
                    ProposalId = proposalId,
                    Sequence = 2,
                    Reference = "OPT-20260828-AM-02",
                    Classification = "Recommended",
                    DriverId = driverId,
                    VehicleId = vehicleId,
                    TrailerId = trailerId,
                    PositionSource = "LiveTracking",
                    CapacityPallets = 26,
                    PlannedPallets = 18,
                    ScoreComponentsJson = "[]",
                    ExplanationJson = "[]",
                    Allocations =
                    [
                        new PlanProposalAllocation
                        {
                            SourceLineId = sourceLineId,
                            Pallets = 18,
                            PalletType = "Standard",
                            CollectionSite = "Collection A",
                            DeliverySite = "Delivery A",
                            CollectionSequence = 1,
                            DeliverySequence = 2
                        }
                    ]
                }
            ]
        });
        await db.SaveChangesAsync();

        var service = new PlanningProposalApplicationService(db);
        var result = await service.ApplyAsync(proposalId, new ApplyPlanProposalRequest(), "planner", CancellationToken.None);

        Assert.Equal("Applied", result.Status);
        Assert.Equal(1, result.CreatedRunCount);
        var loads = await db.Loads.Include(load => load.Stops).OrderBy(load => load.Reference).ToListAsync();
        Assert.Equal(2, loads.Count);
        var locked = Assert.Single(loads.Where(load => load.Id == lockedLoadId));
        Assert.Equal("LOCKED-01", locked.Reference);
        Assert.Equal("Existing locked stop", Assert.Single(locked.Stops).Name);
        var created = Assert.Single(loads.Where(load => load.Id != lockedLoadId));
        Assert.Equal("RUN-20260828-AM-02", created.Reference);
        Assert.Equal(LoadStatus.Draft, created.Status);
        Assert.Equal(driverId, created.DriverId);
        Assert.Equal(vehicleId, created.VehicleId);
        Assert.Equal(trailerId, created.TrailerId);
        Assert.Equal(18m, created.PalletSpacesUsed);
        Assert.Equal(26m, created.TotalPalletSpaces);
        Assert.Equal(new[] { "Collect · Collection A", "Deliver · Delivery A" }, created.Stops.OrderBy(stop => stop.Sequence).Select(stop => stop.Name));

        var allocation = Assert.Single(await db.StagedImports.Where(item => item.EntityType == "planningpalletallocation").ToListAsync());
        Assert.Equal(StagingStatus.Promoted, allocation.Status);
        Assert.Contains(sourceLineId.ToString(), allocation.PayloadJson, StringComparison.OrdinalIgnoreCase);

        var second = await Assert.ThrowsAsync<PlanProposalApplyException>(() =>
            service.ApplyAsync(proposalId, new ApplyPlanProposalRequest(), "planner", CancellationToken.None));
        Assert.Equal("ProposalAlreadyFinalised", second.Code);
    }

    [Fact]
    public async Task Apply_requires_explicit_unverified_acknowledgement_and_never_allows_blocked_work()
    {
        var date = new DateOnly(2026, 8, 29);
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        var unverified = Proposal(date, "Unverified");
        var blocked = Proposal(date.AddDays(1), "Blocked");
        db.PlanProposals.AddRange(unverified, blocked);
        await db.SaveChangesAsync();
        var service = new PlanningProposalApplicationService(db);

        var missingAck = await Assert.ThrowsAsync<PlanProposalApplyException>(() =>
            service.ApplyAsync(unverified.Id, new ApplyPlanProposalRequest(false), "planner", CancellationToken.None));
        Assert.Equal("UnverifiedAcknowledgementRequired", missingAck.Code);
        Assert.Empty(await db.Loads.ToListAsync());

        var blockedError = await Assert.ThrowsAsync<PlanProposalApplyException>(() =>
            service.ApplyAsync(blocked.Id, new ApplyPlanProposalRequest(true), "planner", CancellationToken.None));
        Assert.Equal("ProposalBlocked", blockedError.Code);
        Assert.Empty(await db.Loads.ToListAsync());
    }

    [Fact]
    public async Task Apply_aborts_without_writes_when_live_pallet_balance_changed()
    {
        var date = new DateOnly(2026, 8, 30);
        var sourceLineId = Guid.NewGuid();
        var proposal = Proposal(date, "Recommended", sourceLineId, 12);
        var options = new DbContextOptionsBuilder<TmsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new TmsDbContext(options);
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = Guid.NewGuid(),
            SourceRowKey = "Sheet1!9",
            CollectionDate = date,
            Pallets = 16,
            PayloadJson = "{}"
        });
        db.StagedImports.Add(new StagedImport
        {
            EntityType = "planningpalletallocation",
            IdempotencyKey = "existing-allocation",
            Status = StagingStatus.Promoted,
            PayloadJson = $"{{\"sourceLineId\":\"{sourceLineId}\",\"loadId\":\"{Guid.NewGuid()}\",\"date\":\"{date:yyyy-MM-dd}\",\"pallets\":8}}"
        });
        db.PlanProposals.Add(proposal);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<PlanProposalApplyException>(() =>
            new PlanningProposalApplicationService(db).ApplyAsync(proposal.Id, new ApplyPlanProposalRequest(), "planner", CancellationToken.None));

        Assert.Equal("PalletConflict", error.Code);
        Assert.Empty(await db.Loads.ToListAsync());
        Assert.Equal("Generated", (await db.PlanProposals.SingleAsync()).Status);
    }

    private static PlanProposal Proposal(DateOnly date, string classification, Guid? sourceLineId = null, int pallets = 0)
    {
        var id = Guid.NewGuid();
        var run = new PlanProposalRun
        {
            ProposalId = id,
            Sequence = 1,
            Reference = $"OPT-{date:yyyyMMdd}-AM-01",
            Classification = classification,
            DriverId = Guid.NewGuid(),
            VehicleId = Guid.NewGuid(),
            PositionSource = "LiveTracking",
            CapacityPallets = 26,
            PlannedPallets = pallets,
            ScoreComponentsJson = "[]",
            ExplanationJson = "[]"
        };
        if (sourceLineId is not null)
        {
            run.Allocations.Add(new PlanProposalAllocation
            {
                SourceLineId = sourceLineId.Value,
                Pallets = pallets,
                PalletType = "Standard",
                CollectionSite = "Collection",
                DeliverySite = "Delivery",
                CollectionSequence = 1,
                DeliverySequence = 2
            });
        }
        return new PlanProposal
        {
            Id = id,
            PlanningDate = date,
            Period = "AM",
            Version = 1,
            Status = "Generated",
            Classification = classification,
            InputHash = "hash",
            EvidenceJson = "{}",
            WarningsJson = "[]",
            Runs = [run]
        };
    }
}
