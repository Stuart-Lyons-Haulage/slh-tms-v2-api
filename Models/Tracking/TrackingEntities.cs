using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Slh.Tms.Api.Models.Tracking;

/// <summary>
/// Represents a single vehicle tracking event from an external tracking provider.
/// Events are deduplicated by ProviderName + ProviderEventId combination.
/// </summary>
public sealed class VehicleTrackingEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(50)] public required string ProviderName { get; set; }
    [MaxLength(100)] public required string ProviderEventId { get; set; }
    [MaxLength(100)] public required string VehicleIdentifier { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset EventTimeUtc { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal? SpeedKph { get; set; }
    public bool? IgnitionOn { get; set; }
    public bool? IsMoving { get; set; }
    public required string RawPayload { get; set; }
    [MaxLength(50)] public string? MatchStatus { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

internal static class LiveDriverIdentityRegistry
{
    private sealed record Identity(string? Name, string? CardNumber, DateTimeOffset ObservedAtUtc);
    private static readonly ConcurrentDictionary<string, Identity> identities = new(StringComparer.OrdinalIgnoreCase);

    internal static void Update(string vehicleIdentifier, string? name, string? cardNumber, DateTimeOffset observedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(vehicleIdentifier) || (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(cardNumber))) return;
        identities.AddOrUpdate(
            vehicleIdentifier.Trim(),
            _ => new Identity(name?.Trim(), cardNumber?.Trim(), observedAtUtc),
            (_, current) => observedAtUtc >= current.ObservedAtUtc ? new Identity(name?.Trim() ?? current.Name, cardNumber?.Trim() ?? current.CardNumber, observedAtUtc) : current);
    }

    internal static string? Name(string vehicleIdentifier) =>
        identities.TryGetValue(vehicleIdentifier.Trim(), out var identity) ? identity.Name : null;

    internal static string? CardNumber(string vehicleIdentifier) =>
        identities.TryGetValue(vehicleIdentifier.Trim(), out var identity) ? identity.CardNumber : null;
}

/// <summary>
/// Represents the latest known tracking status for a vehicle.
/// Updated whenever a new VehicleTrackingEvent is successfully processed.
/// </summary>
public sealed class VehicleLiveStatus
{
    private string? currentDriverName;
    private string? currentDriverCardNumber;

    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(100)] public required string VehicleIdentifier { get; set; }
    public DateTimeOffset LastEventTimeUtc { get; set; }
    public DateTimeOffset LastReceivedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public decimal? SpeedKph { get; set; }
    public bool? IgnitionOn { get; set; }
    public bool? IsMoving { get; set; }
    [MaxLength(100)] public string? LastKnownStatus { get; set; }

    // Driver identity changes vehicle throughout the day, so keep it out of master
    // data while retaining the latest RoadTech identity in the running API process.
    // The one-minute tracking ingestion repopulates this immediately after restart.
    [NotMapped, MaxLength(200)]
    public string? CurrentDriverName
    {
        get => currentDriverName ?? LiveDriverIdentityRegistry.Name(VehicleIdentifier);
        set
        {
            currentDriverName = value;
            LiveDriverIdentityRegistry.Update(VehicleIdentifier, value, currentDriverCardNumber, LastEventTimeUtc);
        }
    }

    [NotMapped, MaxLength(100)]
    public string? CurrentDriverCardNumber
    {
        get => currentDriverCardNumber ?? LiveDriverIdentityRegistry.CardNumber(VehicleIdentifier);
        set
        {
            currentDriverCardNumber = value;
            LiveDriverIdentityRegistry.Update(VehicleIdentifier, currentDriverName, value, LastEventTimeUtc);
        }
    }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
