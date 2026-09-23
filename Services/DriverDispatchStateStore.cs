using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record DriverDispatchState(
    Guid LoadId,
    DateTimeOffset? PlannedStartUtc,
    DateTimeOffset UpdatedAtUtc,
    string? UpdatedBy,
    string? Source = null);

/// <summary>
/// Stores planner-only dispatch state without requiring a live schema migration. The run remains
/// the operational source of truth; this store adds the planned yard start used by Dispatch,
/// dashboards, audit timelines and Tacho sign-on comparisons.
/// </summary>
public static class DriverDispatchStateStore
{
    private const string EntityType = "rundispatchstate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<IReadOnlyDictionary<Guid, DriverDispatchState>> ReadAsync(TmsDbContext db, IEnumerable<Guid> loadIds, CancellationToken ct)
    {
        var ids = loadIds.Distinct().ToHashSet();
        if (ids.Count == 0) return new Dictionary<Guid, DriverDispatchState>();
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);
        var result = new Dictionary<Guid, DriverDispatchState>();
        foreach (var row in rows)
        {
            try
            {
                var state = JsonSerializer.Deserialize<DriverDispatchState>(row.PayloadJson, JsonOptions);
                if (state is null || !ids.Contains(state.LoadId) || result.ContainsKey(state.LoadId)) continue;
                result[state.LoadId] = state;
            }
            catch (JsonException) { }
        }
        return result;
    }

    public static async Task<DriverDispatchState> SetPlannedStartAsync(
        TmsDbContext db,
        Guid loadId,
        DateTimeOffset? plannedStartUtc,
        string? actor,
        CancellationToken ct,
        string source = "Manual override")
    {
        var now = DateTimeOffset.UtcNow;
        var state = new DriverDispatchState(loadId, plannedStartUtc, now, actor, source);
        var key = Key(loadId);
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.EntityType == EntityType && item.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = EntityType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "Driver Dispatch",
                Status = StagingStatus.Promoted,
                ReceivedAtUtc = now
            };
            db.StagedImports.Add(row);
        }
        row.PayloadJson = JsonSerializer.Serialize(state, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = now;
        row.ReviewedBy = actor;
        row.ReviewNote = plannedStartUtc is null
            ? "Planned dispatch start cleared."
            : $"Planned dispatch start set to {plannedStartUtc:O} ({source}).";
        await db.SaveChangesAsync(ct);
        return state;
    }

    private static string Key(Guid loadId) => $"{EntityType}:{loadId:N}";
}
