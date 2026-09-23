using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class PlanLockStore
{
    public const string ReasonHeader = "X-Plan-Change-Reason";
    private const string BaselineType = "planbaseline";
    private const string ChangeEventType = "planchangeevent";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    // Plan locking is stored in the existing audited staging/planning register.
    // Do not attempt CREATE TABLE at runtime: the production API identity is not
    // required to have DDL permissions for normal operational use.
    public static Task EnsureSchemaAsync(TmsDbContext db, CancellationToken ct) => Task.CompletedTask;

    public static async Task<bool> IsLockedAsync(TmsDbContext db, DateOnly date, CancellationToken ct) =>
        await db.StagedImports.AsNoTracking().AnyAsync(x =>
            x.EntityType == BaselineType &&
            x.IdempotencyKey == BaselineKey(date) &&
            x.Status == StagingStatus.Promoted, ct);

    public static async Task LockAsync(TmsDbContext db, DateOnly date, string? user, CancellationToken ct)
    {
        List<Load> loads;
        try
        {
            loads = await db.Loads.AsNoTracking().Include(x => x.Stops)
                .Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled)
                .OrderBy(x => x.Reference).ToListAsync(ct);
            if (loads.Count == 0)
                loads = (await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
                    .Where(x => x.Status != LoadStatus.Cancelled).OrderBy(x => x.Reference).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, date, ct))
                .Where(x => x.Status != LoadStatus.Cancelled).OrderBy(x => x.Reference).ToList();
        }

        var now = DateTimeOffset.UtcNow;
        var payload = new PlanBaselinePayload(date, now, user, loads.Select(Snapshot).ToList());
        var key = BaselineKey(date);
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = BaselineType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "SLH plan lock"
            };
            db.StagedImports.Add(row);
        }

        row.EntityType = BaselineType;
        row.PayloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = now;
        row.ReviewedBy = user;
        row.ReviewNote = $"Operational plan locked with {payload.Runs.Count} baseline run{(payload.Runs.Count == 1 ? string.Empty : "s")}.";
        await db.SaveChangesAsync(ct);
    }

    public static async Task<PlanLockInfo?> GetAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntityType == BaselineType && x.IdempotencyKey == BaselineKey(date) && x.Status == StagingStatus.Promoted, ct);
        var payload = ParseBaseline(row);
        return payload is null ? null : new PlanLockInfo(date, payload.LockedAtUtc, payload.LockedBy, payload.Runs.Count);
    }

    public static async Task<IReadOnlyList<LoadBaseline>> BaselineAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.EntityType == BaselineType && x.IdempotencyKey == BaselineKey(date) && x.Status == StagingStatus.Promoted, ct);
        return ParseBaseline(row)?.Runs ?? [];
    }

    public static async Task RecordChangeAsync(TmsDbContext db, DateOnly date, Guid? loadId, string type, string reason, string? user, object? before, object? after, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var change = new StoredPlanChange(date, loadId, type, reason[..Math.Min(reason.Length, 1000)], user, now, before, after);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = ChangeEventType,
            IdempotencyKey = $"planchange:{date:yyyyMMdd}:{now:HHmmssfff}:{Guid.NewGuid():N}",
            PayloadJson = JsonSerializer.Serialize(change, JsonOptions),
            Source = "SLH locked-plan change",
            Status = StagingStatus.Promoted,
            ReviewedAtUtc = now,
            ReviewedBy = user,
            ReviewNote = change.Reason
        });
        await db.SaveChangesAsync(ct);
    }

    public static async Task<IReadOnlyList<PlanChangeEvent>> ChangesAsync(TmsDbContext db, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == ChangeEventType && x.Status == StagingStatus.Promoted)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        var result = new List<PlanChangeEvent>();
        foreach (var row in rows)
        {
            try
            {
                var change = JsonSerializer.Deserialize<StoredPlanChange>(row.PayloadJson, JsonOptions);
                if (change is null || change.PlanningDate < from || change.PlanningDate > to) continue;
                result.Add(new PlanChangeEvent(change.PlanningDate, change.LoadId, change.ChangeType, change.Reason, change.ChangedBy, change.ChangedAtUtc));
            }
            catch (JsonException) { }
        }
        return result.OrderBy(x => x.ChangedAtUtc).ToList();
    }

    public static LoadBaseline Snapshot(Load load) => new(load.Id, load.Reference, load.VehicleId, load.DriverId, load.TrailerId,
        load.Stops.OrderBy(x => x.Sequence).Select(x => new StopBaseline(x.OrderId, x.Sequence, x.Name, x.Address, x.PlannedArrivalUtc)).ToList());

    private static string BaselineKey(DateOnly date) => $"planbaseline:{date:yyyyMMdd}";

    private static PlanBaselinePayload? ParseBaseline(StagedImport? row)
    {
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<PlanBaselinePayload>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private sealed record PlanBaselinePayload(DateOnly PlanningDate, DateTimeOffset LockedAtUtc, string? LockedBy, List<LoadBaseline> Runs);
    private sealed record StoredPlanChange(DateOnly PlanningDate, Guid? LoadId, string ChangeType, string Reason, string? ChangedBy, DateTimeOffset ChangedAtUtc, object? Before, object? After);
}

public sealed record PlanLockInfo(DateOnly PlanningDate, DateTimeOffset LockedAtUtc, string? LockedBy, int BaselineRuns);
public sealed record LoadBaseline(Guid Id, string Reference, Guid? VehicleId, Guid? DriverId, Guid? TrailerId, IReadOnlyList<StopBaseline> Stops);
public sealed record StopBaseline(Guid? OrderId, int Sequence, string Name, string? Address, DateTimeOffset? PlannedArrivalUtc);
public sealed record PlanChangeEvent(DateOnly PlanningDate, Guid? LoadId, string ChangeType, string Reason, string? ChangedBy, DateTimeOffset ChangedAtUtc);
