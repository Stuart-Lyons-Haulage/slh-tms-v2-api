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

        throw new RunCompletionEvidenceException(
            "RUN_COMPLETION_EVIDENCE_REQUIRED",
            "Run completion is evidence-controlled. A load can only become Completed after a RunCompleted geofence evidence event has been recorded.");
    }
}

public sealed class RunCompletionEvidenceException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
