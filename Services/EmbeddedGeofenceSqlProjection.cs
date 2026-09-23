using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Projects the authoritative embedded RoadTech/Falcon geofence reconstruction into
/// the durable GeofenceVisits SQL table. SQL is a projection/audit store here; the
/// embedded engine remains the source of truth for ENTER/EXIT semantics.
/// </summary>
public static class EmbeddedGeofenceSqlProjection
{
    public static async Task RefreshOperatingDaysAsync(TmsDbContext db, IEnumerable<DateOnly> planningDates, CancellationToken ct)
    {
        foreach (var planningDate in planningDates.Distinct())
        {
            var loads = await ReadLoadsAsync(db, planningDate, ct);
            if (loads.Count == 0) continue;

            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, loads, ct);
            await PersistAsync(db, snapshot, ct);
        }
    }

    public static async Task PersistAsync(TmsDbContext db, EmbeddedGeofenceSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.Visits.Count == 0) return;

        // Production uses Azure SQL and still receives the existing self-repair/seed
        // safeguards. Tests use EF's in-memory provider, where relational SQL helpers
        // are intentionally unavailable; the projection itself does not require them.
        if (db.Database.IsRelational())
        {
            await GeofenceRunProgression.EnsureSchemaAsync(db, ct);
            await GeofenceAutoSeed.EnsureAsync(db, ct);
        }

        var normalizedNames = snapshot.Visits
            .Select(visit => Normalize(visit.Fence.Name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var fences = await db.SiteGeofences
            .Where(fence => fence.Active && normalizedNames.Contains(fence.NormalizedName))
            .ToListAsync(ct);
        var fenceByName = fences
            .GroupBy(fence => fence.NormalizedName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(item => item.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);

        var visitIds = snapshot.Visits.Select(visit => visit.Id).Distinct().ToList();
        var existing = await db.GeofenceVisits
            .Where(visit => visitIds.Contains(visit.Id))
            .ToDictionaryAsync(visit => visit.Id, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var derived in snapshot.Visits)
        {
            if (!fenceByName.TryGetValue(Normalize(derived.Fence.Name), out var fence)) continue;

            if (!existing.TryGetValue(derived.Id, out var row))
            {
                row = new GeofenceVisit
                {
                    Id = derived.Id,
                    GeofenceId = fence.Id,
                    VehicleIdentifier = derived.VehicleIdentifier,
                    EnteredAtUtc = derived.EnteredAtUtc,
                    LastInsideAtUtc = derived.LastInsideAtUtc,
                    DwellMinutes = derived.DwellMinutes,
                    Status = Status(derived),
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                };
                db.GeofenceVisits.Add(row);
                existing[derived.Id] = row;
            }

            row.GeofenceId = fence.Id;
            row.LoadId = derived.LoadId;
            row.LoadStopId = derived.LoadStopId;
            row.VehicleId = derived.VehicleId;
            row.VehicleIdentifier = derived.VehicleIdentifier;
            row.EnteredAtUtc = derived.EnteredAtUtc;
            row.ConfirmedAtUtc = derived.ConfirmedAtUtc;
            row.ExitedAtUtc = derived.ExitedAtUtc;
            row.LastInsideAtUtc = derived.LastInsideAtUtc;
            row.DwellMinutes = derived.DwellMinutes;
            row.Status = Status(derived);
            row.StatusReason = "Projected from embedded RoadTech/Falcon geofence reconstruction.";
            row.UpdatedAtUtc = now;
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
    }

    private static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly planningDate, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();

        try
        {
            foreach (var load in await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
                merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var live = await db.Loads.AsNoTracking()
                .Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate)
                .ToListAsync(ct);
            foreach (var load in live) merged[load.Id] = load;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return merged.Values.Where(load => load.Status != LoadStatus.Cancelled).ToList();
    }

    private static string Status(DerivedVisit visit) => visit.ExitedAtUtc is not null
        ? "Departed"
        : visit.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived";

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
