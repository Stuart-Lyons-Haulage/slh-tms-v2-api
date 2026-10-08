using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class RunCompletionPersistenceGuard
{
    public const string CompletionEvidenceStatus = "RunCompleted";

    public static async Task EnsureCompletionEvidenceAsync(TmsDbContext db, Guid loadId, CancellationToken ct)
    {
        var pendingEvidence = db.ChangeTracker.Entries<DriverStatusLog>()
            .Any(entry => entry.State is EntityState.Added or EntityState.Unchanged
                && entry.Entity.LoadId == loadId
                && string.Equals(entry.Entity.Status, CompletionEvidenceStatus, StringComparison.Ordinal));
        if (pendingEvidence) return;

        var persistedEvidence = await db.DriverStatusLogs.AsNoTracking()
            .AnyAsync(log => log.LoadId == loadId && log.Status == CompletionEvidenceStatus, ct);
        if (persistedEvidence) return;

        var load = await db.Loads.AsNoTracking().Include(item => item.Stops)
            .SingleOrDefaultAsync(item => item.Id == loadId, ct)
            ?? await PlanningRegisterStore.GetLoadAsync(db, loadId, ct);
        if (load is not null && load.Stops.Count > 0)
        {
            var stopIds = load.Stops.Select(stop => stop.Id).ToList();
            var snapshots = await db.IntegrationMappings.AsNoTracking()
                .Where(item => item.Active && item.Provider == "Samsara" && item.TmsEntityType == "LoadStop" && stopIds.Contains(item.TmsEntityId))
                .ToListAsync(ct);
            var departed = snapshots
                .Select(item => (item.TmsEntityId, Progress: SamsaraRouteProgressService.ReadProgress(item.Notes)))
                .Where(item => item.Progress?.DepartureTime is not null)
                .Select(item => item.TmsEntityId)
                .ToHashSet();
            if (stopIds.All(departed.Contains)) return;
        }

        throw new RunCompletionEvidenceException(
            "RUN_COMPLETION_EVIDENCE_REQUIRED",
            "Run completion is evidence-controlled. A load can only become Completed after Samsara has reported departure from every planned stop.");
    }
}

public sealed class RunCompletionEvidenceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
