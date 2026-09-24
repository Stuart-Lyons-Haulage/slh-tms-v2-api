using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Planning;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Data;
public sealed class TmsDbContext(DbContextOptions<TmsDbContext> options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerContact> CustomerContacts => Set<CustomerContact>();
    public DbSet<CustomerEmailRoute> CustomerEmailRoutes => Set<CustomerEmailRoute>();
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<Trailer> Trailers => Set<Trailer>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<MarketContact> MarketContacts => Set<MarketContact>();
    public DbSet<StagedImport> StagedImports => Set<StagedImport>();
    public DbSet<StagedImportEvent> StagedImportEvents => Set<StagedImportEvent>();
    public DbSet<OrderMovement> OrderMovements => Set<OrderMovement>();
    public DbSet<OrderRevision> OrderRevisions => Set<OrderRevision>();
    public DbSet<OrderSourceLine> OrderSourceLines => Set<OrderSourceLine>();
    public DbSet<PlanProposal> PlanProposals => Set<PlanProposal>();
    public DbSet<PlanProposalRun> PlanProposalRuns => Set<PlanProposalRun>();
    public DbSet<PlanProposalAllocation> PlanProposalAllocations => Set<PlanProposalAllocation>();
    public DbSet<PlanProposalCandidate> PlanProposalCandidates => Set<PlanProposalCandidate>();
    public DbSet<OrderReferenceIssue> OrderReferenceIssues => Set<OrderReferenceIssue>();
    public DbSet<ReferenceChaseEvent> ReferenceChaseEvents => Set<ReferenceChaseEvent>();
    public DbSet<TransportOrder> TransportOrders => Set<TransportOrder>();
    public DbSet<Load> Loads => Set<Load>();
    public DbSet<LoadStop> LoadStops => Set<LoadStop>();
    public DbSet<Run> Runs => Set<Run>();
    public DbSet<RunStop> RunStops => Set<RunStop>();
    public DbSet<RunOrderAllocation> RunOrderAllocations => Set<RunOrderAllocation>();
    public DbSet<RunResourceAllocation> RunResourceAllocations => Set<RunResourceAllocation>();
    public DbSet<RunStatusHistory> RunStatusHistory => Set<RunStatusHistory>();
    public DbSet<RunTrackingState> RunTrackingStates => Set<RunTrackingState>();
    public DbSet<VehicleTrackingEvent> VehicleTrackingEvents => Set<VehicleTrackingEvent>();
    public DbSet<VehicleLiveStatus> VehicleLiveStatuses => Set<VehicleLiveStatus>();
    public DbSet<FuelPrice> FuelPrices => Set<FuelPrice>();
    public DbSet<IntegrationMapping> IntegrationMappings => Set<IntegrationMapping>();
    public DbSet<DriverStatusLog> DriverStatusLogs => Set<DriverStatusLog>();
    public DbSet<MasterDataAudit> MasterDataAudits => Set<MasterDataAudit>();
    public DbSet<AuditOutbox> AuditOutboxes => Set<AuditOutbox>();
    public DbSet<TmsUser> TmsUsers => Set<TmsUser>();
    public DbSet<SiteGeofence> SiteGeofences => Set<SiteGeofence>();
    public DbSet<GeofenceVisit> GeofenceVisits => Set<GeofenceVisit>();
    public DbSet<EtaSnapshot> EtaSnapshots => Set<EtaSnapshot>();
    public DbSet<MasterDepot> MasterDepots => Set<MasterDepot>();
    public DbSet<MasterCustomer> MasterCustomers => Set<MasterCustomer>();
    public DbSet<MasterDriver> MasterDrivers => Set<MasterDriver>();
    public DbSet<MasterVehicle> MasterVehicles => Set<MasterVehicle>();
    public DbSet<MasterTrailer> MasterTrailers => Set<MasterTrailer>();
    public DbSet<MasterSite> MasterSites => Set<MasterSite>();
    public DbSet<MasterSubcontractor> MasterSubcontractors => Set<MasterSubcontractor>();
    public DbSet<MasterMarket> MasterMarkets => Set<MasterMarket>();
    public DbSet<MasterFuelCard> MasterFuelCards => Set<MasterFuelCard>();
    public DbSet<MasterFuelPrice> MasterFuelPrices => Set<MasterFuelPrice>();

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var completionTransitions = ChangeTracker.Entries<Load>()
            .Where(entry =>
                entry.Entity.Status == LoadStatus.Completed
                && (entry.State == EntityState.Added
                    || entry.State == EntityState.Modified
                    && entry.Property(load => load.Status).IsModified
                    && entry.Property(load => load.Status).OriginalValue != LoadStatus.Completed))
            .Select(entry => entry.Entity.Id)
            .Distinct()
            .ToList();

        foreach (var loadId in completionTransitions)
            await RunCompletionPersistenceGuard.EnsureCompletionEvidenceAsync(this, loadId, cancellationToken);

        NormalizeMarketOrderProjection();
        EnqueuePendingMasterDataAudits();
        return await base.SaveChangesAsync(cancellationToken);
    }

    internal Task<int> SaveAuditReplayChangesAsync(CancellationToken cancellationToken = default) =>
        base.SaveChangesAsync(cancellationToken);

    private void NormalizeMarketOrderProjection()
    {
        foreach (var entry in ChangeTracker.Entries<TransportOrder>()
                     .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var instructions = entry.Entity.DriverInstructions;
            if (string.IsNullOrWhiteSpace(instructions)) continue;

            // OrderSiteMasterAlignment writes these tags from Site Master + Markets Master.
            // Persist the physical Market Site separately from the internal stall/stand so
            // Approved/Live Loads cannot flatten both values into StallNumber during promotion.
            var market = TaggedValue(instructions, "Market");
            var stand = TaggedValue(instructions, "Stall / stand");
            if (!string.IsNullOrWhiteSpace(market)) entry.Entity.MarketName = Clip(market, 80);
            if (!string.IsNullOrWhiteSpace(stand)) entry.Entity.StallNumber = Clip(stand, 200);
        }
    }

    private static string? TaggedValue(string notes, string label)
    {
        var prefix = $"{label}:";
        var part = notes.Split('·', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(value => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(part)) return null;
        var value = part[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Clip(string value, int maxLength) => value.Length <= maxLength ? value : value[..maxLength];

    private void EnqueuePendingMasterDataAudits()
    {
        var pendingAudits = ChangeTracker.Entries<MasterDataAudit>()
            .Where(entry => entry.State == EntityState.Added)
            .ToList();

        foreach (var entry in pendingAudits)
        {
            var audit = entry.Entity;
            AuditOutboxes.Add(new AuditOutbox
            {
                EventType = AuditOutboxEventTypes.MasterDataAudit,
                Payload = JsonSerializer.Serialize(audit),
                CreatedAt = DateTimeOffset.UtcNow,
                RetryCount = 0
            });

            entry.State = EntityState.Detached;
        }
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MasterDepot>().HasNoKey().ToView("vw_ActiveDepots", "dbo");
        b.Entity<MasterCustomer>().HasNoKey().ToView("vw_ActiveCustomers", "dbo");
        b.Entity<MasterDriver>().HasNoKey().ToView("vw_ActiveDrivers", "dbo");
        b.Entity<MasterVehicle>().HasNoKey().ToView("vw_ActiveVehicles", "dbo");
        b.Entity<MasterTrailer>().HasNoKey().ToView("vw_ActiveTrailers", "dbo");
        b.Entity<MasterSite>().HasNoKey().ToView("vw_ActiveSites", "dbo");
        b.Entity<MasterSubcontractor>().HasNoKey().ToView("vw_ActiveSubcontractors", "dbo");
        b.Entity<MasterMarket>().HasNoKey().ToView("vw_ActiveMarkets", "dbo");
        b.Entity<MasterFuelCard>().HasNoKey().ToView("vw_ActiveFuelCards", "dbo");
        b.Entity<MasterFuelPrice>().HasNoKey().ToView("vw_ActiveFuelPrices", "dbo");

        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique(false);
        b.Entity<CustomerContact>().HasIndex(x => new { x.CustomerCode, x.Name }).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.Registration).IsUnique();
        b.Entity<Vehicle>().HasIndex(x => x.FleetioId)
            .IsUnique()
            .HasFilter("[FleetioId] IS NOT NULL")
            .HasDatabaseName("UX_Vehicles_FleetioId");
        b.Entity<Driver>().HasIndex(x => x.EmployeeNumber).IsUnique();
        b.Entity<Driver>().HasIndex(x => x.TachoMasterDriverId)
            .IsUnique()
            .HasDatabaseName("IX_Drivers_TachoMasterDriverId")
            .HasFilter("[TachoMasterDriverId] IS NOT NULL");
        b.Entity<Trailer>().HasIndex(x => x.TrailerNumber).IsUnique();
        b.Entity<Site>().HasIndex(x => x.ExternalCode).IsUnique();
        b.Entity<MarketContact>().HasIndex(x => new { x.Market, x.Name, x.StandOrLocation });
        b.Entity<MarketContact>().HasIndex(x => x.MarketKey).IsUnique().HasFilter("[MarketKey] IS NOT NULL");
        b.Entity<FuelPrice>().HasIndex(x => new { x.WeekCommencing, x.Provider }).IsUnique();
        b.Entity<FuelPrice>().Property(x => x.PricePencePerLitre).HasPrecision(10, 2);

        b.Entity<IntegrationMapping>()
            .HasIndex(x => new { x.Provider, x.ExternalKey, x.TmsEntityType })
            .IsUnique()
            .HasFilter("[Active] = 1")
            .HasDatabaseName("IX_IntegrationMappings_Provider_ExternalKey_Type");
        b.Entity<IntegrationMapping>()
            .HasIndex(x => x.TmsEntityId)
            .HasDatabaseName("IX_IntegrationMappings_TmsEntityId");
        b.Entity<IntegrationMapping>().Property(x => x.ConfidenceThreshold).HasPrecision(5, 4);

        b.Entity<DriverStatusLog>()
            .HasIndex(x => x.LoadId)
            .HasDatabaseName("IX_DriverStatusLogs_LoadId");
        b.Entity<DriverStatusLog>()
            .HasIndex(x => x.CapturedAtUtc)
            .HasDatabaseName("IX_DriverStatusLogs_CapturedAtUtc");

        b.Entity<MasterDataAudit>()
            .HasIndex(x => new { x.EntityType, x.EntityId, x.ChangedAtUtc })
            .HasDatabaseName("IX_MasterDataAudits_Entity_History");
        b.Entity<MasterDataAudit>()
            .HasIndex(x => x.ChangedAtUtc)
            .HasDatabaseName("IX_MasterDataAudits_ChangedAtUtc");

        b.Entity<AuditOutbox>().ToTable("AuditOutbox");
        b.Entity<AuditOutbox>().HasKey(x => x.OutboxId);
        b.Entity<TmsUser>().ToTable("TmsUsers");
        b.Entity<TmsUser>().HasKey(x => x.Id);
        b.Entity<TmsUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<TmsUser>().HasIndex(x => new { x.Active, x.Role });
        b.Entity<AuditOutbox>()
            .HasIndex(x => new { x.ProcessedAt, x.FailedAt, x.CreatedAt })
            .HasDatabaseName("IX_AuditOutbox_Pending");
        b.Entity<AuditOutbox>()
            .HasIndex(x => x.CreatedAt)
            .HasDatabaseName("IX_AuditOutbox_CreatedAt");

        // StagedImports has database triggers in production. EF Core's default SQL Server
        // save pipeline emits OUTPUT without INTO, which SQL Server rejects on triggered tables.
        // Disabling the OUTPUT clause keeps order intake, TachoMaster sync status and fallback
        // register writes trigger-safe without removing audit triggers or rowversion checks.
        b.Entity<StagedImport>().ToTable("StagedImports", table => table.UseSqlOutputClause(false));
        b.Entity<StagedImport>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<StagedImport>()
            .HasIndex(x => new { x.EntityType, x.Status, x.ReceivedAtUtc })
            .HasDatabaseName("IX_StagedImports_Entity_Status_ReceivedAtUtc");
        b.Entity<StagedImport>().Property(x => x.RowVersion).IsRowVersion();
        b.Entity<StagedImportEvent>().HasIndex(x => new { x.StagedImportId, x.OccurredAtUtc });
        b.Entity<StagedImportEvent>().HasOne<StagedImport>().WithMany().HasForeignKey(x => x.StagedImportId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderMovement>().HasIndex(x => new { x.CustomerCode, x.StableMovementKey }).IsUnique();
        b.Entity<OrderRevision>().HasIndex(x => new { x.MovementId, x.RevisionNumber }).IsUnique();
        b.Entity<OrderRevision>().HasIndex(x => x.StagedImportId).IsUnique();
        b.Entity<OrderRevision>().HasOne<OrderMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderRevision>().HasOne<StagedImport>().WithMany().HasForeignKey(x => x.StagedImportId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderSourceLine>().HasIndex(x => new { x.RevisionId, x.SourceRowKey }).IsUnique();
        b.Entity<OrderSourceLine>().HasOne<OrderRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposal>().HasIndex(x => new { x.PlanningDate, x.Period, x.Version }).IsUnique();
        b.Entity<PlanProposal>().HasIndex(x => x.InputHash);
        b.Entity<PlanProposalRun>().HasIndex(x => new { x.ProposalId, x.Sequence }).IsUnique();
        b.Entity<PlanProposalRun>().Property(x => x.Score).HasPrecision(10, 2);
        b.Entity<PlanProposalRun>().HasOne<Driver>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalRun>().HasOne<Vehicle>().WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalRun>().HasOne<Trailer>().WithMany().HasForeignKey(x => x.TrailerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposal>().HasMany(x => x.Runs).WithOne().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanProposalAllocation>().HasIndex(x => new { x.ProposalRunId, x.SourceLineId });
        b.Entity<PlanProposalRun>().HasMany(x => x.Allocations).WithOne().HasForeignKey(x => x.ProposalRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanProposalAllocation>().HasOne<OrderSourceLine>().WithMany().HasForeignKey(x => x.SourceLineId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalCandidate>().HasIndex(x => new { x.ProposalRunId, x.DriverId, x.VehicleId }).IsUnique();
        b.Entity<PlanProposalCandidate>().Property(x => x.Score).HasPrecision(10, 2);
        b.Entity<PlanProposalRun>().HasMany(x => x.Candidates).WithOne().HasForeignKey(x => x.ProposalRunId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<PlanProposalCandidate>().HasOne<Driver>().WithMany().HasForeignKey(x => x.DriverId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<PlanProposalCandidate>().HasOne<Vehicle>().WithMany().HasForeignKey(x => x.VehicleId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<OrderReferenceIssue>().HasIndex(x => new { x.MovementId, x.ReferenceType, x.Status });
        b.Entity<OrderReferenceIssue>().HasOne<OrderMovement>().WithMany().HasForeignKey(x => x.MovementId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<ReferenceChaseEvent>().HasIndex(x => new { x.ReferenceIssueId, x.OccurredAtUtc });
        b.Entity<ReferenceChaseEvent>().HasOne<OrderReferenceIssue>().WithMany().HasForeignKey(x => x.ReferenceIssueId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TransportOrder>().HasIndex(x => x.Reference).IsUnique();
        b.Entity<TransportOrder>().HasIndex(x => x.CollectionDate);
        b.Entity<TransportOrder>().HasIndex(x => new { x.CollectionDate, x.Status })
            .HasDatabaseName("IX_TransportOrders_CollectionDate_Status");
        b.Entity<TransportOrder>().HasIndex(x => x.SourceStagedImportId);
        b.Entity<TransportOrder>().HasOne<StagedImport>().WithMany().HasForeignKey(x => x.SourceStagedImportId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<TransportOrder>().HasIndex(x => x.SourceMovementId);
        b.Entity<TransportOrder>().HasOne<OrderMovement>().WithMany().HasForeignKey(x => x.SourceMovementId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<Load>().HasIndex(x => x.Reference).IsUnique();
        b.Entity<Load>().HasIndex(x => x.PlanningDate);
        b.Entity<Load>().HasIndex(x => new { x.PlanningDate, x.Status })
            .HasDatabaseName("IX_Loads_PlanningDate_Status");
        b.Entity<LoadStop>().HasIndex(x => new { x.LoadId, x.Sequence }).IsUnique();
        b.Entity<Load>().HasMany(x => x.Stops).WithOne().HasForeignKey(x => x.LoadId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<LoadStop>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<LoadStop>().Property(x => x.Longitude).HasPrecision(9, 6);

        b.ConfigureCanonicalPlanningModel();

        b.Entity<SiteGeofence>().HasIndex(x => x.NormalizedName).IsUnique();
        b.Entity<SiteGeofence>().HasIndex(x => x.SiteId);
        b.Entity<Customer>().HasIndex(x => x.Code).IsUnique(false);
        b.Entity<CustomerEmailRoute>().HasIndex(x => new { x.CustomerCode, x.SenderEmail, x.SenderDomain });
        b.Entity<CustomerEmailRoute>().HasIndex(x => new { x.Active, x.SenderEmail });
        b.Entity<CustomerEmailRoute>().HasIndex(x => new { x.Active, x.SenderDomain });
        b.Entity<Site>().HasIndex(x => new { x.CustomerCode, x.ExternalCode });
        b.Entity<GeofenceVisit>().HasIndex(x => new { x.VehicleIdentifier, x.ExitedAtUtc });
        b.Entity<GeofenceVisit>().HasIndex(x => new { x.LoadId, x.LoadStopId });
        b.Entity<GeofenceVisit>().HasIndex(x => new { x.RunId, x.RunStopId });
        b.Entity<GeofenceVisit>().HasIndex(x => new { x.VehicleIdentifier, x.GeofenceId, x.EnteredAtUtc });
        b.Entity<GeofenceVisit>().HasIndex(x => x.EnteredAtUtc);
        b.Entity<EtaSnapshot>().HasIndex(x => new { x.StopId, x.CapturedAtUtc });
        b.Entity<EtaSnapshot>().HasIndex(x => x.LoadId);

        b.Entity<VehicleTrackingEvent>()
            .HasIndex(x => new { x.ProviderName, x.ProviderEventId })
            .IsUnique()
            .HasDatabaseName("IX_VehicleTrackingEvent_ProviderName_ProviderEventId");
        b.Entity<VehicleTrackingEvent>()
            .HasIndex(x => x.VehicleIdentifier)
            .HasDatabaseName("IX_VehicleTrackingEvent_VehicleIdentifier");
        b.Entity<VehicleTrackingEvent>()
            .HasIndex(x => x.EventTimeUtc)
            .HasDatabaseName("IX_VehicleTrackingEvent_EventTimeUtc");
        b.Entity<VehicleTrackingEvent>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<VehicleTrackingEvent>().Property(x => x.Longitude).HasPrecision(9, 6);
        b.Entity<VehicleTrackingEvent>().Property(x => x.SpeedKph).HasPrecision(10, 2);

        b.Entity<VehicleLiveStatus>()
            .HasIndex(x => x.VehicleIdentifier)
            .IsUnique()
            .HasDatabaseName("IX_VehicleLiveStatus_VehicleIdentifier");
        b.Entity<VehicleLiveStatus>()
            .HasIndex(x => x.LastEventTimeUtc)
            .HasDatabaseName("IX_VehicleLiveStatus_LastEventTimeUtc");
        b.Entity<VehicleLiveStatus>().Property(x => x.Latitude).HasPrecision(9, 6);
        b.Entity<VehicleLiveStatus>().Property(x => x.Longitude).HasPrecision(9, 6);
        b.Entity<VehicleLiveStatus>().Property(x => x.SpeedKph).HasPrecision(10, 2);
    }
}
