namespace Slh.Tms.Api.Contracts;

[Flags]
public enum DispatchSkill
{
    None = 0,
    DoubleDecker = 1 << 0,
    MarketRun = 1 << 1,
    HazChem = 1 << 2,
    Moffett = 1 << 3,
    TailLift = 1 << 4,
    RefrigeratedUnit = 1 << 5,
    ManualHandling = 1 << 6
}

public sealed record DispatchGeoPointDto(decimal Latitude, decimal Longitude);

public sealed record DispatchCollectionPointDto(
    string Name,
    decimal? Latitude,
    decimal? Longitude);

public sealed record DispatchTachoDataDto(
    int CurrentDutyDay,
    DateTimeOffset? ShiftEndTimeUtc,
    decimal WeeklyWorkingTime,
    decimal DailyDrivingTime,
    bool BreakCompliance,
    string? LastVehicleRegistration,
    int RequiredRestPeriod,
    int ReducedDailyRestsUsed,
    int? DailyDrivingLimitMinutes,
    int? DriveAvailablePlanningDayMinutes,
    int? WorkAvailableWeekMinutes,
    bool ReducedDailyRestAvailable = false);

public sealed record DispatchTrackingDataDto(
    DispatchGeoPointDto? LastKnownPosition,
    string? LastStopName,
    DateTimeOffset? LastPositionAtUtc);

public sealed record DispatchDriverDto(
    Guid DriverId,
    string DriverCode,
    string Name,
    string EmploymentType,
    DispatchSkill Skills,
    IReadOnlyList<DateOnly> HolidayDates,
    IReadOnlyList<DayOfWeek> ContractedDays,
    string? HomeDepot,
    DispatchTachoDataDto TachoData,
    DispatchTrackingDataDto TrackingData,
    bool NeedsReturn,
    DateTimeOffset? AvailableFrom,
    bool IsBlocked,
    string? BlockedReason,
    Guid? SuggestedRunId,
    string? SuggestedRunReference,
    decimal? DistanceToSuggestedCollectionMiles,
    bool BackloadCandidate,
    decimal? DeadheadReductionMiles,
    string? Suggestion);

public sealed record DispatchRunDto(
    Guid RunId,
    string Reference,
    DispatchCollectionPointDto CollectionPoint,
    DispatchSkill RequiredSkills,
    bool RequiresDoubleDeck,
    bool RequiresRefrigerated,
    bool IsBackload,
    bool IsOvernightMarket,
    bool IsSouthbound);

public sealed record DispatchAvailableTimesRequest(
    DateOnly PlanningDate,
    IReadOnlyList<Guid> DriverIds,
    IReadOnlyList<Guid>? ReducedRestDriverIds = null);

public sealed record DispatchAvailableTimeDto(
    Guid DriverId,
    DateTimeOffset? AvailableFrom,
    int RequiredRestPeriod,
    decimal WeeklyWorkingTimeUsed,
    decimal DailyDrivingTimeUsed,
    string WtdStatus,
    string? BreachDetail);

public sealed record DispatchAllocationRequest(
    Guid DriverId,
    Guid VehicleId,
    Guid? TrailerId,
    Guid RunId,
    DateTimeOffset PlannedStartTime,
    bool UseReducedDailyRest = false);

public sealed record DispatchLockRequest(
    DateOnly PlanningDate,
    IReadOnlyList<DispatchAllocationRequest> Allocations);

public sealed record DispatchLockFailure(
    Guid DriverId,
    Guid? RunId,
    string Reason);

public sealed record DispatchLockResponse(
    bool Success,
    IReadOnlyList<DispatchLockFailure> Failures);
