using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Shared production-safe planning readers. Production can contain current runs in the
/// planning register, the legacy/live Loads table, and the durable planner-plan import audit.
/// Real planning/live rows always win. If an imported run survives only in audit, it is
/// materialised back into the Planning Register so it is not merely visible to Dashboard/TV:
/// it becomes writable by Driver Dispatch, routing, allocation and downstream execution too.
/// </summary>
internal static class PlanningResilience
{
    public static async Task<List<Load>> ReadLoadsAsync(TmsDbContext db, DateOnly? date, CancellationToken ct)
    {
        var merged = new Dictionary<Guid, Load>();
        var registeredLogicalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var registered = await PlanningRegisterStore.ReadLoadsAsync(db, date, ct);
            foreach (var load in registered)
            {
                merged[load.Id] = load;
                registeredLogicalKeys.Add(LogicalRunKey(load));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var query = db.Loads.AsNoTracking().Include(x => x.Stops).AsQueryable();
            if (date is not null) query = query.Where(x => x.PlanningDate == date.Value);
            var live = await query.OrderBy(x => x.PlanningDate).ThenBy(x => x.Reference).Take(2000).ToListAsync(ct);
            var relationalFallbacks = live.Count(load => !registeredLogicalKeys.Contains(LogicalRunKey(load)));
            TmsMetrics.Shared.RecordPlanningRecovery(relationalFallbacks, "relational_loads");
            foreach (var load in live)
            {
                if (merged.TryGetValue(load.Id, out var registered))
                {
                    merged[load.Id] = PreferSameIdCopy(registered, load);
                    continue;
                }
                merged[load.Id] = load;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        // Planner imports are durably audited as one promoted plannerplanrun row per imported
        // run. If a reset removed the operational row, recover it into the Planning Register.
        // This closes the old split where Dashboard/TV could see an audit-only run but Driver
        // Dispatch could neither select nor save it. Never replace a real active row with audit.
        try
        {
            var audited = await PlannerPlanAuditProjection.ReadLoadsAsync(db, date, ct);
            var activeKeys = merged.Values
                .Where(load => load.Status != LoadStatus.Cancelled)
                .Select(LogicalRunKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var auditRecoveries = 0;
            foreach (var load in audited)
            {
                var logicalKey = LogicalRunKey(load);
                if (activeKeys.Contains(logicalKey)) continue;

                try
                {
                    await PlanningRegisterStore.SaveLoadAsync(db, load, "system:planner-audit-recovery", ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Visibility remains available even if the recovery write is temporarily
                    // unavailable. A later read will retry materialisation automatically.
                    db.ChangeTracker.Clear();
                }

                merged[load.Id] = load;
                activeKeys.Add(logicalKey);
                auditRecoveries++;
            }
            TmsMetrics.Shared.RecordPlanningRecovery(auditRecoveries, "planner_audit");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        return CollapseLogicalDuplicates(merged.Values)
            .OrderBy(x => x.PlanningDate)
            .ThenBy(x => x.Reference)
            .Take(2000)
            .ToList();
    }

    internal static bool KeepRegisteredOverLiveTombstone(Load registered, Load live) =>
        live.Status == LoadStatus.Cancelled && registered.Status != LoadStatus.Cancelled;

    /// <summary>
    /// A run can exist with the same GUID in both the Planning Register and dbo.Loads.
    /// Allocation writes deliberately target the register when that resilient copy exists,
    /// while older core rows can remain stale. Choose the stronger operational copy and merge
    /// missing detail from the other copy so a successful Driver Dispatch allocation survives
    /// the very next workbench refresh. Executed live progress still wins over a merely planned
    /// register copy.
    /// </summary>
    internal static Load PreferSameIdCopy(Load registered, Load live)
    {
        if (KeepRegisteredOverLiveTombstone(registered, live))
        {
            MergeMissingOperationalData(registered, live);
            return registered;
        }

        var registeredScore = OperationalScore(registered);
        var liveScore = OperationalScore(live);
        if (registeredScore >= liveScore)
        {
            MergeMissingOperationalData(registered, live);
            return registered;
        }

        MergeMissingOperationalData(live, registered);
        return live;
    }

    /// <summary>
    /// SQL Loads and the Planning Register can temporarily contain the same real-world run
    /// under different GUIDs after resilient import/recovery writes. Consumers must see one
    /// operational run, not one row per persistence copy. The UK planning date plus normalised
    /// run reference is the logical identity; the strongest operational copy wins and missing
    /// allocation/capacity detail is retained from its duplicate copy.
    /// </summary>
    internal static List<Load> CollapseLogicalDuplicates(IEnumerable<Load> loads)
    {
        var result = new List<Load>();
        foreach (var group in loads.GroupBy(LogicalRunKey, StringComparer.OrdinalIgnoreCase))
        {
            var candidates = group
                .OrderByDescending(OperationalScore)
                .ThenByDescending(load => load.CreatedAtUtc)
                .ThenBy(load => load.Id)
                .ToList();
            if (candidates.Count == 0) continue;

            var preferred = candidates[0];
            TmsMetrics.Shared.RecordLogicalDuplicateCollapse(candidates.Count - 1);
            foreach (var duplicate in candidates.Skip(1))
                MergeMissingOperationalData(preferred, duplicate);
            result.Add(preferred);
        }
        return result;
    }

    internal static string LogicalRunKey(Load load)
    {
        var reference = new string((load.Reference ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        var internalPrefix = $"PLAN{load.PlanningDate:yyyyMMdd}";
        if (reference.StartsWith(internalPrefix, StringComparison.OrdinalIgnoreCase))
            reference = reference[internalPrefix.Length..];
        if (string.IsNullOrWhiteSpace(reference)) reference = $"ID{load.Id:N}";
        return $"{load.PlanningDate:yyyyMMdd}|{reference}";
    }

    private static int OperationalScore(Load load)
    {
        // Operational progression must outrank data richness. A stale InProgress/Dispatched copy
        // may contain more allocation/capacity fields than the copy that has already reached
        // Completed; it must never be allowed to move a terminal run backwards. Large status
        // bands leave plenty of room for the detail score while preserving lifecycle order.
        var score = load.Status switch
        {
            LoadStatus.Completed => 50000,
            LoadStatus.InProgress => 40000,
            LoadStatus.Dispatched => 30000,
            LoadStatus.Planned => 20000,
            LoadStatus.Draft => 10000,
            LoadStatus.Cancelled => 0,
            _ => 5000
        };
        if (load.DriverId is not null) score += 300;
        if (load.VehicleId is not null) score += 300;
        if (load.TrailerId is not null) score += 200;
        if (load.PalletSpacesUsed is not null) score += 30;
        if (load.TotalPalletSpaces is not null) score += 30;
        if (!string.IsNullOrWhiteSpace(load.CapacityType)) score += 20;
        if (!string.IsNullOrWhiteSpace(load.PlannerNotes)) score += 20;
        score += Math.Min(load.Stops.Count, 20) * 5;
        return score;
    }

    private static void MergeMissingOperationalData(Load preferred, Load duplicate)
    {
        preferred.DriverId ??= duplicate.DriverId;
        preferred.VehicleId ??= duplicate.VehicleId;
        preferred.TrailerId ??= duplicate.TrailerId;
        preferred.PalletSpacesUsed ??= duplicate.PalletSpacesUsed;
        preferred.TotalPalletSpaces ??= duplicate.TotalPalletSpaces;
        preferred.CapacityType ??= duplicate.CapacityType;
        preferred.DepotSplits ??= duplicate.DepotSplits;
        preferred.TemperatureC ??= duplicate.TemperatureC;
        if (string.IsNullOrWhiteSpace(preferred.PlannerNotes) && !string.IsNullOrWhiteSpace(duplicate.PlannerNotes))
            preferred.PlannerNotes = duplicate.PlannerNotes;
    }

    public static async Task<Load?> ReadLoadAsync(TmsDbContext db, Guid id, CancellationToken ct)
    {
        try
        {
            var registered = await PlanningRegisterStore.GetLoadAsync(db, id, ct);
            if (registered is not null) return registered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var live = await db.Loads.AsNoTracking().Include(x => x.Stops).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (live is not null)
            {
                TmsMetrics.Shared.RecordPlanningRecovery(1, "relational_loads");
                return live;
            }
        }
        catch (Exception ex) when (SchemaUnavailable(ex))
        {
            db.ChangeTracker.Clear();
        }

        try
        {
            var recovered = (await PlannerPlanAuditProjection.ReadLoadsAsync(db, null, ct)).SingleOrDefault(load => load.Id == id);
            if (recovered is null) return null;
            try
            {
                await PlanningRegisterStore.SaveLoadAsync(db, recovered, "system:planner-audit-recovery", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }
            TmsMetrics.Shared.RecordPlanningRecovery(1, "planner_audit");
            return recovered;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public static bool SchemaUnavailable(Exception ex)
    {
        var message = ex.GetBaseException().Message;
        return ex is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}