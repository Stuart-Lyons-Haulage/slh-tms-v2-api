using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed class OrderCompletenessService(TmsDbContext db)
{
    public async Task RefreshReferenceIssuesAsync(CancellationToken ct)
    {
        var movements = await db.OrderMovements.ToListAsync(ct);
        var revisionIds = movements.Where(x => x.CurrentRevisionId is not null).Select(x => x.CurrentRevisionId!.Value).ToList();
        var revisions = await db.OrderRevisions.AsNoTracking().Where(x => revisionIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var lines = await db.OrderSourceLines.AsNoTracking().Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
        var orders = await db.TransportOrders.AsNoTracking().Where(x => x.SourceMovementId != null).ToListAsync(ct);
        var existing = await db.OrderReferenceIssues.Where(x => x.Status == ReferenceIssueStatus.Open).ToListAsync(ct);

        foreach (var movement in movements)
        {
            revisions.TryGetValue(movement.CurrentRevisionId ?? Guid.Empty, out var revision);
            List<OrderSourceLine> currentLines = revision is null ? [] : lines.Where(x => x.RevisionId == revision.Id).ToList();
            var order = orders.FirstOrDefault(x => x.SourceMovementId == movement.Id);
            var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (MissingPo(revision?.PayloadJson)) missing.Add("PO");
            if (currentLines.Count > 0 && currentLines.All(x => string.IsNullOrWhiteSpace(x.LoadReference))) missing.Add("LoadReference");
            foreach (var type in missing)
            {
                if (!existing.Any(x => x.MovementId == movement.Id && x.ReferenceType == type))
                {
                    var issue = new OrderReferenceIssue { MovementId = movement.Id, TransportOrderId = order?.Id, ReferenceType = type };
                    db.OrderReferenceIssues.Add(issue);
                    existing.Add(issue);
                }
            }
            foreach (var issue in existing.Where(x => x.MovementId == movement.Id && !missing.Contains(x.ReferenceType)))
            {
                issue.Status = ReferenceIssueStatus.Resolved;
                issue.ResolvedAtUtc = DateTimeOffset.UtcNow;
                issue.ResolvedBy = "Reference supplied by intake update";
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<object> ReadCompletenessAsync(CancellationToken ct)
    {
        await RefreshReferenceIssuesAsync(ct);
        var issues = await db.OrderReferenceIssues.AsNoTracking().Where(x => x.Status == ReferenceIssueStatus.Open).ToListAsync(ct);
        return new { critical = 0, warnings = issues.Count, information = 0, issues = issues.Select(x => new { code = $"missing_{x.ReferenceType.ToLowerInvariant()}", severity = "Warning", x.MovementId, x.TransportOrderId, x.DetectedAtUtc }) };
    }

    private static bool MissingPo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return true;
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var name in new[] { "poNumber", "customerPo", "transportPo", "purchaseOrder", "productPo", "cratePo" })
                if (document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return false;
            return true;
        }
        catch (JsonException) { return true; }
    }
}
