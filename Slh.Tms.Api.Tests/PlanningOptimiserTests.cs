using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningOptimiserTests
{
    [Fact]
    public async Task Identical_operational_evidence_has_the_same_hash_when_generated_later()
    {
        // Defect protected: proposal identity changes merely because the Generate
        // button was pressed later, even though all operational evidence is identical.
        var date = new DateOnly(2026, 8, 27);
        var movementId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.StagedImports.Add(new StagedImport
        {
            Id = stagedId,
            EntityType = "order",
            IdempotencyKey = "optimiser-deterministic-order",
            PayloadJson = "{}",
            Status = StagingStatus.Approved
        });
        db.OrderMovements.Add(new OrderMovement
        {
            Id = movementId,
            CustomerCode = "ALDI",
            StableMovementKey = "ALDI:PO-DETERMINISTIC",
            CurrentRevisionId = revisionId,
            LifecycleStatus = OrderMovementStatus.PlannerReady
        });
        db.OrderRevisions.Add(new OrderRevision
        {
            Id = revisionId,
            MovementId = movementId,
            StagedImportId = stagedId,
            RevisionNumber = 1,
            PayloadJson = "{}"
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            RevisionId = revisionId,
            SourceRowKey = "Sheet1!2",
            CollectionSite = "Collection D",
            DeliverySite = "Delivery D",
            CollectionDate = date,
            DeliveryDate = date,
            CollectionTimeFrom = new TimeOnly(3, 0),
            PalletType = "Standard",
            Pallets = 16,
            PayloadJson = "{}"
        });
        await db.SaveChangesAsync();

        var firstService = new PlanningOptimiserService(
            db,
            NullLogger<PlanningOptimiserService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero)));
        var first = await firstService.GenerateAsync(new GeneratePlanProposalRequest(date, "AM"), "planner", CancellationToken.None);
        db.ChangeTracker.Clear();
        var laterService = new PlanningOptimiserService(
            db,
            NullLogger<PlanningOptimiserService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero)));
        var later = await laterService.GenerateAsync(new GeneratePlanProposalRequest(date, "AM"), "planner", CancellationToken.None);

        Assert.NotEqual(first.Id, later.Id);
        Assert.NotEqual(first.Version, later.Version);
        Assert.Equal(first.InputHash, later.InputHash);
        Assert.Equal(
            first.Runs.Select(run => (run.Reference, run.CapacityPallets, run.PlannedPallets)),
            later.Runs.Select(run => (run.Reference, run.CapacityPallets, run.PlannedPallets)));

        db.Trailers.Add(new Trailer
        {
            TrailerNumber = "SLH20",
            Type = "Refrigerated",
            StandardCapacity = 20,
            EuroCapacity = 28,
            Active = true
        });
        await db.SaveChangesAsync();
        var changedEvidenceService = new PlanningOptimiserService(
            db,
            NullLogger<PlanningOptimiserService>.Instance,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero)));
        var changedEvidence = await changedEvidenceService.GenerateAsync(
            new GeneratePlanProposalRequest(date, "AM"),
            "planner",
            CancellationToken.None);

        Assert.NotEqual(later.InputHash, changedEvidence.InputHash);
        Assert.Equal(20, Assert.Single(changedEvidence.Runs).CapacityPallets);
    }

    [Fact]
    public async Task Generate_uses_configured_trailer_capacity_and_conserves_split_pallets()
    {
        // Defect protected: the optimiser silently uses the 26-pallet default when
        // a selected trailer has a configured capacity, or loses pallets when splitting.
        var date = new DateOnly(2026, 8, 26);
        var movementId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sourceLineId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var trailerId = Guid.NewGuid();
        var secondTrailerId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.Trailers.AddRange(
            new Trailer
            {
                Id = trailerId,
                TrailerNumber = "SLH20",
                Type = "Refrigerated",
                StandardCapacity = 20,
                EuroCapacity = 28,
                Active = true
            },
            new Trailer
            {
                Id = secondTrailerId,
                TrailerNumber = "SLH21",
                Type = "Refrigerated",
                StandardCapacity = 20,
                EuroCapacity = 28,
                Active = true
            });
        db.StagedImports.Add(new StagedImport
        {
            Id = stagedId,
            EntityType = "order",
            IdempotencyKey = "optimiser-capacity-order",
            PayloadJson = "{}",
            Status = StagingStatus.Approved
        });
        db.OrderMovements.Add(new OrderMovement
        {
            Id = movementId,
            CustomerCode = "ALDI",
            StableMovementKey = "ALDI:PO-CAPACITY",
            CurrentRevisionId = revisionId,
            LifecycleStatus = OrderMovementStatus.PlannerReady
        });
        db.OrderRevisions.Add(new OrderRevision
        {
            Id = revisionId,
            MovementId = movementId,
            StagedImportId = stagedId,
            RevisionNumber = 1,
            PayloadJson = "{}"
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = revisionId,
            SourceRowKey = "Sheet1!2",
            CollectionSite = "Collection C",
            DeliverySite = "Delivery C",
            CollectionDate = date,
            DeliveryDate = date,
            CollectionTimeFrom = new TimeOnly(3, 0),
            PalletType = "Standard",
            Pallets = 25,
            PayloadJson = "{}"
        });
        await db.SaveChangesAsync();

        var service = new PlanningOptimiserService(db, NullLogger<PlanningOptimiserService>.Instance);
        var proposal = await service.GenerateAsync(new GeneratePlanProposalRequest(date, "AM"), "planner", CancellationToken.None);

        var runs = proposal.Runs.Where(run => !run.IsLocked).OrderBy(run => run.Sequence).ToList();
        Assert.Equal(2, runs.Count);
        Assert.All(runs, run =>
        {
            Assert.Equal(20, run.CapacityPallets);
        });
        Assert.Equal(2, runs.Select(run => run.TrailerId).Distinct().Count());
        Assert.Contains(runs, run => run.TrailerId == trailerId);
        Assert.Contains(runs, run => run.TrailerId == secondTrailerId);
        Assert.Equal(25, runs.SelectMany(run => run.Allocations).Sum(allocation => allocation.Pallets));
        Assert.Equal(new[] { 20, 5 }, runs.Select(run => run.PlannedPallets).ToArray());
        Assert.All(runs.SelectMany(run => run.Allocations), allocation => Assert.True(allocation.CollectionSequence < allocation.DeliverySequence));
    }

    [Fact]
    public async Task Generate_carries_locked_live_runs_as_fixed_without_mutating_them()
    {
        // Defect protected: generating a whole-day proposal omits locked work or
        // silently treats it as movable/new work.
        var date = new DateOnly(2026, 8, 25);
        var loadId = Guid.NewGuid();
        var movementId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sourceLineId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.Loads.Add(new Load
        {
            Id = loadId,
            Reference = "LOCKED-01",
            PlanningDate = date,
            Status = LoadStatus.Planned,
            Stops = [new LoadStop { LoadId = loadId, Sequence = 1, Name = "Locked collection" }]
        });
        db.StagedImports.Add(new StagedImport
        {
            Id = stagedId,
            EntityType = "order",
            IdempotencyKey = "optimiser-locked-order",
            PayloadJson = "{}",
            Status = StagingStatus.Approved
        });
        db.OrderMovements.Add(new OrderMovement
        {
            Id = movementId,
            CustomerCode = "ALDI",
            StableMovementKey = "ALDI:PO-LOCK",
            CurrentRevisionId = revisionId,
            LifecycleStatus = OrderMovementStatus.PlannerReady
        });
        db.OrderRevisions.Add(new OrderRevision
        {
            Id = revisionId,
            MovementId = movementId,
            StagedImportId = stagedId,
            RevisionNumber = 1,
            PayloadJson = "{}"
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = revisionId,
            SourceRowKey = "Sheet1!2",
            CollectionSite = "Collection B",
            DeliverySite = "Delivery B",
            CollectionDate = date,
            DeliveryDate = date,
            CollectionTimeFrom = new TimeOnly(3, 0),
            PalletType = "Standard",
            Pallets = 12,
            PayloadJson = "{}"
        });
        await db.SaveChangesAsync();
        await PlanLockStore.LockAsync(db, date, "planner", CancellationToken.None);

        var service = new PlanningOptimiserService(db, NullLogger<PlanningOptimiserService>.Instance);
        var proposal = await service.GenerateAsync(new GeneratePlanProposalRequest(date, "AM"), "planner", CancellationToken.None);

        var locked = Assert.Single(proposal.Runs.Where(run => run.IsLocked));
        Assert.Equal(loadId, locked.LiveLoadId);
        Assert.Equal("LOCKED-01", locked.Reference);
        var generated = Assert.Single(proposal.Runs.Where(run => !run.IsLocked));
        Assert.Equal(12, Assert.Single(generated.Allocations).Pallets);
        var live = Assert.Single(await db.Loads.Include(load => load.Stops).ToListAsync());
        Assert.Equal("LOCKED-01", live.Reference);
        Assert.Single(live.Stops);
    }

    [Fact]
    public async Task Generate_persists_selected_live_position_driver_vehicle_and_score_evidence()
    {
        // Defect protected: the ranker exists in isolation but generated proposals
        // discard its selected driver/vehicle, position source and score explanation.
        var now = DateTimeOffset.UtcNow;
        var date = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);
        var movementId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sourceLineId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var secondDriverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var secondVehicleId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.Drivers.AddRange(
            new Driver { Id = driverId, EmployeeNumber = "D-001", DisplayName = "Driver One", DriverGroup = "Day Drivers", Active = true },
            new Driver { Id = secondDriverId, EmployeeNumber = "D-002", DisplayName = "Driver Two", DriverGroup = "Day Drivers", Active = true });
        db.Vehicles.AddRange(
            new Vehicle { Id = vehicleId, Registration = "AB12CDE", Active = true },
            new Vehicle { Id = secondVehicleId, Registration = "CD34EFG", Active = true });
        db.VehicleLiveStatuses.AddRange(
            new Slh.Tms.Api.Models.Tracking.VehicleLiveStatus
            {
                VehicleIdentifier = "AB12CDE",
                LastEventTimeUtc = now.AddMinutes(-5),
                LastReceivedAtUtc = now.AddMinutes(-5),
                Latitude = 54.2m,
                Longitude = -1.5m
            },
            new Slh.Tms.Api.Models.Tracking.VehicleLiveStatus
            {
                VehicleIdentifier = "CD34EFG",
                LastEventTimeUtc = now.AddMinutes(-5),
                LastReceivedAtUtc = now.AddMinutes(-5),
                Latitude = 51.0m,
                Longitude = -0.1m
            });
        db.Sites.AddRange(
            new Site { ExternalCode = "COLL", Name = "Northern Collection", Active = true },
            new Site { ExternalCode = "DEL", Name = "Southern Delivery", Active = true });
        db.StagedImports.AddRange(
            new StagedImport
            {
                Id = stagedId,
                EntityType = "order",
                IdempotencyKey = "optimiser-ranked-order",
                PayloadJson = "{}",
                Status = StagingStatus.Approved
            },
            new StagedImport
            {
                EntityType = "masterdetail:driver",
                IdempotencyKey = "masterdetail:driver:d001",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    employeeNumber = "D-001",
                    tachoDriveAvailableTodayMinutes = 500,
                    lastTachoSyncUtc = now.AddMinutes(-10)
                }),
                Status = StagingStatus.Promoted
            },
            new StagedImport
            {
                EntityType = "masterdetail:driver",
                IdempotencyKey = "masterdetail:driver:d002",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    employeeNumber = "D-002",
                    tachoDriveAvailableTodayMinutes = 500,
                    lastTachoSyncUtc = now.AddMinutes(-10)
                }),
                Status = StagingStatus.Promoted
            },
            new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = "masterdetail:site:coll",
                PayloadJson = "{\"externalCode\":\"COLL\",\"latitude\":54.0,\"longitude\":-1.5}",
                Status = StagingStatus.Promoted
            },
            new StagedImport
            {
                EntityType = "masterdetail:site",
                IdempotencyKey = "masterdetail:site:del",
                PayloadJson = "{\"externalCode\":\"DEL\",\"latitude\":51.5,\"longitude\":-0.1}",
                Status = StagingStatus.Promoted
            });
        db.OrderMovements.Add(new OrderMovement
        {
            Id = movementId,
            CustomerCode = "ALDI",
            StableMovementKey = "ALDI:PO-RANK",
            CurrentRevisionId = revisionId,
            LifecycleStatus = OrderMovementStatus.PlannerReady
        });
        db.OrderRevisions.Add(new OrderRevision
        {
            Id = revisionId,
            MovementId = movementId,
            StagedImportId = stagedId,
            RevisionNumber = 1,
            PayloadJson = "{}"
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = revisionId,
            SourceRowKey = "Sheet1!2",
            CollectionSite = "Northern Collection",
            DeliverySite = "Southern Delivery",
            CollectionDate = date,
            DeliveryDate = date,
            CollectionTimeFrom = new TimeOnly(3, 0),
            PalletType = "Standard",
            Pallets = 20,
            PayloadJson = "{}"
        });
        await db.SaveChangesAsync();

        var service = new PlanningOptimiserService(db, NullLogger<PlanningOptimiserService>.Instance);
        var proposal = await service.GenerateAsync(new GeneratePlanProposalRequest(date, "AM"), "planner", CancellationToken.None);

        Assert.Equal("Recommended", proposal.Classification);
        var run = Assert.Single(proposal.Runs);
        Assert.Equal(driverId, run.DriverId);
        Assert.Equal(vehicleId, run.VehicleId);
        Assert.Equal("LiveTracking", run.PositionSource);
        Assert.Contains(run.ScoreComponents, component => component.Code == "SouthboundPositioning" && component.Value > 0);
        Assert.Contains(run.Explanations, explanation => explanation.Contains("southbound", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(4, run.Candidates.Count);
        Assert.Single(run.Candidates.Where(candidate => candidate.Selected));
        Assert.Contains(run.Candidates, candidate => candidate.Classification == "Alternative" && !candidate.Selected);
        Assert.All(run.Candidates, candidate => Assert.NotEmpty(candidate.Explanations));
        db.Vehicles.AddRange(Enumerable.Range(0, 50).Select(index => new Vehicle { Registration = $"FLEET{index:00}", Active = true }));
        await db.SaveChangesAsync();
        var large = await service.GenerateAsync(new GeneratePlanProposalRequest(date, "AM"), "planner", CancellationToken.None);
        var largeRun = Assert.Single(large.Runs);
        Assert.Equal(20, largeRun.Candidates.Count);
        Assert.Equal(run.DriverId, largeRun.DriverId);
        Assert.Equal(run.VehicleId, largeRun.VehicleId);
        Assert.Single(largeRun.Candidates.Where(candidate => candidate.Selected));
        db.ChangeTracker.Clear();
        var reloaded = await service.GetAsync(large.Id, CancellationToken.None);
        Assert.Equal(20, Assert.Single(reloaded!.Runs).Candidates.Count);
        Assert.Empty(await db.Loads.ToListAsync());
    }

    [Fact]
    public void Candidate_ranking_prefers_fresh_position_then_previous_end_and_explains_southbound_return_home()
    {
        // Defect protected: stale tracking overrides yesterday's known end location,
        // or Southbound/return-home preferences alter ranking without an explanation.
        var now = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
        var ranker = new PlanningCandidateRanker();

        var fresh = ranker.Score(new PlanningCandidateEvidence(
            VehicleId: Guid.NewGuid(),
            DriverId: Guid.NewGuid(),
            CollectionLatitude: 54.0m,
            DeliveryLatitude: 51.5m,
            LiveLatitude: 54.2m,
            LiveObservedAtUtc: now.AddMinutes(-8),
            PreviousEndLatitude: 52.0m,
            PreviousEndObservedAtUtc: now.AddDays(-1),
            ConsecutiveDays: 5,
            EvidenceCapturedAtUtc: now,
            UtilisationPercent: 90m));
        var stale = ranker.Score(new PlanningCandidateEvidence(
            VehicleId: Guid.NewGuid(),
            DriverId: Guid.NewGuid(),
            CollectionLatitude: 54.0m,
            DeliveryLatitude: 51.5m,
            LiveLatitude: 55.0m,
            LiveObservedAtUtc: now.AddHours(-7),
            PreviousEndLatitude: 53.8m,
            PreviousEndObservedAtUtc: now.AddDays(-1),
            ConsecutiveDays: 5,
            EvidenceCapturedAtUtc: now,
            UtilisationPercent: 90m));

        Assert.Equal("LiveTracking", fresh.PositionSource);
        Assert.Equal("PreviousRunEnd", stale.PositionSource);
        Assert.Contains(fresh.Components, component => component.Code == "SouthboundPositioning" && component.Value > 0);
        Assert.Contains(fresh.Components, component => component.Code == "ReturnHome" && component.Value > 0);
        Assert.Contains(fresh.Explanations, explanation => explanation.Contains("southbound", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fresh.Explanations, explanation => explanation.Contains("home", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Constraint_evidence_blocks_known_hours_breach_and_marks_stale_evidence_unverified()
    {
        // Defect protected: a known legal hours breach or stale Tacho snapshot is
        // ranked as a feasible recommendation merely because a driver is available.
        var now = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var evaluator = new PlanningConstraintEvaluator();

        var blocked = evaluator.EvaluateDriver(new PlanningDriverEvidence(
            DriverId: Guid.NewGuid(),
            RequiredDriveMinutes: 240,
            DriveAvailableTodayMinutes: 180,
            TachoObservedAtUtc: now.AddMinutes(-10),
            EvidenceCapturedAtUtc: now,
            ConsecutiveDays: 4,
            AlternatingSixthDayAllowed: false));
        var stale = evaluator.EvaluateDriver(new PlanningDriverEvidence(
            DriverId: Guid.NewGuid(),
            RequiredDriveMinutes: 120,
            DriveAvailableTodayMinutes: 300,
            TachoObservedAtUtc: now.AddHours(-7),
            EvidenceCapturedAtUtc: now,
            ConsecutiveDays: 5,
            AlternatingSixthDayAllowed: true));

        Assert.Equal("Blocked", blocked.Classification);
        Assert.Contains(blocked.Results, result => result.Code == "InsufficientDriveTime" && !result.Passed);
        Assert.Equal("Unverified", stale.Classification);
        Assert.Contains(stale.Results, result => result.Code == "TachoEvidenceStale" && !result.Passed);
        Assert.Contains(stale.Results, result => result.Code == "AlternatingSixthDay" && result.Passed);
    }

    [Fact]
    public async Task Generate_persists_an_immutable_proposal_without_creating_live_runs()
    {
        // Defect protected: proposal generation accidentally writes an optimiser
        // suggestion into live Loads or loses the source-line pallet quantity.
        var date = new DateOnly(2026, 8, 24);
        var movementId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var sourceLineId = Guid.NewGuid();
        var stagedId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TmsDbContext(options);
        db.StagedImports.Add(new StagedImport
        {
            Id = stagedId,
            EntityType = "order",
            IdempotencyKey = "optimiser-order-1",
            PayloadJson = "{}",
            Status = StagingStatus.Approved
        });
        db.OrderMovements.Add(new OrderMovement
        {
            Id = movementId,
            CustomerCode = "ALDI",
            StableMovementKey = "ALDI:PO-1001",
            CurrentRevisionId = revisionId,
            LifecycleStatus = OrderMovementStatus.PlannerReady
        });
        db.OrderRevisions.Add(new OrderRevision
        {
            Id = revisionId,
            MovementId = movementId,
            StagedImportId = stagedId,
            RevisionNumber = 1,
            PayloadJson = "{}"
        });
        db.OrderSourceLines.Add(new OrderSourceLine
        {
            Id = sourceLineId,
            RevisionId = revisionId,
            SourceRowKey = "Sheet1!2",
            CollectionSite = "Aldi Swindon",
            DeliverySite = "New Covent Garden",
            CollectionDate = date,
            DeliveryDate = date.AddDays(1),
            CollectionTimeFrom = new TimeOnly(3, 0),
            PalletType = "Standard",
            Pallets = 18,
            TemperatureRequirement = "Chilled",
            LoadReference = "PO-1001",
            PayloadJson = "{}"
        });
        await db.SaveChangesAsync();

        var service = new PlanningOptimiserService(db, NullLogger<PlanningOptimiserService>.Instance);
        var proposal = await service.GenerateAsync(
            new GeneratePlanProposalRequest(date, "AM"),
            "planner@lyonshaulage.com",
            CancellationToken.None);

        Assert.Equal(date, proposal.PlanningDate);
        Assert.Equal("Unverified", proposal.Classification);
        var run = Assert.Single(proposal.Runs);
        var allocation = Assert.Single(run.Allocations);
        Assert.Equal(sourceLineId, allocation.SourceLineId);
        Assert.Equal(18, allocation.Pallets);
        Assert.Contains(proposal.Warnings, warning => warning.Code == "TachoEvidenceMissing");
        Assert.Empty(await db.Loads.ToListAsync());
        Assert.Empty(await db.LoadStops.ToListAsync());

        var stored = Assert.Single(await db.PlanProposals.AsNoTracking().ToListAsync());
        Assert.Equal(proposal.Id, stored.Id);
        Assert.Equal("Generated", stored.Status);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
