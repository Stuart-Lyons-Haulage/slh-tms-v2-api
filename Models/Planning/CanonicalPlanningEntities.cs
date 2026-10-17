using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models.Planning;

public enum RunStatus
{
    Draft,
    Allocated,
    Dispatched,
    InProgress,
    Completed,
    Cancelled
}

public sealed class Run
{
    private string _runReference = string.Empty;

    public Guid RunId { get; set; } = Guid.NewGuid();
    public DateOnly PlanningDate { get; set; }

    [MaxLength(80)]
    public required string RunReference
    {
        get => _runReference;
        set => _runReference = NormalizeReference(value);
    }

    public RunStatus Status { get; set; } = RunStatus.Draft;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? UpdatedBy { get; set; }

    [Timestamp]
    public byte[] RowVersion { get; set; } = [];

    public List<RunStop> Stops { get; set; } = [];
    public List<RunOrderAllocation> OrderAllocations { get; set; } = [];
    public RunResourceAllocation? ResourceAllocation { get; set; }
    public List<RunStatusHistory> StatusHistory { get; set; } = [];
    public RunTrackingState? TrackingState { get; set; }

    public static string NormalizeReference(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var normalized = new string(value
            .Trim()
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());

        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("Run reference must contain at least one letter or digit.", nameof(value));
        if (normalized.Length > 80)
            throw new ArgumentException("Normalized run reference cannot exceed 80 characters.", nameof(value));

        return normalized;
    }
}

public sealed class RunStop
{
    public Guid RunStopId { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Run Run { get; set; } = null!;
    public int Sequence { get; set; }
    public Guid SiteId { get; set; }
    public Slh.Tms.Api.Models.Site Site { get; set; } = null!;
    public DateTimeOffset? PlannedArrival { get; set; }
    public DateTimeOffset? PlannedDeparture { get; set; }
    public DateTimeOffset? ActualArrival { get; set; }
    public DateTimeOffset? ActualDeparture { get; set; }
    public Guid? GeofenceVisitId { get; set; }
    public Slh.Tms.Api.Models.GeofenceVisit? GeofenceVisit { get; set; }
}

public sealed class RunOrderAllocation
{
    public Guid AllocationId { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Run Run { get; set; } = null!;
    public Guid OrderId { get; set; }
    public Slh.Tms.Api.Models.TransportOrder Order { get; set; } = null!;
    public int Pallets { get; set; }
    public int Trolleys { get; set; }
    public int Trays { get; set; }
    public decimal CapacityUnits { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? UpdatedBy { get; set; }
    public Guid SourceRevisionId { get; set; }
    public Slh.Tms.Api.Models.OrderRevision SourceRevision { get; set; } = null!;
}

public sealed class RunResourceAllocation
{
    public Guid ResourceAllocationId { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Run Run { get; set; } = null!;
    public Guid DriverId { get; set; }
    public Slh.Tms.Api.Models.Driver Driver { get; set; } = null!;
    public Guid VehicleId { get; set; }
    public Slh.Tms.Api.Models.Vehicle Vehicle { get; set; } = null!;
    public Guid TrailerId { get; set; }
    public Slh.Tms.Api.Models.Trailer Trailer { get; set; } = null!;
    public DateTimeOffset AllocatedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? AllocatedBy { get; set; }
}

public sealed class RunStatusHistory
{
    public Guid HistoryId { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Run Run { get; set; } = null!;
    public RunStatus Status { get; set; }
    public DateTimeOffset ChangedAt { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(200)] public string? ChangedBy { get; set; }
    [MaxLength(1000)] public string? Note { get; set; }
}

public sealed class RunTrackingState
{
    public Guid RunId { get; set; }
    public Run Run { get; set; } = null!;
    public decimal? LastLatitude { get; set; }
    public decimal? LastLongitude { get; set; }
    public DateTimeOffset? LastUpdated { get; set; }
    public int? ETAMinutes { get; set; }
    [MaxLength(80)] public string? TrackingSource { get; set; }
}
