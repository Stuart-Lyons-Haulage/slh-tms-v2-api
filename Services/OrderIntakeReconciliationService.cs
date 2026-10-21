using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record OrderIntakeControlRow(
    Guid EvidenceId,
    string MessageId,
    string? Sender,
    string? Subject,
    DateTimeOffset ReceivedAtUtc,
    string Direction,
    bool IsOrderCandidate,
    int AttachmentCount,
    int AccountedRows,
    int ExpectedRows,
    int PendingReview,
    int Failed,
    int LinkedOrders,
    int PlannedOrders,
    string Status,
    string Detail,
    string Fingerprint);

public sealed record OrderIntakeControlSnapshot(
    DateTimeOffset GeneratedAtUtc,
    DateTimeOffset FromUtc,
    int SourcesReceived,
    int OutboundPlansExcluded,
    int OrdersCaptured,
    int AwaitingReview,
    int ParserFailures,
    int DuplicateCandidates,
    int MissingSourceRows,
    int ApprovedAwaitingPlanning,
    int PlannerGaps,
    IReadOnlyList<OrderIntakeControlRow> Records);

public sealed class OrderIntakeReconciliationService(TmsDbContext db, ILogger<OrderIntakeReconciliationService> logger)
{
    public async Task<OrderIntakeControlSnapshot> CompareAsync(DateTimeOffset fromUtc, bool persistChanges, CancellationToken ct)
    {
        var evidence = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "email-evidence" && x.ReceivedAtUtc >= fromUtc)
            .OrderByDescending(x => x.ReceivedAtUtc).ToListAsync(ct);
        var stagedOrders = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.ReceivedAtUtc >= fromUtc.AddDays(-2))
            .ToListAsync(ct);
        var stagedIds = stagedOrders.Select(x => x.Id).ToList();
        var revisions = await db.OrderRevisions.AsNoTracking()
            .Where(x => stagedIds.Contains(x.StagedImportId)).ToListAsync(ct);
        var revisionIds = revisions.Select(x => x.Id).ToList();
        var sourceLines = await db.OrderSourceLines.AsNoTracking()
            .Where(x => revisionIds.Contains(x.RevisionId)).ToListAsync(ct);
        var transportOrders = await db.TransportOrders.AsNoTracking()
            .Where(x => x.SourceStagedImportId != null && stagedIds.Contains(x.SourceStagedImportId.Value))
            .ToListAsync(ct);
        var transportIds = transportOrders.Select(x => x.Id).ToList();
        var plannedOrderIds = await db.LoadStops.AsNoTracking()
            .Where(x => x.OrderId != null && transportIds.Contains(x.OrderId.Value))
            .Select(x => x.OrderId!.Value).Distinct().ToListAsync(ct);
        var planned = plannedOrderIds.ToHashSet();

        var ordersByMessage = stagedOrders
            .Select(item => (Item: item, MessageId: Text(Parse(item.PayloadJson), "sourceEmailMessageId", "messageId")))
            .Where(x => !string.IsNullOrWhiteSpace(x.MessageId))
            .GroupBy(x => NormaliseMessageId(x.MessageId!))
            .ToDictionary(x => x.Key, x => x.Select(v => v.Item).ToList(), StringComparer.OrdinalIgnoreCase);
        var revisionsByStaged = revisions.GroupBy(x => x.StagedImportId).ToDictionary(x => x.Key, x => x.ToList());
        var linesByRevision = sourceLines.GroupBy(x => x.RevisionId).ToDictionary(x => x.Key, x => x.ToList());
        var transportByStaged = transportOrders.GroupBy(x => x.SourceStagedImportId!.Value).ToDictionary(x => x.Key, x => x.ToList());

        var rows = new List<OrderIntakeControlRow>();
        foreach (var source in evidence)
        {
            var payload = Parse(source.PayloadJson);
            var messageId = Text(payload, "messageId", "internetMessageId") ?? source.Id.ToString("N");
            var direction = Text(payload, "sourceDirection") ?? InferLegacyDirection(payload);
            var isCandidate = Bool(payload, "isOrderCandidate") ?? LegacyOrderCandidate(payload, direction);
            ordersByMessage.TryGetValue(NormaliseMessageId(messageId), out var sourceOrders);
            sourceOrders ??= [];
            var accountedRows = 0;
            foreach (var item in sourceOrders)
                if (revisionsByStaged.TryGetValue(item.Id, out var itemRevisions))
                    accountedRows += itemRevisions.Sum(revision => linesByRevision.TryGetValue(revision.Id, out var itemLines) ? Math.Max(1, itemLines.Count) : 1);
                else accountedRows++;

            var expectedRows = Int(payload, "expectedSourceRowCount", "sourceRowCount", "expectedLineCount") ?? accountedRows;
            var pending = sourceOrders.Count(x => x.Status == StagingStatus.PendingReview);
            var failed = sourceOrders.Count(x => x.Status == StagingStatus.Failed);
            var linked = sourceOrders.Sum(x => transportByStaged.TryGetValue(x.Id, out var linkedOrders) ? linkedOrders.Count : 0);
            var plannedCount = sourceOrders.Sum(x => transportByStaged.TryGetValue(x.Id, out var linkedOrders) ? linkedOrders.Count(o => planned.Contains(o.Id)) : 0);
            var activeKeys = sourceOrders.Where(x => x.Status is not StagingStatus.Archived and not StagingStatus.Rejected)
                .Select(x => StableOrderIdentity(x.PayloadJson)).Where(x => x.Length > 0).ToList();
            var duplicate = activeKeys.Count != activeKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var excluded = direction is "OutboundLyonsPlan" or "InternalOutbound" or "NonOrder" || !isCandidate;

            var (status, detail) = excluded ? ("Excluded", direction == "OutboundLyonsPlan" ? "Outbound Lyons plan retained for comparison only." : "Evidence retained; not incoming demand.")
                : failed > 0 ? ("ParserFailure", $"{failed} extracted row(s) failed validation.")
                : sourceOrders.Count == 0 ? ("ParserFailure", "Inbound instruction has source evidence but no accounted order row.")
                : duplicate ? ("DuplicateCandidate", "Two active rows have the same customer/reference/date/site/quantity identity.")
                : expectedRows > accountedRows ? ("MissingSourceRows", $"{accountedRows} of {expectedRows} source rows are accounted for.")
                : pending > 0 ? ("AwaitingReview", $"{pending} row(s) require review.")
                : linked == 0 ? ("ApprovedAwaitingPlanning", "Approved source rows have no linked live TMS order.")
                : plannedCount < linked ? ("PlannerGap", $"{linked - plannedCount} linked order(s) are not in a saved run.")
                : ("Captured", "All accounted source rows are linked through to saved runs.");

            var fingerprint = Hash($"{source.Id:N}|{status}|{accountedRows}|{expectedRows}|{pending}|{failed}|{linked}|{plannedCount}");
            rows.Add(new(source.Id, messageId, Text(payload, "senderAddress"), Text(payload, "subject"), source.ReceivedAtUtc,
                direction, isCandidate, ArrayCount(payload, "attachments"), accountedRows, expectedRows, pending, failed,
                linked, plannedCount, status, detail, fingerprint));
        }

        if (persistChanges) await PersistChangedExceptionsAsync(rows, ct);
        return BuildSnapshot(fromUtc, rows);
    }

    private async Task PersistChangedExceptionsAsync(IReadOnlyList<OrderIntakeControlRow> rows, CancellationToken ct)
    {
        var current = rows.Where(x => x.Status is not "Captured" and not "Excluded").ToDictionary(x => x.EvidenceId);
        var evidenceSet = rows.Select(x => x.EvidenceId).ToHashSet();
        var existing = (await db.StagedImports.Where(x => x.EntityType == "intake-reconciliation-exception" &&
            x.ReceivedAtUtc >= DateTimeOffset.UtcNow.AddDays(-30)).ToListAsync(ct))
            .Where(x => EvidenceIdFromKey(x.IdempotencyKey) is Guid id && evidenceSet.Contains(id)).ToList();

        foreach (var old in existing.Where(x => x.Status == StagingStatus.PendingReview))
        {
            var evidenceId = EvidenceIdFromKey(old.IdempotencyKey);
            if (evidenceId is null || !current.TryGetValue(evidenceId.Value, out var row) || !old.IdempotencyKey.EndsWith(row.Fingerprint, StringComparison.Ordinal))
            {
                old.Status = StagingStatus.Archived;
                old.ReviewedAtUtc = DateTimeOffset.UtcNow;
                old.ReviewedBy = "system:intake-reconciliation";
                old.ReviewNote = current.ContainsKey(evidenceId ?? Guid.Empty) ? "Replaced by a changed discrepancy." : "Discrepancy resolved.";
            }
        }

        var activeKeys = existing.Where(x => x.Status == StagingStatus.PendingReview).Select(x => x.IdempotencyKey).ToHashSet(StringComparer.Ordinal);
        foreach (var row in current.Values)
        {
            var key = $"reconcile:{row.EvidenceId:N}:{row.Fingerprint}";
            if (activeKeys.Contains(key)) continue;
            db.StagedImports.Add(new StagedImport
            {
                EntityType = "intake-reconciliation-exception",
                IdempotencyKey = key,
                PayloadJson = JsonSerializer.Serialize(row),
                Status = StagingStatus.PendingReview,
                Source = "Independent order-intake reconciliation",
                ReceivedAtUtc = DateTimeOffset.UtcNow,
                ReviewNote = row.Detail
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static OrderIntakeControlSnapshot BuildSnapshot(DateTimeOffset fromUtc, IReadOnlyList<OrderIntakeControlRow> rows) => new(
        DateTimeOffset.UtcNow, fromUtc, rows.Count, rows.Count(x => x.Direction == "OutboundLyonsPlan"),
        rows.Sum(x => x.AccountedRows), rows.Count(x => x.Status == "AwaitingReview"), rows.Count(x => x.Status == "ParserFailure"),
        rows.Count(x => x.Status == "DuplicateCandidate"), rows.Count(x => x.Status == "MissingSourceRows"),
        rows.Count(x => x.Status == "ApprovedAwaitingPlanning"), rows.Count(x => x.Status == "PlannerGap"), rows);

    private static JsonElement Parse(string json) { try { return JsonDocument.Parse(json).RootElement.Clone(); } catch { return JsonDocument.Parse("{}").RootElement.Clone(); } }
    private static string? Text(JsonElement root, params string[] names) { foreach (var name in names) if (Property(root, name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString(); return null; }
    private static int? Int(JsonElement root, params string[] names) { foreach (var name in names) if (Property(root, name, out var value) && value.TryGetInt32(out var number)) return number; return null; }
    private static bool? Bool(JsonElement root, string name) => Property(root, name, out var value) ? value.ValueKind is JsonValueKind.True ? true : value.ValueKind is JsonValueKind.False ? false : null : null;
    private static int ArrayCount(JsonElement root, string name) => Property(root, name, out var value) && value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
    private static bool Property(JsonElement root, string name, out JsonElement value) { if (root.ValueKind == JsonValueKind.Object) foreach (var p in root.EnumerateObject()) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = p.Value; return true; } value = default; return false; }
    private static string InferLegacyDirection(JsonElement payload) => OrderSourceDirectionClassifier.IsLyons(Text(payload, "senderAddress")) ? "InternalOutbound" : "Inbound";
    private static bool LegacyOrderCandidate(JsonElement payload, string direction)
    {
        if (direction is not ("Inbound" or "ForwardedInbound")) return false;
        var source = string.Join(' ', Text(payload, "subject"), Text(payload, "bodyText"));
        if (System.Text.RegularExpressions.Regex.IsMatch(source, @"\b(order|booking|collect(?:ion)?|deliver(?:y)?|backhaul|return load|transfer|trans[ -]?ship|pallet|quantity|qty|cancel|amend)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;
        if (!Property(payload, "attachments", out var attachments) || attachments.ValueKind != JsonValueKind.Array) return false;
        return attachments.EnumerateArray().Any(x =>
        {
            var name = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var value) ? value.GetString() : null;
            return Path.GetExtension(name ?? string.Empty).ToLowerInvariant() is ".xlsx" or ".xls" or ".xlsm" or ".csv" or ".pdf";
        });
    }
    private static string NormaliseMessageId(string value) => value.Trim().Trim('<', '>').ToUpperInvariant();
    private static string StableOrderIdentity(string json)
    {
        var p = Parse(json);
        var parts = new[] { Text(p, "customerCode"), Text(p, "poNumber", "reference"), Text(p, "collectionDate"), Text(p, "collectionSite", "sellerName"), Text(p, "deliverySite", "marketName"), Scalar(p, "pallets", "quantity") };
        return parts.Count(x => !string.IsNullOrWhiteSpace(x)) < 3 ? string.Empty : string.Join('|', parts.Select(x => (x ?? "").Trim().ToUpperInvariant()));
    }
    private static string? Scalar(JsonElement root, params string[] names)
    {
        foreach (var name in names) if (Property(root, name, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString();
        return null;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    private static Guid? EvidenceIdFromKey(string key) { var parts = key.Split(':'); return parts.Length >= 3 && Guid.TryParseExact(parts[1], "N", out var id) ? id : null; }
}

public sealed class OrderIntakeReconciliationWorker(IServiceScopeFactory scopes, ILogger<OrderIntakeReconciliationWorker> logger) : BackgroundService
{
    internal static DateTimeOffset NextRun(DateTimeOffset utcNow)
    {
        var zone = ResolveUkTimeZone();
        var local = TimeZoneInfo.ConvertTime(utcNow, zone);
        var next = new DateTime(local.Year, local.Month, local.Day, local.Hour - local.Hour % 2, 0, 0).AddHours(2);
        while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.Date.AddDays(1).AddHours(6);
        if (next.Hour < 6) next = next.Date.AddHours(6);
        if (next.Hour > 20) { next = next.Date.AddDays(1).AddHours(6); while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) next = next.Date.AddDays(1); }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), zone));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(NextRun(DateTimeOffset.UtcNow) - DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<OrderIntakeReconciliationService>();
                await service.CompareAsync(DateTimeOffset.UtcNow.AddDays(-14), true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Independent order-intake reconciliation failed; the next weekday pass will retry."); }
        }
    }

    private static TimeZoneInfo ResolveUkTimeZone() { try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); } catch { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); } }
}
