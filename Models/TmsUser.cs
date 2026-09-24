using System.ComponentModel.DataAnnotations;

namespace Slh.Tms.Api.Models;

public sealed class TmsUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(120)] public required string Username { get; set; }
    [MaxLength(160)] public required string DisplayName { get; set; }
    [MaxLength(512)] public required string PasswordHash { get; set; }
    [MaxLength(80)] public required string Role { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAtUtc { get; set; }
    public DateTimeOffset? PasswordChangedAtUtc { get; set; }
}
