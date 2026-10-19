using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Orders waiting for review that are exact copies of one already approved (same reference,
/// collection date, collection site, destination and quantity) are closed as duplicates, so
/// the review queue only holds work that still needs a decision. Anything that differs in
/// any of those (an amendment) is left for review.
/// </summary>
public static class PendingDuplicateSweep
{
    public const string Note = "Closed automatically: an identical order (same reference, date, route and quantity) is already approved.";

    public static async Task<int> RunAsync(TmsDbContext db, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-21);
        var approved = (await db.StagedImports.AsNoTracking()
                .Where(item => item.EntityType == "order" && item.ReceivedAtUtc >= since &&
                               (item.Status == StagingStatus.Approved || item.Status == StagingStatus.Promoted))
                .Select(item => item.PayloadJson)
                .ToListAsync(ct))
            .Select(Fingerprint).Where(key => key is not null).ToHashSet(StringComparer.Ordinal);
        if (approved.Count == 0) return 0;

        var pending = await db.StagedImports
            .Where(item => item.EntityType == "order" && item.Status == StagingStatus.PendingReview && item.ReceivedAtUtc >= since)
            .ToListAsync(ct);
        var closed = 0;
        foreach (var item in pending)
        {
            var key = Fingerprint(item.PayloadJson);
            if (key is null || !approved.Contains(key)) continue;
            var previous = item.Status;
            item.Status = StagingStatus.Archived;
            item.IdempotencyKey = $"archived-duplicate:{item.Id:N}";
            item.ReviewedAtUtc = DateTimeOffset.UtcNow;
            item.ReviewedBy = "Duplicate check";
            item.ReviewNote = Note;
            db.StagedImportEvents.Add(StagingAudit.Create(item, "DuplicateClosed", previous, Note, "Duplicate check"));
            closed++;
        }
        if (closed > 0) await db.SaveChangesAsync(ct);
        return closed;
    }

    /// <summary>Reference|collection date|collection|destination|pallets, or null when any part is missing.</summary>
    internal static string? Fingerprint(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            var reference = Text(root, "poNumber") ?? Text(root, "customerPo");
            var date = Text(root, "collectionDate");
            var from = Text(root, "collectionSite") ?? Text(root, "sellerName");
            var to = Text(root, "deliverySite") ?? Text(root, "stallNumber");
            var pallets = root.TryGetProperty("pallets", out var p) ? p.ToString().Trim() : null;
            if (reference is null || date is null || from is null || to is null || string.IsNullOrWhiteSpace(pallets) || pallets == "0") return null;
            return string.Join('|', Key(reference), date, Key(from), Key(to), pallets);
        }
        catch (JsonException) { return null; }
    }

    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number && !string.IsNullOrWhiteSpace(value.ToString()) ? value.ToString().Trim() : null;
}
