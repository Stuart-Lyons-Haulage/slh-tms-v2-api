using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;

public sealed class MasterDataAudit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public required string EntityType { get; set; }
    public Guid EntityId { get; set; }
    [MaxLength(80)] public required string Action { get; set; }
    [MaxLength(4000)] public string? ChangesJson { get; set; }
    [MaxLength(200)] public string? ChangedBy { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
