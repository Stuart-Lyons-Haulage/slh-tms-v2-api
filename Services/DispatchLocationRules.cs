using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record DispatchResolvedPosition(
    decimal Latitude,
    decimal Longitude,
    DateTimeOffset AtUtc,
    string Label);

/// <summary>
/// Resolves a driver's parked/sign-off position from the vehicle recorded on the
/// driver's most recent Tacho duty. This is intentionally bounded around duty end so
/// a vehicle that is later moved by another driver cannot silently become the previous
/// driver's planning location.
/// </summary>
public static class DispatchLocationRules
{
    private static readonly TimeSpan MaximumPreEndLookback = TimeSpan.FromHours(12);
    private static readonly TimeSpan MaximumPostEndGrace = TimeSpan.FromMinutes(30);

    public static DispatchResolvedPosition? ResolveTachoSignOffPosition(
        string? vehicleRegistration,
        DateTimeOffset? dutyEndUtc,
        IEnumerable<VehicleTrackingEvent> source)
    {
        if (string.IsNullOrWhiteSpace(vehicleRegistration) || dutyEndUtc is null) return null;

        var vehicleKey = ExecutionIdentityResolver.NormaliseVehicle(vehicleRegistration);
        if (vehicleKey.Length == 0) return null;

        var dutyEnd = dutyEndUtc.Value;
        var from = dutyEnd - MaximumPreEndLookback;
        var through = dutyEnd + MaximumPostEndGrace;
        var candidates = source
            .Where(item =>
                item.EventTimeUtc >= from &&
                item.EventTimeUtc <= through &&
                ExecutionIdentityResolver.NormaliseVehicle(item.VehicleIdentifier) == vehicleKey)
            .ToList();

        // Prefer the final GPS fix at or before duty end. A small post-end grace is only
        // used when there was no pre-end fix at all (for example provider delivery lag).
        var selected = candidates
            .Where(item => item.EventTimeUtc <= dutyEnd)
            .OrderByDescending(item => item.EventTimeUtc)
            .FirstOrDefault()
            ?? candidates
                .Where(item => item.EventTimeUtc > dutyEnd)
                .OrderBy(item => item.EventTimeUtc)
                .FirstOrDefault();

        if (selected is null) return null;

        return new DispatchResolvedPosition(
            selected.Latitude,
            selected.Longitude,
            selected.EventTimeUtc,
            $"Tacho sign-off · {vehicleRegistration.Trim()}");
    }
}
