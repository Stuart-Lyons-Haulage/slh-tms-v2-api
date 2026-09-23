namespace Slh.Tms.Api.Models;

public sealed record GeneratePlanProposalRequest(DateOnly PlanningDate, string? Period = null);
public sealed record PlanProposalWarning(string Code, string Severity, string Message);
public sealed record PlanProposalAllocationResult(
    Guid Id,
    Guid SourceLineId,
    int Pallets,
    string? PalletType,
    string? CollectionSite,
    string? DeliverySite,
    int CollectionSequence,
    int DeliverySequence);
public sealed record PlanProposalCandidateResult(
    Guid Id,
    Guid DriverId,
    Guid VehicleId,
    bool Selected,
    string Classification,
    string PositionSource,
    decimal Score,
    IReadOnlyList<PlanningScoreComponent> ScoreComponents,
    IReadOnlyList<PlanningConstraintResult> ConstraintResults,
    IReadOnlyList<string> Explanations);
public sealed record PlanProposalRunResult(
    Guid Id,
    int Sequence,
    string Reference,
    bool IsLocked,
    Guid? LiveLoadId,
    string Classification,
    Guid? DriverId,
    Guid? VehicleId,
    Guid? TrailerId,
    string? PositionSource,
    int CapacityPallets,
    int PlannedPallets,
    decimal Score,
    IReadOnlyList<PlanningScoreComponent> ScoreComponents,
    IReadOnlyList<string> Explanations,
    IReadOnlyList<PlanProposalAllocationResult> Allocations,
    IReadOnlyList<PlanProposalCandidateResult> Candidates,
    string? DriverName = null,
    string? VehicleRegistration = null,
    string? TrailerNumber = null);
public sealed record PlanProposalResult(
    Guid Id,
    DateOnly PlanningDate,
    string Period,
    int Version,
    string Status,
    string Classification,
    string InputHash,
    DateTimeOffset EvidenceCapturedAtUtc,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    IReadOnlyList<PlanProposalWarning> Warnings,
    IReadOnlyList<PlanProposalRunResult> Runs);

public sealed record PlanningDriverEvidence(
    Guid DriverId,
    int RequiredDriveMinutes,
    int? DriveAvailableTodayMinutes,
    DateTimeOffset? TachoObservedAtUtc,
    DateTimeOffset EvidenceCapturedAtUtc,
    int ConsecutiveDays,
    bool AlternatingSixthDayAllowed);
public sealed record PlanningConstraintResult(string Code, bool Passed, string Severity, string Explanation);
public sealed record PlanningConstraintEvaluation(string Classification, IReadOnlyList<PlanningConstraintResult> Results);
public sealed record PlanningCandidateEvidence(
    Guid VehicleId,
    Guid DriverId,
    decimal? CollectionLatitude,
    decimal? DeliveryLatitude,
    decimal? LiveLatitude,
    DateTimeOffset? LiveObservedAtUtc,
    decimal? PreviousEndLatitude,
    DateTimeOffset? PreviousEndObservedAtUtc,
    int ConsecutiveDays,
    DateTimeOffset EvidenceCapturedAtUtc,
    decimal UtilisationPercent);
public sealed record PlanningScoreComponent(string Code, decimal Value, string Explanation);
public sealed record PlanningCandidateScore(
    decimal Total,
    string PositionSource,
    decimal? StartLatitude,
    IReadOnlyList<PlanningScoreComponent> Components,
    IReadOnlyList<string> Explanations);
