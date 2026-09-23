using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class GeofenceAutoSeed
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const int SupplementalFenceCount = 0;
    private const string LocationOnly = "LOCATION_ONLY";
    private static readonly string[] SupplementalFenceNames =
    [
    ];

    public static async Task<GeofenceAutoSeedResult> EnsureAsync(TmsDbContext db, CancellationToken ct)
    {
        await GeofenceRuntimeRepair.EnsureAsync(db, ct);
        await GeofenceRunProgression.EnsureSchemaAsync(db, ct);

        // Run these even when all 736 fences already exist. Historic imports interpreted
        // DOT/Falcon site_no=1 as SLH Site 1, and Site Master aliases may have been added
        // after the original geofence import. Both repairs are idempotent and keep the
        // existing polygon/visit history intact.
        await GeofenceProviderPlaceholderRepair.EnsureAsync(db, ct);
        await GeofenceSiteAliasRepair.EnsureAsync(db, ct);

        var active = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
        if (await IsCompleteAsync(db, active, ct)) return new GeofenceAutoSeedResult(false, active, 0, 0);

        await Gate.WaitAsync(ct);
        try
        {
            active = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
            if (await IsCompleteAsync(db, active, ct)) return new GeofenceAutoSeedResult(false, active, 0, 0);

            // A SiteId chosen from the Site Master geofence dropdown is an explicit operator
            // decision and must survive any later provider/embedded catalogue refresh. The
            // Falcon import routine updates existing rows and can otherwise replace SiteId /
            // SiteNumber with its automatic name match. Preserve the exact linked row ids,
            // including LOCATION_ONLY overrides, and restore only those identity fields after
            // the geometry/catalogue refresh. Geometry and operational settings remain fresh.
            var explicitlyLinkedRows = await db.SiteGeofences.AsNoTracking()
                .Where(x => x.Active && (x.SiteId != null || x.SiteNumber == LocationOnly))
                .ToListAsync(ct);
            var preservedLinks = CaptureExplicitLinks(explicitlyLinkedRows);

            using var document = JsonDocument.Parse(GeofenceSeedPayload.Json);
            var inserted = 0;
            var updated = 0;
            var matched = 0;

            foreach (var record in document.RootElement.EnumerateArray())
            {
                var payload = JsonSerializer.SerializeToElement(new
                {
                    format = "falcon.geofence",
                    version = 1,
                    category = NullableText(record, "category"),
                    category_max_wait_time = NullableInt(record, "category_max_wait_time"),
                    geofences = new[]
                    {
                        new
                        {
                            name = NullableText(record, "name"),
                            max_wait_time = NullableInt(record, "max_wait_time"),
                            pending_entry_minutes = NullableInt(record, "pending_entry_minutes") ?? 0,
                            pending_exit_minutes = NullableInt(record, "pending_exit_minutes") ?? 0,
                            site_no = NullableText(record, "site_no"),
                            points = record.GetProperty("points")
                        }
                    }
                });

                var result = await GeofenceRunProgression.ImportFalconAsync(db, payload, ct);
                inserted += result.Inserted;
                updated += result.Updated;
                matched += result.SiteMatched;
            }

            if (preservedLinks.Count > 0)
            {
                var preservedIds = preservedLinks.Keys.ToList();
                var refreshedRows = await db.SiteGeofences
                    .Where(x => preservedIds.Contains(x.Id))
                    .ToListAsync(ct);
                if (RestoreExplicitLinks(refreshedRows, preservedLinks) > 0)
                    await db.SaveChangesAsync(ct);
            }

            await GeofenceSiteAliasRepair.EnsureAsync(db, ct);
            active = await db.SiteGeofences.AsNoTracking().CountAsync(x => x.Active, ct);
            return new GeofenceAutoSeedResult(true, active, inserted + updated, matched);
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static IReadOnlyDictionary<Guid, GeofencePreservedLink> CaptureExplicitLinks(IEnumerable<SiteGeofence> fences)
        => fences
            .Where(fence => fence.Active && (fence.SiteId is not null || string.Equals(fence.SiteNumber?.Trim(), LocationOnly, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(fence => fence.Id)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var fence = group.OrderByDescending(item => item.UpdatedAtUtc).First();
                    return new GeofencePreservedLink(fence.SiteId, fence.SiteNumber);
                });

    internal static int RestoreExplicitLinks(
        IEnumerable<SiteGeofence> fences,
        IReadOnlyDictionary<Guid, GeofencePreservedLink> preservedLinks)
    {
        var restored = 0;
        var now = DateTimeOffset.UtcNow;
        foreach (var fence in fences)
        {
            if (!preservedLinks.TryGetValue(fence.Id, out var preserved)) continue;
            if (fence.SiteId == preserved.SiteId && string.Equals(fence.SiteNumber, preserved.SiteNumber, StringComparison.OrdinalIgnoreCase))
                continue;

            fence.SiteId = preserved.SiteId;
            fence.SiteNumber = preserved.SiteNumber;
            fence.UpdatedAtUtc = now;
            restored++;
        }
        return restored;
    }

    private static async Task<bool> IsCompleteAsync(TmsDbContext db, int active, CancellationToken ct)
    {
        if (active < OperationalGeofencePayload.ExpectedFenceCount + SupplementalFenceCount) return false;
        if (SupplementalFenceNames.Length == 0) return true;
        var required = SupplementalFenceNames.Select(Normalize).ToList();
        var present = await db.SiteGeofences.AsNoTracking()
            .Where(x => x.Active && required.Contains(x.NormalizedName))
            .Select(x => x.NormalizedName)
            .Distinct()
            .CountAsync(ct);
        return present == SupplementalFenceNames.Length;
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? NullableText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined ? value.ToString() : null;

    private static int? NullableInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
}

internal sealed record GeofencePreservedLink(Guid? SiteId, string? SiteNumber);

public sealed record GeofenceAutoSeedResult(bool Seeded, int Active, int Imported, int SiteMatched);
