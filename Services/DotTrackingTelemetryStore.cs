using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed class DotTrackingTelemetryStore(
    TmsDbContext db,
    ILogger<DotTrackingTelemetryStore> logger,
    RoadTechLiveSnapshot? liveSnapshot = null)
{
    private static readonly ConcurrentDictionary<string, byte> NormalisedProviderIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MaximumLiveFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaximumStoredFutureRepairWindow = TimeSpan.FromHours(48);

    public async Task PersistAsync(IEnumerable<DotTelemetryRecord> records, CancellationToken ct, bool markAsLiveReceipt = true)
    {
        var batch = records.ToList();
        // In production every live caller is reading the same central RoadTech snapshot. Reuse
        // that snapshot's capture time when updating receipt freshness, so a wallboard refresh
        // cannot make an old provider batch look newer than the actual RoadTech read.
        var receivedAt = markAsLiveReceipt
            ? liveSnapshot?.Read().CapturedAtUtc ?? DateTimeOffset.UtcNow
            : DateTimeOffset.UtcNow;
        var futureCeiling = receivedAt.Add(MaximumLiveFutureSkew);
        var currentAnchors = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in batch)
        {
            var rawIdentifier = (record.VehicleIdentifier ?? string.Empty).Trim();
            var canonicalIdentifier = ExecutionIdentityResolver.NormaliseVehicle(rawIdentifier);
            if (canonicalIdentifier.Length == 0) continue;

            // GetCurrentTelemetry is a receipt-time assertion that this is the vehicle's
            // current position. A provider timestamp materially in the future must never
            // poison the operating-day history or make a wallboard wait until tomorrow.
            // In that exceptional case use the current receipt as the safe live timestamp;
            // historical replay remains provider-time based so its movement chronology is
            // preserved and can repair previously stored rows from the authoritative page.
            var eventTimeUtc = record.EventTimeUtc;
            if (markAsLiveReceipt && eventTimeUtc > futureCeiling)
            {
                logger.LogWarning(
                    "RoadTech returned future event time {ProviderEventTimeUtc} for {VehicleIdentifier}; normalising current telemetry to receipt time {ReceivedAtUtc}.",
                    eventTimeUtc,
                    canonicalIdentifier,
                    receivedAt);
                eventTimeUtc = receivedAt;
            }
            if (markAsLiveReceipt)
                currentAnchors[canonicalIdentifier] = currentAnchors.TryGetValue(canonicalIdentifier, out var existingAnchor) && existingAnchor > eventTimeUtc
                    ? existingAnchor
                    : eventTimeUtc;

            var hasGps = record.Latitude is not null && record.Longitude is not null;
            if (!markAsLiveReceipt && hasGps)
            {
                // Never create breadcrumb rows. This is solely a bounded repair for
                // legacy rows that pre-date the operational-only retention policy.
                var legacy = await db.VehicleTrackingEvents.FirstOrDefaultAsync(item =>
                    item.ProviderName == "RoadTech Falcon" && item.ProviderEventId == record.ProviderEventId, ct);
                if (legacy is not null)
                {
                    legacy.VehicleIdentifier = canonicalIdentifier;
                    legacy.EventTimeUtc = eventTimeUtc;
                    legacy.Latitude = record.Latitude!.Value;
                    legacy.Longitude = record.Longitude!.Value;
                    legacy.SpeedKph = record.SpeedKph;
                    legacy.IgnitionOn = record.IgnitionOn;
                    legacy.IsMoving = record.IsMoving;
                    legacy.RawPayload = record.RawPayload;
                    legacy.MatchStatus = "LegacyClockRepaired";
                }
            }
            else if (markAsLiveReceipt && hasGps)
            {
                // Repair an already-retained legacy row if it is poisoned, but never
                // create a new breadcrumb row from the live poll.
                var legacy = await db.VehicleTrackingEvents.FirstOrDefaultAsync(item =>
                    item.ProviderName == "RoadTech Falcon" && item.ProviderEventId == record.ProviderEventId, ct);
                if (legacy is not null && legacy.EventTimeUtc > futureCeiling)
                {
                    legacy.VehicleIdentifier = canonicalIdentifier;
                    legacy.EventTimeUtc = eventTimeUtc;
                    legacy.Latitude = record.Latitude!.Value;
                    legacy.Longitude = record.Longitude!.Value;
                    legacy.SpeedKph = record.SpeedKph;
                    legacy.IgnitionOn = record.IgnitionOn;
                    legacy.IsMoving = record.IsMoving;
                    legacy.RawPayload = record.RawPayload;
                    legacy.MatchStatus = "LegacyClockRepaired";
                }
            }
            // RoadTech owns raw telemetry and breadcrumb history. The TMS deliberately
            // does not insert a VehicleTrackingEvent for each poll; it holds one current
            // state row and creates a GeofenceVisit only when the vehicle crosses a site.
            // A non-live/historical call is consequently a no-op for SQL persistence.
            if (!markAsLiveReceipt || !hasGps) continue;

            var live = await ResolveLiveStatusAsync(rawIdentifier, canonicalIdentifier, ct);

            if (live is null)
            {
                live = new VehicleLiveStatus
                {
                    VehicleIdentifier = canonicalIdentifier,
                    LastEventTimeUtc = eventTimeUtc,
                    LastReceivedAtUtc = receivedAt,
                    Latitude = record.Latitude!.Value,
                    Longitude = record.Longitude!.Value,
                    SpeedKph = record.SpeedKph,
                    IgnitionOn = record.IgnitionOn,
                    IsMoving = record.IsMoving,
                    LastKnownStatus = record.Status,
                    CurrentDriverName = record.DriverName,
                    CurrentDriverCardNumber = record.DriverCardNumber,
                    UpdatedAtUtc = receivedAt
                };
                db.VehicleLiveStatuses.Add(live);
            }
            else
            {
                live.VehicleIdentifier = canonicalIdentifier;

                // Receipt freshness answers whether GetCurrentTelemetry is actively
                // confirming this GPS position. Stationary vehicles may keep the same
                // provider event timestamp throughout a long unload. Every consumer of
                // the same central snapshot therefore retains the same receipt timestamp.
                live.LastReceivedAtUtc = receivedAt;
                live.UpdatedAtUtc = receivedAt;

                // Normal operation must not roll position backwards. A stored live timestamp
                // that is itself in the future is different: it is poisoned state, and the
                // newly normalised current snapshot is the evidence needed to recover it.
                if (live.LastEventTimeUtc > futureCeiling || eventTimeUtc >= live.LastEventTimeUtc)
                {
                    live.LastEventTimeUtc = eventTimeUtc;
                    live.Latitude = record.Latitude!.Value;
                    live.Longitude = record.Longitude!.Value;
                    live.SpeedKph = record.SpeedKph;
                    live.IgnitionOn = record.IgnitionOn;
                    live.IsMoving = record.IsMoving;
                    live.LastKnownStatus = record.Status;
                    if (!string.IsNullOrWhiteSpace(record.DriverName)) live.CurrentDriverName = record.DriverName;
                    if (!string.IsNullOrWhiteSpace(record.DriverCardNumber)) live.CurrentDriverCardNumber = record.DriverCardNumber;
                }
            }
        }

        if (markAsLiveReceipt && currentAnchors.Count > 0)
            await RepairFutureStoredEventsAsync(currentAnchors, receivedAt, futureCeiling, ct);

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        if (batch.Count > 0)
            logger.LogDebug("Updated current state from {RecordCount} RoadTech telemetry record(s); breadcrumb history remains in RoadTech.", batch.Count);
    }

    private async Task RepairFutureStoredEventsAsync(
        IReadOnlyDictionary<string, DateTimeOffset> currentAnchors,
        DateTimeOffset receivedAt,
        DateTimeOffset futureCeiling,
        CancellationToken ct)
    {
        var repairCeiling = receivedAt.Add(MaximumStoredFutureRepairWindow);
        var globalAnchor = currentAnchors.Values.Max();
        var futureRows = await db.VehicleTrackingEvents
            .Where(item =>
                item.ProviderName == "RoadTech Falcon" &&
                item.EventTimeUtc > futureCeiling &&
                item.EventTimeUtc <= repairCeiling)
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var group in futureRows.GroupBy(row => ExecutionIdentityResolver.NormaliseVehicle(row.VehicleIdentifier), StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(group.Key)) continue;
            if (!currentAnchors.TryGetValue(group.Key, out var currentAnchor))
                currentAnchor = globalAnchor;
            var newestFuture = group.Max(row => row.EventTimeUtc);
            var skew = newestFuture - currentAnchor;
            if (skew <= MaximumLiveFutureSkew || skew > MaximumStoredFutureRepairWindow) continue;

            foreach (var row in group)
            {
                row.EventTimeUtc -= skew;
                row.MatchStatus = string.IsNullOrWhiteSpace(row.MatchStatus)
                    ? "ClockRepaired"
                    : row.MatchStatus.Contains("ClockRepaired", StringComparison.OrdinalIgnoreCase)
                        ? row.MatchStatus
                        : $"{row.MatchStatus};ClockRepaired";
                repaired++;
            }
        }

        if (repaired > 0)
            logger.LogWarning(
                "Repaired {RecordCount} stored RoadTech tracking event time(s) that were ahead of the current Falcon snapshot.",
                repaired);
    }

    private async Task<VehicleLiveStatus?> ResolveLiveStatusAsync(
        string rawIdentifier,
        string canonicalIdentifier,
        CancellationToken ct)
    {
        var localCandidates = db.VehicleLiveStatuses.Local
            .Where(item =>
                SameCanonical(item.VehicleIdentifier, canonicalIdentifier) ||
                string.Equals(item.VehicleIdentifier, rawIdentifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var databaseCandidates = await db.VehicleLiveStatuses
            .Where(item =>
                item.VehicleIdentifier == canonicalIdentifier ||
                item.VehicleIdentifier == rawIdentifier)
            .ToListAsync(ct);

        var candidates = localCandidates
            .Concat(databaseCandidates)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .ToList();

        if (candidates.Count == 0) return null;

        // Prefer an already-persisted canonical row. If historical/raw aliases have
        // created more than one logical live row, collapse them before renaming so the
        // unique VehicleIdentifier index cannot fail during SaveChanges.
        var keeper = candidates
            .OrderByDescending(item => db.Entry(item).State != EntityState.Added)
            .ThenByDescending(item => string.Equals(item.VehicleIdentifier, canonicalIdentifier, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.LastReceivedAtUtc)
            .ThenByDescending(item => item.LastEventTimeUtc)
            .First();

        var duplicates = candidates.Where(item => item.Id != keeper.Id).ToList();
        var hasPersistedDuplicates = duplicates.Any(item => db.Entry(item).State != EntityState.Added);

        foreach (var duplicate in duplicates)
        {
            if (duplicate.LastReceivedAtUtc > keeper.LastReceivedAtUtc)
            {
                keeper.LastReceivedAtUtc = duplicate.LastReceivedAtUtc;
                keeper.UpdatedAtUtc = duplicate.UpdatedAtUtc;
            }

            if (duplicate.LastEventTimeUtc > keeper.LastEventTimeUtc)
            {
                keeper.LastEventTimeUtc = duplicate.LastEventTimeUtc;
                keeper.Latitude = duplicate.Latitude;
                keeper.Longitude = duplicate.Longitude;
                keeper.SpeedKph = duplicate.SpeedKph;
                keeper.IgnitionOn = duplicate.IgnitionOn;
                keeper.IsMoving = duplicate.IsMoving;
                keeper.LastKnownStatus = duplicate.LastKnownStatus;
            }

            db.VehicleLiveStatuses.Remove(duplicate);
        }

        if (hasPersistedDuplicates)
        {
            // Delete aliases before changing the keeper to the canonical identifier.
            // This avoids a transient unique-key collision in SQL Server.
            await db.SaveChangesAsync(ct);
        }

        keeper.VehicleIdentifier = canonicalIdentifier;
        return keeper;
    }

    private static bool SameCanonical(string value, string canonicalIdentifier) =>
        string.Equals(
            ExecutionIdentityResolver.NormaliseVehicle(value),
            canonicalIdentifier,
            StringComparison.OrdinalIgnoreCase);
}
