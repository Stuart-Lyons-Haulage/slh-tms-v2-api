using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Planning;

namespace Slh.Tms.Api.Data;

internal static class CanonicalPlanningModelConfiguration
{
    internal static void ConfigureCanonicalPlanningModel(this ModelBuilder b)
    {
        b.Entity<Run>(entity =>
        {
            entity.ToTable("Runs");
            entity.HasKey(x => x.RunId);
            entity.Property(x => x.RunReference).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(200);
            entity.Property(x => x.RowVersion).IsRowVersion().IsConcurrencyToken();
            entity.HasIndex(x => new { x.PlanningDate, x.RunReference })
                .IsUnique()
                .HasDatabaseName("UX_Runs_PlanningDate_RunReference");
            entity.HasIndex(x => x.Status).HasDatabaseName("IX_Runs_Status");
        });

        b.Entity<RunStop>(entity =>
        {
            entity.ToTable("RunStops");
            entity.HasKey(x => x.RunStopId);
            entity.HasIndex(x => new { x.RunId, x.Sequence })
                .IsUnique()
                .HasDatabaseName("UX_RunStops_RunId_Sequence");
            entity.HasIndex(x => x.SiteId).HasDatabaseName("IX_RunStops_SiteId");
            entity.HasIndex(x => x.GeofenceVisitId)
                .IsUnique()
                .HasFilter("[GeofenceVisitId] IS NOT NULL")
                .HasDatabaseName("UX_RunStops_GeofenceVisitId");
            entity.HasOne(x => x.Run)
                .WithMany(x => x.Stops)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Site)
                .WithMany()
                .HasForeignKey(x => x.SiteId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.GeofenceVisit)
                .WithMany()
                .HasForeignKey(x => x.GeofenceVisitId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        b.Entity<RunOrderAllocation>(entity =>
        {
            entity.ToTable("RunOrderAllocations");
            entity.HasKey(x => x.AllocationId);
            entity.Property(x => x.CapacityUnits).HasPrecision(10, 2);
            entity.Property(x => x.UpdatedBy).HasMaxLength(200);
            entity.HasIndex(x => new { x.RunId, x.OrderId })
                .IsUnique()
                .HasDatabaseName("UX_RunOrderAllocations_RunId_OrderId");
            entity.HasIndex(x => x.OrderId).HasDatabaseName("IX_RunOrderAllocations_OrderId");
            entity.HasIndex(x => x.SourceRevisionId).HasDatabaseName("IX_RunOrderAllocations_SourceRevisionId");
            entity.HasOne(x => x.Run)
                .WithMany(x => x.OrderAllocations)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Order)
                .WithMany()
                .HasForeignKey(x => x.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SourceRevision)
                .WithMany()
                .HasForeignKey(x => x.SourceRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RunResourceAllocation>(entity =>
        {
            entity.ToTable("RunResourceAllocations");
            entity.HasKey(x => x.ResourceAllocationId);
            entity.Property(x => x.AllocatedBy).HasMaxLength(200);
            entity.HasIndex(x => x.RunId)
                .IsUnique()
                .HasDatabaseName("UX_RunResourceAllocations_RunId");
            entity.HasIndex(x => x.DriverId).HasDatabaseName("IX_RunResourceAllocations_DriverId");
            entity.HasIndex(x => x.VehicleId).HasDatabaseName("IX_RunResourceAllocations_VehicleId");
            entity.HasIndex(x => x.TrailerId).HasDatabaseName("IX_RunResourceAllocations_TrailerId");
            entity.HasOne(x => x.Run)
                .WithOne(x => x.ResourceAllocation)
                .HasForeignKey<RunResourceAllocation>(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Driver)
                .WithMany()
                .HasForeignKey(x => x.DriverId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Vehicle)
                .WithMany()
                .HasForeignKey(x => x.VehicleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Trailer)
                .WithMany()
                .HasForeignKey(x => x.TrailerId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<RunStatusHistory>(entity =>
        {
            entity.ToTable("RunStatusHistory");
            entity.HasKey(x => x.HistoryId);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ChangedBy).HasMaxLength(200);
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.HasIndex(x => new { x.RunId, x.ChangedAt })
                .HasDatabaseName("IX_RunStatusHistory_RunId_ChangedAt");
            entity.HasOne(x => x.Run)
                .WithMany(x => x.StatusHistory)
                .HasForeignKey(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RunTrackingState>(entity =>
        {
            entity.ToTable("RunTrackingStates");
            entity.HasKey(x => x.RunId);
            entity.Property(x => x.LastLatitude).HasPrecision(9, 6);
            entity.Property(x => x.LastLongitude).HasPrecision(9, 6);
            entity.Property(x => x.TrackingSource).HasMaxLength(80);
            entity.HasOne(x => x.Run)
                .WithOne(x => x.TrackingState)
                .HasForeignKey<RunTrackingState>(x => x.RunId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
