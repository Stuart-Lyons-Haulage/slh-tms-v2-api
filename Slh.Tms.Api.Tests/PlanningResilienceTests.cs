using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Models;
using Xunit;

namespace Slh.Tms.Api.Tests;

public sealed class PlanningResilienceTests
{
    [Fact]
    public void Active_planning_register_row_wins_over_cancelled_live_tombstone()
    {
        var id = Guid.NewGuid();
        var registered = new Load { Id = id, Reference = "PLAN-1", Status = LoadStatus.Planned };
        var live = new Load { Id = id, Reference = "PLAN-1", Status = LoadStatus.Cancelled };

        Assert.True(PlanningResilience.KeepRegisteredOverLiveTombstone(registered, live));
    }

    [Fact]
    public void Live_runtime_row_still_wins_when_it_is_not_cancelled()
    {
        var id = Guid.NewGuid();
        var registered = new Load { Id = id, Reference = "PLAN-2", Status = LoadStatus.Planned };
        var live = new Load { Id = id, Reference = "PLAN-2", Status = LoadStatus.InProgress };

        Assert.False(PlanningResilience.KeepRegisteredOverLiveTombstone(registered, live));
    }

    [Fact]
    public void Cancelled_register_does_not_revive_from_cancelled_live_row()
    {
        var id = Guid.NewGuid();
        var registered = new Load { Id = id, Reference = "PLAN-3", Status = LoadStatus.Cancelled };
        var live = new Load { Id = id, Reference = "PLAN-3", Status = LoadStatus.Cancelled };

        Assert.False(PlanningResilience.KeepRegisteredOverLiveTombstone(registered, live));
    }

    [Fact]
    public void Same_id_register_allocation_wins_over_stale_live_draft()
    {
        var id = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var trailerId = Guid.NewGuid();
        var registered = new Load
        {
            Id = id,
            Reference = "Run 1",
            Status = LoadStatus.Planned,
            DriverId = driverId,
            VehicleId = vehicleId,
            TrailerId = trailerId
        };
        var staleLive = new Load
        {
            Id = id,
            Reference = "Run 1",
            Status = LoadStatus.Draft
        };

        var preferred = PlanningResilience.PreferSameIdCopy(registered, staleLive);

        Assert.Same(registered, preferred);
        Assert.Equal(driverId, preferred.DriverId);
        Assert.Equal(vehicleId, preferred.VehicleId);
        Assert.Equal(trailerId, preferred.TrailerId);
        Assert.Equal(LoadStatus.Planned, preferred.Status);
    }

    [Fact]
    public void Same_id_executed_live_copy_wins_but_keeps_register_allocation()
    {
        var id = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var registered = new Load
        {
            Id = id,
            Reference = "Run 2",
            Status = LoadStatus.Planned,
            DriverId = driverId,
            VehicleId = vehicleId
        };
        var live = new Load
        {
            Id = id,
            Reference = "Run 2",
            Status = LoadStatus.InProgress
        };

        var preferred = PlanningResilience.PreferSameIdCopy(registered, live);

        Assert.Same(live, preferred);
        Assert.Equal(LoadStatus.InProgress, preferred.Status);
        Assert.Equal(driverId, preferred.DriverId);
        Assert.Equal(vehicleId, preferred.VehicleId);
    }

    [Fact]
    public void Same_planning_run_under_different_ids_is_returned_once()
    {
        var date = new DateOnly(2026, 8, 28);
        var driverId = Guid.NewGuid();
        var vehicleId = Guid.NewGuid();
        var trailerId = Guid.NewGuid();
        var core = new Load
        {
            Id = Guid.NewGuid(),
            PlanningDate = date,
            Reference = "PLAN-20260828-Run 7",
            Status = LoadStatus.Planned,
            DriverId = driverId,
            VehicleId = vehicleId
        };
        var register = new Load
        {
            Id = Guid.NewGuid(),
            PlanningDate = date,
            Reference = "Run 7",
            Status = LoadStatus.Planned,
            DriverId = driverId,
            VehicleId = vehicleId,
            TrailerId = trailerId
        };

        var rows = PlanningResilience.CollapseLogicalDuplicates([core, register]);

        var row = Assert.Single(rows);
        Assert.Equal(driverId, row.DriverId);
        Assert.Equal(vehicleId, row.VehicleId);
        Assert.Equal(trailerId, row.TrailerId);
    }

    [Fact]
    public void Executed_copy_wins_but_keeps_missing_allocation_detail_from_duplicate()
    {
        var date = new DateOnly(2026, 8, 28);
        var trailerId = Guid.NewGuid();
        var liveId = Guid.NewGuid();
        var live = new Load
        {
            Id = liveId,
            PlanningDate = date,
            Reference = "Run 9 PM",
            Status = LoadStatus.InProgress
        };
        var register = new Load
        {
            Id = Guid.NewGuid(),
            PlanningDate = date,
            Reference = "PLAN-20260828-Run 9 PM",
            Status = LoadStatus.Planned,
            TrailerId = trailerId
        };

        var row = Assert.Single(PlanningResilience.CollapseLogicalDuplicates([register, live]));

        Assert.Equal(liveId, row.Id);
        Assert.Equal(LoadStatus.InProgress, row.Status);
        Assert.Equal(trailerId, row.TrailerId);
    }

    [Fact]
    public void Same_reference_on_different_days_remains_separate()
    {
        var first = new Load { Id = Guid.NewGuid(), PlanningDate = new DateOnly(2026, 8, 28), Reference = "Run 4", Status = LoadStatus.Planned };
        var second = new Load { Id = Guid.NewGuid(), PlanningDate = new DateOnly(2026, 8, 29), Reference = "Run 4", Status = LoadStatus.Planned };

        var rows = PlanningResilience.CollapseLogicalDuplicates([first, second]);

        Assert.Equal(2, rows.Count);
    }
}
