using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;

public sealed class SiteGeofence
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(200)] public required string Name { get; set; }
    [MaxLength(200)] public required string NormalizedName { get; set; }
    [MaxLength(80)] public string? Category { get; set; }
    public int? CategoryMaxWaitMinutes { get; set; }
    public int? MaxWaitMinutes { get; set; }
    public int PendingEntryMinutes { get; set; }
    public int PendingExitMinutes { get; set; }
    [MaxLength(40)] public string? SiteNumber { get; set; }
    public Guid? SiteId { get; set; }
    public required string PolygonJson { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GeofenceVisit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid GeofenceId { get; set; }
    public Guid? LoadId { get; set; }
    public Guid? LoadStopId { get; set; }
    // The canonical planner uses Runs/RunStops.  Keep these links alongside the
    // legacy Load projection so RoadTech evidence is useful to both operational
    // surfaces while that projection is retired.
    public Guid? RunId { get; set; }
    public Guid? RunStopId { get; set; }
    public Guid? SiteId { get; set; }
    public Guid? VehicleId { get; set; }
    [MaxLength(80)] public required string VehicleIdentifier { get; set; }
    public DateTimeOffset EnteredAtUtc { get; set; }
    public DateTimeOffset? ConfirmedAtUtc { get; set; }
    public DateTimeOffset? ExitedAtUtc { get; set; }
    public DateTimeOffset LastInsideAtUtc { get; set; }
    public int DwellMinutes { get; set; }
    [MaxLength(40)] public required string Status { get; set; }
    [MaxLength(500)] public string? StatusReason { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
