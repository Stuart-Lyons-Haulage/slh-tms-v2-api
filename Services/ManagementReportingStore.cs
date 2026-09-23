using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class ManagementReportingStore
{
    public static async Task EnsureSchemaAsync(TmsDbContext db, CancellationToken ct)
    {
        const string sql = """
IF OBJECT_ID(N'[dbo].[EtaSnapshots]', N'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[EtaSnapshots](
        [Id] uniqueidentifier NOT NULL PRIMARY KEY,
        [LoadId] uniqueidentifier NOT NULL,
        [StopId] uniqueidentifier NOT NULL,
        [OrderId] uniqueidentifier NULL,
        [CapturedAtUtc] datetimeoffset NOT NULL,
        [EtaUtc] datetimeoffset NULL,
        [Source] nvarchar(20) NOT NULL,
        [Risk] nvarchar(40) NOT NULL,
        [TachoStatus] nvarchar(40) NOT NULL,
        [BreakMinutesIncluded] int NOT NULL,
        [TrackingUpdatedAtUtc] datetimeoffset NULL
    );
    CREATE INDEX [IX_EtaSnapshots_Stop_Captured] ON [dbo].[EtaSnapshots]([StopId],[CapturedAtUtc]);
    CREATE INDEX [IX_EtaSnapshots_Load] ON [dbo].[EtaSnapshots]([LoadId]);
END;
""";
        await db.Database.ExecuteSqlRawAsync(sql, ct);
    }

    public static async Task<int> CaptureAsync(TmsDbContext db, IReadOnlyCollection<EtaSnapshotCaptureItem> items, CancellationToken ct)
    {
        await EnsureSchemaAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var stopIds = items.Where(x => x.EtaUtc != null).Select(x => x.StopId).Distinct().ToList();
        if (stopIds.Count == 0) return 0;

        var recentCutoff = now.AddMinutes(-12);
        var recent = await db.EtaSnapshots.AsNoTracking()
            .Where(x => stopIds.Contains(x.StopId) && x.CapturedAtUtc >= recentCutoff)
            .OrderByDescending(x => x.CapturedAtUtc)
            .ToListAsync(ct);
        var latest = recent.GroupBy(x => x.StopId).ToDictionary(x => x.Key, x => x.First());
        var added = 0;

        foreach (var item in items.Where(x => x.EtaUtc != null))
        {
            if (latest.TryGetValue(item.StopId, out var previous) && now - previous.CapturedAtUtc < TimeSpan.FromMinutes(8))
                continue;

            db.EtaSnapshots.Add(new EtaSnapshot
            {
                LoadId = item.LoadId,
                StopId = item.StopId,
                OrderId = item.OrderId,
                CapturedAtUtc = now,
                EtaUtc = item.EtaUtc,
                Source = string.IsNullOrWhiteSpace(item.Source) ? "Unavailable" : item.Source.Trim(),
                Risk = string.IsNullOrWhiteSpace(item.Risk) ? "Pending" : item.Risk.Trim(),
                TachoStatus = string.IsNullOrWhiteSpace(item.TachoStatus) ? "Unavailable" : item.TachoStatus.Trim(),
                BreakMinutesIncluded = Math.Max(0, item.BreakMinutesIncluded),
                TrackingUpdatedAtUtc = item.TrackingUpdatedAtUtc
            });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return added;
    }
}

public sealed record EtaSnapshotCaptureItem(
    Guid LoadId,
    Guid StopId,
    Guid? OrderId,
    DateTimeOffset? EtaUtc,
    string Source,
    string Risk,
    string TachoStatus,
    int BreakMinutesIncluded,
    DateTimeOffset? TrackingUpdatedAtUtc);