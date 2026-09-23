using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class RunOperationalStore
{
    private const string EntityType = "runoperational";
    private const string LegacyCommercialType = "loadcommercial";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task EnrichAsync(TmsDbContext db, IEnumerable<Load> loads, CancellationToken ct)
    {
        var list = loads.ToList();
        if (list.Count == 0) return;

        // Operational enrichment must never move a terminal run backwards. Resolve terminal
        // evidence per load rather than through a bulk Contains projection: these reads are also
        // used on schema-resilience/InMemory paths, and a failed bulk translation used to be
        // swallowed by the read-only fallback and leave a stale InProgress copy on the wallboard.
        foreach (var load in list)
        {
            try
            {
                var coreCompleted = await db.Loads.AsNoTracking()
                    .AnyAsync(row => row.Id == load.Id && row.Status == LoadStatus.Completed, ct);
                if (coreCompleted)
                {
                    load.Status = LoadStatus.Completed;
                    continue;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }

            // RunCompleted is the durable evidence gate required before a run may enter Completed.
            // It is stronger terminal evidence than an older Planning Register/audit copy whose
            // status still says InProgress.
            try
            {
                var completionEvidence = await db.DriverStatusLogs.AsNoTracking()
                    .AnyAsync(log => log.LoadId == load.Id && log.Status == RunCompletionPersistenceGuard.CompletionEvidenceStatus, ct);
                if (completionEvidence) load.Status = LoadStatus.Completed;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
            }
        }

        var operationalKeys = list.Select(x => Key(x.Id)).ToList();
        var legacyKeys = list.Select(x => LegacyKey(x.Id)).ToList();
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => (x.EntityType == EntityType && operationalKeys.Contains(x.IdempotencyKey)) ||
                        (x.EntityType == LegacyCommercialType && legacyKeys.Contains(x.IdempotencyKey)))
            .ToListAsync(ct);

        foreach (var load in list)
        {
            var operational = rows.FirstOrDefault(x => x.EntityType == EntityType && x.IdempotencyKey == Key(load.Id));
            if (operational is not null)
            {
                try
                {
                    var values = JsonSerializer.Deserialize<RunOperationalValues>(operational.PayloadJson, JsonOptions);
                    if (values is not null) Apply(load, values);
                }
                catch (JsonException) { }
                continue;
            }

            // Preserve only operational values from historic commercial-control rows.
            // Revenue, cost, surcharge, invoice and commercial-note data are deliberately
            // not projected back into live run models after the Phase 1 foundation cleanup.
            var legacy = rows.FirstOrDefault(x => x.EntityType == LegacyCommercialType && x.IdempotencyKey == LegacyKey(load.Id));
            if (legacy is null) continue;
            try
            {
                var values = JsonSerializer.Deserialize<LoadCommercialValues>(legacy.PayloadJson, JsonOptions);
                if (values is not null)
                    Apply(load, new RunOperationalValues(values.PalletSpacesUsed, values.TotalPalletSpaces, values.CapacityType, values.DepotSplits, values.TemperatureC, values.PlannerNotes, values.EmptyMiles));
            }
            catch (JsonException) { }
        }
    }

    public static async Task SaveAsync(TmsDbContext db, Load load, RunOperationalValues values, string? user, CancellationToken ct)
    {
        Apply(load, values);
        var key = Key(load.Id);
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = EntityType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Source = "SLH operational run control"
            };
            db.StagedImports.Add(row);
        }
        row.EntityType = EntityType;
        row.PayloadJson = JsonSerializer.Serialize(values, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = user;
        row.ReviewNote = "Operational run details updated.";

        // A planner can deliberately put only part of a single pallet order on a run.
        // Persist that run quantity into the shared pallet-allocation ledger so every
        // planning surface (Orders to Plan, Pallet Control and capacity) sees the same
        // outstanding balance. Multi-order and non-pallet runs are ignored safely by
        // PlanningAllocationStore.CandidateAsync.
        await PlanningAllocationStore.SyncSingleOrderRunAsync(db, load, user, ct);
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(Load load, RunOperationalValues values)
    {
        load.PalletSpacesUsed = values.PalletSpacesUsed;
        load.TotalPalletSpaces = values.TotalPalletSpaces;
        load.CapacityType = values.CapacityType;
        load.DepotSplits = values.DepotSplits;
        load.TemperatureC = values.TemperatureC;
        load.PlannerNotes = values.PlannerNotes;
        load.EmptyMiles = values.EmptyMiles;
    }

    private static string Key(Guid id) => $"runoperational:{id:N}";
    private static string LegacyKey(Guid id) => $"loadcommercial:{id:N}";
}

public sealed record RunOperationalValues(decimal? PalletSpacesUsed, decimal? TotalPalletSpaces, string? CapacityType, string? DepotSplits, decimal? TemperatureC, string? PlannerNotes, decimal? EmptyMiles = null);