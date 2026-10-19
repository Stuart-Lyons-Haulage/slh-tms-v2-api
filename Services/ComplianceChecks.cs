using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// What the office did about a vehicle-check gap: chased the driver, or saw the paper
/// walkround check. Stored append-only so the day's check record is auditable.
/// </summary>
public static class ComplianceChecks
{
    public const string EntityType = "compliancecheck";
    public const string PaperCheck = "PaperCheck";
    public const string Chased = "Chased";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed record Action(DateOnly Date, string AssetType, Guid AssetId, Guid DriverId, string Kind, string? Note, string? RecordedBy, DateTimeOffset RecordedAtUtc)
    {
        public string Key => KeyFor(AssetType, AssetId, DriverId);
    }

    public sealed record Request(DateOnly Date, string AssetType, Guid AssetId, Guid DriverId, string Kind, string? Note);

    public static string KeyFor(string assetType, Guid assetId, Guid driverId) => $"{assetType}|{assetId:N}|{driverId:N}".ToLowerInvariant();

    public static string NormaliseKind(string? kind) =>
        string.Equals(kind, PaperCheck, StringComparison.OrdinalIgnoreCase) ? PaperCheck : Chased;

    public static async Task<Action> RecordAsync(TmsDbContext db, Request request, string? user, CancellationToken ct)
    {
        var action = new Action(request.Date, request.AssetType is "Trailer" ? "Trailer" : "Vehicle", request.AssetId, request.DriverId,
            NormaliseKind(request.Kind), string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(), user, DateTimeOffset.UtcNow);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"compliancecheck:{Guid.NewGuid():N}",
            Status = StagingStatus.Promoted,
            Source = "Vehicle checks",
            PayloadJson = JsonSerializer.Serialize(action, JsonOptions),
            ReviewedBy = user,
            ReviewedAtUtc = action.RecordedAtUtc
        });
        await db.SaveChangesAsync(ct);
        return action;
    }

    /// <summary>Latest action of each kind per asset/driver for the date.</summary>
    public static async Task<Dictionary<string, List<Action>>> ForDateAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-60);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == EntityType && x.ReceivedAtUtc >= since)
            .OrderBy(x => x.ReceivedAtUtc)
            .Select(x => x.PayloadJson)
            .Take(5000)
            .ToListAsync(ct);
        var result = new Dictionary<string, List<Action>>(StringComparer.Ordinal);
        foreach (var json in rows)
        {
            try
            {
                var action = JsonSerializer.Deserialize<Action>(json, JsonOptions);
                if (action is null || action.Date != date) continue;
                if (!result.TryGetValue(action.Key, out var list)) result[action.Key] = list = [];
                list.Add(action);
            }
            catch (JsonException) { }
        }
        return result;
    }
}
