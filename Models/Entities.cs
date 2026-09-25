using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Slh.Tms.Api.Models;
public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string Code { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public string? TradingName { get; set; }
    [MaxLength(200)] public string? AccountOwner { get; set; }
    [MaxLength(1000)] public string? ServiceNotes { get; set; }
    [MaxLength(80)] public string? DefaultSiteCode { get; set; }
    public bool Active { get; set; } = true;
}

public sealed class CustomerEmailRoute
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string CustomerCode { get; set; }
    [EmailAddress, MaxLength(320)] public string? SenderEmail { get; set; }
    [MaxLength(320)] public string? SenderDomain { get; set; }
    [MaxLength(200)] public string? SubjectContains { get; set; }
    [MaxLength(120)] public string? ParserType { get; set; }
    [MaxLength(80)] public string? DefaultSiteCode { get; set; }
    [MaxLength(80)] public string? DefaultDeliverySiteCode { get; set; }
    [MaxLength(160)] public string? MarketKey { get; set; }
    public bool RequiresReview { get; set; } = true;
    public bool Active { get; set; } = true;
}
public sealed class CustomerContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string CustomerCode { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [EmailAddress, MaxLength(320)] public string? Email { get; set; }
    [MaxLength(40)] public string? MobileNumber { get; set; }
    public bool ReceivesEtaUpdates { get; set; } = true;
    public bool Active { get; set; } = true;
}
public sealed class Vehicle
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(20)] public required string Registration { get; set; }
    [MaxLength(40)] public string? VIN { get; set; }
    [MaxLength(80)] public string? OwnerType { get; set; }
    [MaxLength(160)] public string? VehicleSite { get; set; }
    [MaxLength(40)] public string? FleetNumber { get; set; }
    [MaxLength(20)] public string? Abbreviation { get; set; }
    [MaxLength(20)] public string? Transmission { get; set; }
    public bool? DvsCompliant { get; set; }
    [MaxLength(30)] public string? FuelProvider { get; set; }
    [MaxLength(40)] public string? CabMobile { get; set; }
    [MaxLength(80)] public string? FuelPin { get; set; }
    [MaxLength(80)] public string? ShellCard { get; set; }
    [MaxLength(80)] public string? BpRedCard { get; set; }
    [MaxLength(80)] public string? BpPlainCard { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(120)] public string? FuelPinSecretName { get; set; }
    [MaxLength(4)] public string? FuelCardLastFour { get; set; }
    public DateOnly? MOTExpiry { get; set; }
    public DateOnly? TachoCalibrationExpiry { get; set; }
    public DateOnly? VehicleTestExpiry { get; set; }
    [MaxLength(80)] public string? FleetioId { get; set; }
    [MaxLength(160)] public string? FleetioName { get; set; }
    [MaxLength(80)] public string? FleetioStatus { get; set; }
    [NotMapped] public bool? FleetioVor { get; set; }
    [NotMapped] public DateTimeOffset? FleetioPmiDueUtc { get; set; }
    [NotMapped] public DateTimeOffset? FleetioMotDueUtc { get; set; }
    [NotMapped, MaxLength(160)] public string? FleetioServiceStatus { get; set; }
    [NotMapped] public DateTimeOffset? FleetioLastSyncedUtc { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Driver
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string EmployeeNumber { get; set; }
    [MaxLength(160)] public required string DisplayName { get; set; }
    [MaxLength(160)] public string? TachoName { get; set; }
    [MaxLength(40)] public string? MobileNumber { get; set; }
    [NotMapped, MaxLength(320)] public string? Email { get; set; }
    [NotMapped, MaxLength(80)] public string? GradeCode { get; set; }
    [NotMapped, MaxLength(160)] public string? AllocatedVehicle { get; set; }
    [MaxLength(80)] public string? DriverType { get; set; }
    [MaxLength(80)] public string? DriverGroup { get; set; }
    [MaxLength(160)] public string? Skills { get; set; }
    [NotMapped, MaxLength(80)] public string? Coding { get; set; }
    [NotMapped, MaxLength(160)] public string? AgencyName { get; set; }
    [NotMapped] public bool? NorthEligible { get; set; }
    [NotMapped] public bool? PreloadEligible { get; set; }
    [NotMapped, MaxLength(500)] public string? Notes { get; set; }
    [MaxLength(80)] public string? TachoMasterDriverId { get; set; }
    [NotMapped, MaxLength(80)] public string? TachoCardNumber { get; set; }
    [NotMapped] public int? TachoDriveAvailableTodayMinutes { get; set; }
    [NotMapped] public int? TachoDriveAvailableWeekMinutes { get; set; }
    [NotMapped] public int? TachoWorkAvailableWeekMinutes { get; set; }
    [MaxLength(80)] public string? DrivingLicenceNumber { get; set; }
    public DateOnly? LicenceExpiry { get; set; }
    public DateOnly? CPCExpiry { get; set; }
    public DateOnly? DigitalTachoCardExpiry { get; set; }
    public DateOnly? MedicalExpiry { get; set; }
    [NotMapped, MaxLength(40)] public string? LicenceStatus { get; set; }
    public DateTimeOffset? LastTachoSyncUtc { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Trailer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string TrailerNumber { get; set; }
    [MaxLength(80)] public string? Type { get; set; }
    public int? StandardCapacity { get; set; }
    public int? EuroCapacity { get; set; }
    [NotMapped, MaxLength(500)] public string? Notes { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class Site
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string ExternalCode { get; set; }
    [MaxLength(40)] public string? CustomerCode { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public string? DriverTextName { get; set; }
    [MaxLength(500)] public string? CollectionAddress { get; set; }
    [MaxLength(1000)] public string? CollectionInstructions { get; set; }
    [MaxLength(1000)] public string? MapLink { get; set; }
    [NotMapped] public decimal? Latitude { get; set; }
    [NotMapped] public decimal? Longitude { get; set; }
    [NotMapped] public int? GeofenceRadiusMetres { get; set; }
    [NotMapped, MaxLength(500)] public string? Aliases { get; set; }
    [NotMapped, MaxLength(200)] public string? CustomField1 { get; set; }
    [NotMapped, MaxLength(200)] public string? CustomField2 { get; set; }
    [NotMapped, MaxLength(200)] public string? CustomField3 { get; set; }
    [NotMapped, MaxLength(80)] public string? RoadrunnerCode { get; set; }
    [NotMapped] public string? RoadrunnerProfileJson { get; set; }
    [MaxLength(80)] public string? OperationalRegion { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class MarketContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(160)] public string? MarketKey { get; set; }
    [MaxLength(80)] public required string Market { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public string? StandOrLocation { get; set; }
    [MaxLength(200)] public string? Salesman { get; set; }
    [MaxLength(200)] public string? Sender { get; set; }
    [MaxLength(1000)] public string? ReadOnlyMapPdfUrl { get; set; }
    public bool Active { get; set; } = true;
}
public sealed class FuelPrice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly WeekCommencing { get; set; }
    [MaxLength(120)] public required string Provider { get; set; }
    public decimal PricePencePerLitre { get; set; }
    public bool IsPricingMaximum { get; set; }
    [MaxLength(200)] public string? Source { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public enum StagingStatus { PendingReview, Approved, Rejected, Promoted, Failed, Archived }
public sealed class StagedImport
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string EntityType { get; set; }
    [MaxLength(200)] public required string IdempotencyKey { get; set; }
    public required string PayloadJson { get; set; }
    public StagingStatus Status { get; set; } = StagingStatus.PendingReview;
    [MaxLength(200)] public string? Source { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAtUtc { get; set; }
    [MaxLength(200)] public string? ReviewedBy { get; set; }
    [MaxLength(1000)] public string? ReviewNote { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
public sealed class StagedImportEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid StagedImportId { get; set; }
    [MaxLength(40)] public required string EventType { get; set; }
    public StagingStatus? PreviousStatus { get; set; }
    public StagingStatus NewStatus { get; set; }
    public required string PayloadJson { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
    [MaxLength(200)] public string? Actor { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public enum OrderMovementStatus { AwaitingDetails, PendingReview, PlannerReady, Superseded, Cancelled }
public sealed class OrderMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string CustomerCode { get; set; }
    [MaxLength(240)] public required string StableMovementKey { get; set; }
    public Guid? CurrentRevisionId { get; set; }
    public OrderMovementStatus LifecycleStatus { get; set; } = OrderMovementStatus.PendingReview;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class OrderRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MovementId { get; set; }
    public Guid StagedImportId { get; set; }
    public int RevisionNumber { get; set; }
    [MaxLength(500)] public string? MessageId { get; set; }
    [MaxLength(500)] public string? AttachmentIdentity { get; set; }
    [MaxLength(120)] public string? ParserTemplate { get; set; }
    [MaxLength(40)] public string? ParserVersion { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? SupersedesRevisionId { get; set; }
}
public sealed class OrderSourceLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RevisionId { get; set; }
    [MaxLength(160)] public required string SourceRowKey { get; set; }
    [MaxLength(200)] public string? CollectionSite { get; set; }
    [MaxLength(200)] public string? DeliverySite { get; set; }
    public DateOnly? CollectionDate { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public TimeOnly? CollectionTimeFrom { get; set; }
    public TimeOnly? CollectionTimeTo { get; set; }
    [MaxLength(40)] public string? PalletType { get; set; }
    public int? Pallets { get; set; }
    [MaxLength(80)] public string? TemperatureRequirement { get; set; }
    [MaxLength(120)] public string? LoadReference { get; set; }
    public required string PayloadJson { get; set; }
}
public sealed class PlanProposal
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateOnly PlanningDate { get; set; }
    [MaxLength(10)] public required string Period { get; set; }
    public int Version { get; set; } = 1;
    [MaxLength(40)] public required string Status { get; set; }
    [MaxLength(40)] public required string Classification { get; set; }
    [MaxLength(128)] public required string InputHash { get; set; }
    public required string EvidenceJson { get; set; }
    public required string WarningsJson { get; set; }
    public DateTimeOffset EvidenceCapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? CreatedBy { get; set; }
    public List<PlanProposalRun> Runs { get; set; } = [];
}
public sealed class PlanProposalRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProposalId { get; set; }
    public int Sequence { get; set; }
    [MaxLength(80)] public required string Reference { get; set; }
    public bool IsLocked { get; set; }
    public Guid? LiveLoadId { get; set; }
    [MaxLength(40)] public required string Classification { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? VehicleId { get; set; }
    public Guid? TrailerId { get; set; }
    [MaxLength(40)] public string? PositionSource { get; set; }
    public int CapacityPallets { get; set; }
    public int PlannedPallets { get; set; }
    public decimal Score { get; set; }
    public required string ScoreComponentsJson { get; set; }
    public required string ExplanationJson { get; set; }
    public List<PlanProposalAllocation> Allocations { get; set; } = [];
    public List<PlanProposalCandidate> Candidates { get; set; } = [];
}
public sealed class PlanProposalCandidate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProposalRunId { get; set; }
    public Guid DriverId { get; set; }
    public Guid VehicleId { get; set; }
    public bool Selected { get; set; }
    [MaxLength(40)] public required string Classification { get; set; }
    [MaxLength(40)] public required string PositionSource { get; set; }
    public decimal Score { get; set; }
    public required string ScoreComponentsJson { get; set; }
    public required string ConstraintResultsJson { get; set; }
    public required string ExplanationJson { get; set; }
}
public sealed class PlanProposalAllocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProposalRunId { get; set; }
    public Guid SourceLineId { get; set; }
    public int Pallets { get; set; }
    [MaxLength(40)] public string? PalletType { get; set; }
    [MaxLength(200)] public string? CollectionSite { get; set; }
    [MaxLength(200)] public string? DeliverySite { get; set; }
    public int CollectionSequence { get; set; }
    public int DeliverySequence { get; set; }
}
public enum ReferenceIssueStatus { Open, Resolved }
public sealed class OrderReferenceIssue
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MovementId { get; set; }
    public Guid? TransportOrderId { get; set; }
    [MaxLength(40)] public required string ReferenceType { get; set; }
    public ReferenceIssueStatus Status { get; set; } = ReferenceIssueStatus.Open;
    [MaxLength(200)] public string? Owner { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    public DateTimeOffset DetectedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ResolvedAtUtc { get; set; }
    [MaxLength(200)] public string? ResolvedBy { get; set; }
}
public sealed class ReferenceChaseEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ReferenceIssueId { get; set; }
    [MaxLength(40)] public required string EventType { get; set; }
    [MaxLength(320)] public string? Recipient { get; set; }
    [MaxLength(500)] public string? ProviderMessageId { get; set; }
    [MaxLength(500)] public string? ProviderThreadId { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
    [MaxLength(200)] public string? Actor { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public enum OrderStatus { Draft, ReadyToPlan, Planned, InTransit, Delivered, Cancelled }
public sealed class TransportOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? SourceStagedImportId { get; set; }
    public Guid? SourceMovementId { get; set; }
    [MaxLength(80)] public required string Reference { get; set; }
    [MaxLength(40)] public required string CustomerCode { get; set; }
    public DateOnly CollectionDate { get; set; }
    public DateOnly? DeliveryDate { get; set; }
    public DateTimeOffset? DeliveryWindowStartUtc { get; set; }
    public DateTimeOffset? DeliveryWindowEndUtc { get; set; }
    public int? Pallets { get; set; }
    [MaxLength(200)] public string? SellerName { get; set; }
    [MaxLength(80)] public string? MarketName { get; set; }
    [MaxLength(200)] public string? StallNumber { get; set; }
    [MaxLength(1000)] public string? DriverInstructions { get; set; }
    [MaxLength(1000)] public string? MapLink { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.ReadyToPlan;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
public enum LoadStatus { Draft, Planned, Dispatched, InProgress, Completed, Cancelled }
public sealed class Load
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string Reference { get; set; }
    public DateOnly PlanningDate { get; set; }
    public LoadStatus Status { get; set; } = LoadStatus.Draft;
    public Guid? VehicleId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? TrailerId { get; set; }
    [NotMapped] public decimal? RevenueAmount { get; set; }
    [NotMapped] public decimal? FuelSurchargeAmount { get; set; }
    [NotMapped] public decimal? EstimatedCostAmount { get; set; }
    [NotMapped] public decimal? ActualCostAmount { get; set; }
    [NotMapped] public decimal? EstimatedDistanceMiles { get; set; }
    [NotMapped] public decimal? EmptyMiles { get; set; }
    [NotMapped, MaxLength(40)] public string? InvoiceStatus { get; set; }
    [NotMapped, MaxLength(500)] public string? CommercialNotes { get; set; }
    [NotMapped] public decimal? PalletSpacesUsed { get; set; }
    [NotMapped] public decimal? TotalPalletSpaces { get; set; }
    [NotMapped, MaxLength(40)] public string? CapacityType { get; set; }
    [NotMapped, MaxLength(1000)] public string? DepotSplits { get; set; }
    [NotMapped] public decimal? TemperatureC { get; set; }
    [NotMapped, MaxLength(1000)] public string? PlannerNotes { get; set; }
    [NotMapped] public decimal? UtilisationPercent => TotalPalletSpaces > 0 && PalletSpacesUsed is not null
        ? Math.Round(PalletSpacesUsed.Value / TotalPalletSpaces.Value * 100, 1)
        : null;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<LoadStop> Stops { get; set; } = [];
}
public sealed class LoadStop
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoadId { get; set; }
    public Guid? OrderId { get; set; }
    public int Sequence { get; set; }
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(500)] public string? Address { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public DateTimeOffset? PlannedArrivalUtc { get; set; }
    // Stored in the audited planning register JSON. Keeping this non-mapped avoids
    // making the live planner dependent on an immediate database migration.
    [NotMapped, MaxLength(1000)] public string? PlannerNote { get; set; }
}
public sealed class IntegrationMapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(40)] public required string Provider { get; set; }
    [MaxLength(200)] public required string ExternalKey { get; set; }
    [MaxLength(200)] public string? ExternalLabel { get; set; }
    [MaxLength(20)] public required string TmsEntityType { get; set; }
    public Guid TmsEntityId { get; set; }
    public bool Active { get; set; } = true;
    [MaxLength(1000)] public string? Notes { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? UpdatedBy { get; set; }
    [MaxLength(40)] public string? MappingKind { get; set; }
    [MaxLength(300)] public string? NormalizedExternalValue { get; set; }
    [MaxLength(320)] public string? SenderPattern { get; set; }
    [MaxLength(120)] public string? TemplateName { get; set; }
    [MaxLength(40)] public string? TemplateVersion { get; set; }
    public decimal? ConfidenceThreshold { get; set; }
    public DateTimeOffset? EffectiveFromUtc { get; set; }
    public DateTimeOffset? EffectiveToUtc { get; set; }
}
public enum DriverDispatchStatus { Dispatched, Accepted, ArrivedCollection, Loaded, ArrivedDelivery, Delivered, IssueReported }
public sealed class DriverStatusLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoadId { get; set; }
    public Guid? DriverId { get; set; }
    [MaxLength(40)] public required string Status { get; set; }
    [MaxLength(1000)] public string? Notes { get; set; }
    [MaxLength(200)] public string? CapturedBy { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
