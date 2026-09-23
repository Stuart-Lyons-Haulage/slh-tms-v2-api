using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Links a later crate/tray instruction to the earlier NWF dump snapshot. The
/// dump remains the reference authority; ambiguous route/date matches stay in
/// review and are never guessed.
/// </summary>
public static class NwfCrateReferenceLinker
{
    public static async Task<int> RepairPendingAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.StagedImports
            .Where(row => row.EntityType == "order" && row.Status == StagingStatus.PendingReview)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        var parsedRows = rows.Select(row =>
        {
            try { return (Row: row, Payload: JsonNode.Parse(row.PayloadJson)?.AsObject()); }
            catch (JsonException) { return (Row: row, Payload: null); }
        }).Where(item => item.Payload is not null).ToList();

        var candidates = parsedRows
            .Select(item => TryCandidate(item.Row.Id, item.Row.PayloadJson))
            .Where(candidate => candidate is not null)
            .Cast<Candidate>()
            .ToList();
        if (candidates.Count == 0) return 0;

        var repaired = 0;
        foreach (var item in parsedRows)
        {
            var payload = item.Payload!;
            if (!NeedsLink(payload)) continue;

            var original = payload.ToJsonString();
            var request = new MailboxEmailIntakeRequest(
                MessageId: Text(payload, "sourceMessageId") ?? item.Row.Id.ToString(),
                InternetMessageId: null,
                Mailbox: null,
                SenderAddress: null,
                SenderName: null,
                Subject: Text(payload, "sourceSubject"),
                ReceivedAtUtc: item.Row.ReceivedAtUtc,
                BodyText: null,
                BodyHtml: null,
                WebLink: null,
                Attachments: null,
                ConversationId: Text(payload, "sourceConversationId"));
            var order = new ParsedEmailOrder(
                "pending-reference-repair",
                Text(payload, "intakeNaturalKey") ?? item.Row.Id.ToString(),
                JsonSerializer.SerializeToElement(payload),
                ReadWarnings(payload));
            var enriched = Enrich(order, candidates, request);
            var updated = JsonNode.Parse(enriched.Payload.GetRawText())?.AsObject();
            if (updated is null || string.Equals(original, updated.ToJsonString(), StringComparison.Ordinal)) continue;

            item.Row.PayloadJson = updated.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            item.Row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            item.Row.ReviewNote = "Pending NWF crate/tray load linked automatically to its retained dump reference.";
            repaired++;
        }

        if (repaired > 0) await db.SaveChangesAsync(ct);
        return repaired;
    }

    public static async Task<EmailIntakeParseResult> EnrichAsync(
        TmsDbContext db,
        EmailIntakeParseResult parsed,
        MailboxEmailIntakeRequest request,
        CancellationToken ct)
    {
        if (parsed.Orders.Count == 0) return parsed;

        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == "order" &&
                (row.PayloadJson.Contains("NWF crate") || row.PayloadJson.Contains("IFCO")))
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .Select(row => new { row.Id, row.PayloadJson })
            .ToListAsync(ct);

        var candidates = rows.Select(row => TryCandidate(row.Id, row.PayloadJson))
            .Where(candidate => candidate is not null)
            .Cast<Candidate>()
            .ToList();
        if (candidates.Count == 0) return parsed;

        var enriched = parsed.Orders.Select(order => Enrich(order, candidates, request)).ToList();
        return new EmailIntakeParseResult(enriched, parsed.Warnings, parsed.IgnoredReason);
    }

    private static ParsedEmailOrder Enrich(
        ParsedEmailOrder order,
        IReadOnlyList<Candidate> candidates,
        MailboxEmailIntakeRequest request)
    {
        var payload = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
        if (!NeedsLink(payload)) return order;

        var ranked = candidates
            .Select(candidate => (Candidate: candidate, Rank: MatchRank(payload, candidate.Payload, request)))
            .Where(item => item.Rank > 0)
            .ToList();
        if (ranked.Count == 0) return order;

        var bestRank = ranked.Max(item => item.Rank);
        var best = ranked.Where(item => item.Rank == bestRank).ToList();
        if (best.Count != 1)
        {
            var warning = "Multiple NWF crate/tray dump rows match this load; select the correct reference in Order Review.";
            AddWarning(payload, warning);
            return order with { Payload = JsonSerializer.SerializeToElement(payload), Warnings = MergeWarnings(order.Warnings, warning) };
        }

        var source = best[0].Candidate;
        CopyIfMissing(payload, source.Payload, "transportPo");
        CopyIfMissing(payload, source.Payload, "cratePo");
        CopyIfMissing(payload, source.Payload, "collectionReference");
        CopyIfMissing(payload, source.Payload, "loadRef");
        CopyIfMissing(payload, source.Payload, "intakeMatchKeys");

        var reference = FirstText(source.Payload, "loadReference", "loadRef", "collectionReference", "cratePo", "transportPo");
        if (!string.IsNullOrWhiteSpace(reference) && Missing(Text(payload, "loadReference")))
            payload["loadReference"] = reference;
        // Existing Order Review versions already recognise customerRef as an
        // operational driver reference, so expose the linked dump identity
        // there as well as in the more specific crate/load fields.
        if (!string.IsNullOrWhiteSpace(reference) && Missing(Text(payload, "customerRef")))
            payload["customerRef"] = reference;

        var collection = FirstText(source.Payload, "sellerName", "collectionSite", "collectionLocation", "collectionDepot");
        if (!string.IsNullOrWhiteSpace(collection) && Missing(Text(payload, "sellerName")))
            payload["sellerName"] = collection;
        if (!string.IsNullOrWhiteSpace(collection) && Missing(Text(payload, "collectionSite")))
            payload["collectionSite"] = collection;

        var notes = new List<string>();
        AddNote(notes, "Transport PO", Text(payload, "transportPo"));
        AddNote(notes, "Crate PO", Text(payload, "cratePo"));
        AddNote(notes, "Collection ref", Text(payload, "collectionReference"));
        if (notes.Count == 0) AddNote(notes, "Load ref", reference);
        var existingInstructions = RemoveResolvedInstructionSegments(Text(payload, "driverInstructions"));
        var missingNotes = notes.Where(note => existingInstructions?.Contains(note, StringComparison.OrdinalIgnoreCase) != true);
        payload["driverInstructions"] = Clip(string.Join(" · ", new[] { existingInstructions }.Concat(missingNotes).Where(value => !string.IsNullOrWhiteSpace(value))), 1000);
        payload["referenceLinkSourceStagedImportId"] = source.Id.ToString();
        payload["referenceLinkMethod"] = bestRank switch { 3 => "Reference", 2 => "Conversation", _ => "Unique date/destination/quantity" };

        RemoveResolvedWarnings(payload);
        if (IsOperationallyComplete(payload))
        {
            payload["plannerReady"] = true;
            payload["intakeStatus"] = "ReadyForReview";
            payload["intakeConfidence"] = ReadWarnings(payload).Count == 0 ? "High" : "Medium";
        }
        var remainingWarnings = ReadWarnings(payload);
        return order with { Payload = JsonSerializer.SerializeToElement(payload), Warnings = remainingWarnings };
    }

    private static int MatchRank(JsonObject target, JsonObject source, MailboxEmailIntakeRequest request)
    {
        var targetKeys = MatchKeys(target);
        var sourceKeys = MatchKeys(source);
        if (targetKeys.Overlaps(sourceKeys)) return 3;

        var sourceConversation = Text(source, "sourceConversationId");
        if (!string.IsNullOrWhiteSpace(request.ConversationId) &&
            string.Equals(request.ConversationId.Trim(), sourceConversation, StringComparison.OrdinalIgnoreCase)) return 2;

        if (!DatesOverlap(target, source)) return 0;
        if (!SameText(Destination(target), Destination(source))) return 0;
        var targetQuantity = Int(target, "pallets");
        var sourceQuantity = Int(source, "pallets");
        if (targetQuantity is null || sourceQuantity is null || targetQuantity != sourceQuantity) return 0;
        return 1;
    }

    private static bool NeedsLink(JsonObject payload)
    {
        var signal = string.Join(' ', new[] { Text(payload, "jobType"), Text(payload, "driverInstructions"), Text(payload, "sourceSubject") });
        if (!signal.Contains("crate", StringComparison.OrdinalIgnoreCase) &&
            !signal.Contains("tray", StringComparison.OrdinalIgnoreCase) &&
            !signal.Contains("IFCO", StringComparison.OrdinalIgnoreCase)) return false;
        return FirstText(payload, "loadReference", "loadRef", "collectionReference", "cratePo", "transportPo", "customerPo") is null
            || Missing(FirstText(payload, "sellerName", "collectionSite", "collectionLocation", "collectionDepot"));
    }

    private static Candidate? TryCandidate(Guid id, string json)
    {
        try
        {
            var payload = JsonNode.Parse(json)?.AsObject();
            if (payload is null || FirstText(payload, "loadReference", "loadRef", "collectionReference", "cratePo", "transportPo") is null)
                return null;
            return new Candidate(id, payload);
        }
        catch (JsonException) { return null; }
    }

    private static HashSet<string> MatchKeys(JsonObject payload)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (payload["intakeMatchKeys"] is not JsonArray values) return keys;
        foreach (var value in values)
        {
            var key = value?.ToString();
            if (!string.IsNullOrWhiteSpace(key)) keys.Add(CanonicalKey(key));
        }
        return keys;
    }

    private static string CanonicalKey(string key)
    {
        var parts = key.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length >= 3 && (parts[2].StartsWith("TRANSPORT:", StringComparison.OrdinalIgnoreCase) ||
            parts[2].StartsWith("CRATEREF:", StringComparison.OrdinalIgnoreCase) ||
            parts[2].StartsWith("CRATEPO:", StringComparison.OrdinalIgnoreCase) ||
            parts[2].StartsWith("LOAD:", StringComparison.OrdinalIgnoreCase)))
            return $"{parts[0].ToUpperInvariant()}|{parts[2].ToUpperInvariant()}";
        return key.ToUpperInvariant();
    }

    private static bool DatesOverlap(JsonObject left, JsonObject right)
    {
        var leftDates = Values(left, "collectionDate", "deliveryDate");
        var rightDates = Values(right, "collectionDate", "deliveryDate");
        return leftDates.Overlaps(rightDates);
    }

    private static HashSet<string> Values(JsonObject payload, params string[] names) =>
        names.Select(name => Text(payload, name)).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? Destination(JsonObject payload) => FirstText(payload, "stallNumber", "deliverySite", "deliveryLocation", "returningTo");

    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Normalise(left) == Normalise(right);

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static int? Int(JsonObject payload, string name)
    {
        var text = Text(payload, name);
        return int.TryParse(text, out var value) ? value : null;
    }

    private static string? FirstText(JsonObject payload, params string[] names) =>
        names.Select(name => Text(payload, name)).FirstOrDefault(value => !Missing(value));

    private static string? Text(JsonObject payload, string name)
    {
        if (!payload.TryGetPropertyValue(name, out var value) || value is null) return null;
        return value.ToString().Trim() is { Length: > 0 } text ? text : null;
    }

    private static bool Missing(string? value) => string.IsNullOrWhiteSpace(value) ||
        value.Contains("TBC", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("missing", StringComparison.OrdinalIgnoreCase);

    private static void CopyIfMissing(JsonObject target, JsonObject source, string name)
    {
        if (!Missing(Text(target, name)) || !source.TryGetPropertyValue(name, out var value) || value is null) return;
        target[name] = value.DeepClone();
    }

    private static void AddNote(List<string> notes, string label, string? value)
    {
        if (!Missing(value)) notes.Add($"{label}: {value}");
    }

    private static void AddWarning(JsonObject payload, string warning)
    {
        var warnings = payload["intakeWarnings"] as JsonArray ?? new JsonArray();
        if (!warnings.Any(item => string.Equals(item?.ToString(), warning, StringComparison.OrdinalIgnoreCase))) warnings.Add(warning);
        payload["intakeWarnings"] = warnings;
    }

    private static void RemoveResolvedWarnings(JsonObject payload)
    {
        if (payload["intakeWarnings"] is not JsonArray warnings) return;
        var remaining = warnings.Where(item => item is not null && !IsResolvedWarning(item.ToString())).Select(item => item!.DeepClone()).ToArray();
        payload["intakeWarnings"] = new JsonArray(remaining);
    }

    private static bool IsResolvedWarning(string warning) =>
        (warning.Contains("reference", StringComparison.OrdinalIgnoreCase) && (warning.Contains("missing", StringComparison.OrdinalIgnoreCase) || warning.Contains("TBC", StringComparison.OrdinalIgnoreCase))) ||
        (warning.Contains("PO", StringComparison.OrdinalIgnoreCase) && (warning.Contains("missing", StringComparison.OrdinalIgnoreCase) || warning.Contains("TBC", StringComparison.OrdinalIgnoreCase))) ||
        (warning.Contains("collection", StringComparison.OrdinalIgnoreCase) && (warning.Contains("missing", StringComparison.OrdinalIgnoreCase) || warning.Contains("not explicit", StringComparison.OrdinalIgnoreCase) || warning.Contains("TBC", StringComparison.OrdinalIgnoreCase)));

    private static bool IsOperationallyComplete(JsonObject payload) =>
        FirstText(payload, "loadReference", "loadRef", "collectionReference", "cratePo", "transportPo", "customerPo") is not null &&
        !Missing(FirstText(payload, "sellerName", "collectionSite", "collectionLocation", "collectionDepot")) &&
        !string.IsNullOrWhiteSpace(Destination(payload)) &&
        Values(payload, "collectionDate", "deliveryDate").Count > 0 &&
        Int(payload, "pallets") is > 0;

    private static string? RemoveResolvedInstructionSegments(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions)) return instructions;
        var retained = instructions.Split(" · ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => !segment.StartsWith("Intake warning:", StringComparison.OrdinalIgnoreCase) || !IsResolvedWarning(segment))
            .ToList();
        return retained.Count == 0 ? null : string.Join(" · ", retained);
    }

    private static IReadOnlyList<string> ReadWarnings(JsonObject payload) => payload["intakeWarnings"] is JsonArray warnings
        ? warnings.Where(item => item is not null && !string.IsNullOrWhiteSpace(item.ToString())).Select(item => item!.ToString()).ToList()
        : [];

    private static IReadOnlyList<string> MergeWarnings(IReadOnlyList<string> warnings, string warning) =>
        warnings.Append(warning).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string Clip(string value, int max) => value[..Math.Min(value.Length, max)];

    private sealed record Candidate(Guid Id, JsonObject Payload);
}
