using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;

public sealed class EtaSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid LoadId { get; set; }
    public Guid StopId { get; set; }
    public Guid? OrderId { get; set; }
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EtaUtc { get; set; }
    [MaxLength(20)] public required string Source { get; set; }
    [MaxLength(40)] public required string Risk { get; set; }
    [MaxLength(40)] public required string TachoStatus { get; set; }
    public int BreakMinutesIncluded { get; set; }
    public DateTimeOffset? TrackingUpdatedAtUtc { get; set; }
}