using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class PlanningAllocationStore
{
    public const string EntityType = "planningpalletallocation";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<int> SyncPlannerImportAllocationsAsync(
        TmsDbContext db,
        Guid loadId,
        DateOnly date,
        IEnumerable<(Guid OrderId, int Pallets)> allocations,
        string? reviewedBy,
        CancellationToken ct)
    {
        var grouped = allocations
            .Where(allocation => allocation.Pallets > 0)
            .GroupBy(allocation => allocation.OrderId)
            .Select(group => (OrderId: group.Key, Pallets: group.Sum(allocation => allocation.Pallets)))
            .ToList();
        if (grouped.Count == 0) return 0;

        var stored = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(20000)
            .ToListAsync(ct);

        var changed = 0;
        foreach (var allocation in grouped)
        {
            var current = LatestFor(stored, allocation.OrderId, loadId, date);
            if (current?.Pallets == allocation.Pallets) continue;
            Add(db, allocation.OrderId, loadId, allocation.Pallets, date, reviewedBy, "SLH planner import pallet allocation");
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    public static async Task<bool> SyncSingleOrderRunAsync(TmsDbContext db, Load load, string? reviewedBy, CancellationToken ct)
    {
        var candidate = await CandidateAsync(db, load, ct);
        if (candidate is null) return false;

        var latest = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);
        var current = LatestFor(latest, candidate.Value.OrderId, load.Id, load.PlanningDate);
        if (current?.Pallets == candidate.Value.Pallets) return false;

        Add(db, candidate.Value.OrderId, load.Id, candidate.Value.Pallets, load.PlanningDate, reviewedBy, "SLH run quantity sync");
        return true;
    }

    public static async Task<int> ReconcileSingleOrderRunsAsync(TmsDbContext db, IEnumerable<Load> loads, string? reviewedBy, CancellationToken ct)
    {
        var rows = loads.ToList();
        if (rows.Count == 0) return 0;

        var stored = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);
        var changed = 0;

        foreach (var load in rows)
        {
            var candidate = await CandidateAsync(db, load, ct);
            if (candidate is null) continue;
            var current = LatestFor(stored, candidate.Value.OrderId, load.Id, load.PlanningDate);
            if (current?.Pallets == candidate.Value.Pallets) continue;
            Add(db, candidate.Value.OrderId, load.Id, candidate.Value.Pallets, load.PlanningDate, reviewedBy, "SLH run quantity reconciliation");
            changed++;
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }

    private static async Task<(Guid OrderId, int Pallets)?> CandidateAsync(TmsDbContext db, Load load, CancellationToken ct)
    {
        var capacityType = (load.CapacityType ?? string.Empty).Trim();
        if (!capacityType.Equals("Standard pallets", StringComparison.OrdinalIgnoreCase) &&
            !capacityType.Equals("Euro pallets", StringComparison.OrdinalIgnoreCase))
            return null;

        if (load.PalletSpacesUsed is null || load.PalletSpacesUsed < 0 || decimal.Truncate(load.PalletSpacesUsed.Value) != load.PalletSpacesUsed.Value)
            return null;

        var orderIds = load.Stops
            .Where(stop => stop.OrderId is not null)
            .Select(stop => stop.OrderId!.Value)
            .Distinct()
            .Take(2)
            .ToList();

        if (orderIds.Count == 0)
        {
            try
            {
                orderIds = await db.LoadStops.AsNoTracking()
                    .Where(stop => stop.LoadId == load.Id && stop.OrderId != null)
                    .Select(stop => stop.OrderId!.Value)
                    .Distinct()
                    .Take(2)
                    .ToListAsync(ct);
            }
            catch (Exception ex) when (SchemaUnavailable(ex))
            {
                db.ChangeTracker.Clear();
            }
        }

        return orderIds.Count == 1 ? (orderIds[0], decimal.ToInt32(load.PalletSpacesUsed.Value)) : null;
    }

    private static AllocationState? LatestFor(IEnumerable<StagedImport> rows, Guid orderId, Guid loadId, DateOnly date)
    {
        foreach (var row in rows)
        {
            try
            {
                var state = JsonSerializer.Deserialize<AllocationState>(row.PayloadJson, JsonOptions);
                if (state is not null && state.OrderId == orderId && state.LoadId == loadId && state.Date == date) return state;
            }
            catch (JsonException) { }
        }
        return null;
    }

    private static void Add(TmsDbContext db, Guid orderId, Guid loadId, int pallets, DateOnly date, string? reviewedBy, string source)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new AllocationState(orderId, loadId, pallets, date, now, reviewedBy);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"palletallocation:auto:{orderId:N}:{loadId:N}:{now:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            Source = source,
            Status = StagingStatus.Promoted,
            ReviewedAtUtc = now,
            ReviewedBy = reviewedBy,
            ReviewNote = $"Single-order run quantity synchronised automatically at {pallets} pallet{(pallets == 1 ? string.Empty : "s")}."
        });
    }

    private static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AllocationState(Guid OrderId, Guid LoadId, int Pallets, DateOnly Date, DateTimeOffset UpdatedAtUtc, string? UpdatedBy);
}
