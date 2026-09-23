using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Planning;
using Xunit;
using PlanningRun = Slh.Tms.Api.Models.Planning.Run;

namespace Slh.Tms.Api.Tests;

public sealed class CanonicalPlanningModelTests
{
    [Fact]
    public void Run_reference_is_normalised_for_canonical_identity()
    {
        var run = new PlanningRun
        {
            PlanningDate = new DateOnly(2026, 8, 31),
            RunReference = " Plan-12 / PM "
        };

        Assert.Equal("PLAN12PM", run.RunReference);
    }

    [Fact]
    public void Canonical_run_has_unique_business_key_and_rowversion_concurrency()
    {
        using var db = CreateContext();
        var entity = db.Model.FindEntityType(typeof(PlanningRun));

        Assert.NotNull(entity);
        Assert.Equal("Runs", entity!.GetTableName());

        var businessKey = entity.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(new[] { nameof(PlanningRun.PlanningDate), nameof(PlanningRun.RunReference) }));
        Assert.True(businessKey.IsUnique);

        var rowVersion = entity.FindProperty(nameof(PlanningRun.RowVersion));
        Assert.NotNull(rowVersion);
        Assert.True(rowVersion!.IsConcurrencyToken);
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.OnAddOrUpdate, rowVersion.ValueGenerated);
    }

    [Fact]
    public void Canonical_planning_foreign_keys_are_explicit_and_database_backed()
    {
        using var db = CreateContext();

        AssertForeignKey<RunStop, PlanningRun>(db, nameof(RunStop.RunId), required: true);
        AssertForeignKey<RunStop, Site>(db, nameof(RunStop.SiteId), required: true);
        AssertForeignKey<RunStop, GeofenceVisit>(db, nameof(RunStop.GeofenceVisitId), required: false);

        AssertForeignKey<RunOrderAllocation, PlanningRun>(db, nameof(RunOrderAllocation.RunId), required: true);
        AssertForeignKey<RunOrderAllocation, TransportOrder>(db, nameof(RunOrderAllocation.OrderId), required: true);
        AssertForeignKey<RunOrderAllocation, OrderRevision>(db, nameof(RunOrderAllocation.SourceRevisionId), required: true);

        AssertForeignKey<RunResourceAllocation, PlanningRun>(db, nameof(RunResourceAllocation.RunId), required: true);
        AssertForeignKey<RunResourceAllocation, Driver>(db, nameof(RunResourceAllocation.DriverId), required: true);
        AssertForeignKey<RunResourceAllocation, Vehicle>(db, nameof(RunResourceAllocation.VehicleId), required: true);
        AssertForeignKey<RunResourceAllocation, Trailer>(db, nameof(RunResourceAllocation.TrailerId), required: true);

        AssertForeignKey<RunStatusHistory, PlanningRun>(db, nameof(RunStatusHistory.RunId), required: true);
        AssertForeignKey<RunTrackingState, PlanningRun>(db, nameof(RunTrackingState.RunId), required: true);
    }

    [Fact]
    public void Resource_allocation_and_tracking_state_are_singletons_per_run()
    {
        using var db = CreateContext();

        var resource = db.Model.FindEntityType(typeof(RunResourceAllocation))!;
        Assert.Contains(resource.GetIndexes(), index =>
            index.IsUnique && index.Properties.Count == 1 && index.Properties[0].Name == nameof(RunResourceAllocation.RunId));

        var tracking = db.Model.FindEntityType(typeof(RunTrackingState))!;
        Assert.Equal(new[] { nameof(RunTrackingState.RunId) }, tracking.FindPrimaryKey()!.Properties.Select(property => property.Name));
    }

    [Fact]
    public void Canonical_planning_migration_is_discoverable_by_ef_core()
    {
        using var db = CreateContext();

        Assert.Contains("20260831214900_CanonicalRelationalPlanning", db.Database.GetMigrations());
    }

    private static TmsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<TmsDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        return new TmsDbContext(options);
    }

    private static void AssertForeignKey<TDependent, TPrincipal>(TmsDbContext db, string propertyName, bool required)
    {
        var dependent = db.Model.FindEntityType(typeof(TDependent));
        Assert.NotNull(dependent);

        var foreignKey = dependent!.GetForeignKeys().Single(fk =>
            fk.Properties.Count == 1 && fk.Properties[0].Name == propertyName);

        Assert.Equal(typeof(TPrincipal), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(required, foreignKey.IsRequired);
    }
}
