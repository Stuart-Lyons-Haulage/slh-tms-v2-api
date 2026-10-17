using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;

public static class AuditOutboxEventTypes
{
    public const string MasterDataAudit = nameof(MasterDataAudit);
}

public sealed class AuditOutbox
{
    public Guid OutboxId { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public required string EventType { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? FailedAt { get; set; }
    public int RetryCount { get; set; }
}
