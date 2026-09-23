using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging/queue")]
[Authorize]
public sealed class StagingQueueController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] StagingStatus? status,
        [FromQuery] string? entityType,
        [FromQuery] DateOnly? planningDate,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var targetStatus = status ?? StagingStatus.PendingReview;
        var normalisedEntityType = entityType?.Trim().ToLowerInvariant();
        var query = db.StagedImports.AsNoTracking().Where(item => item.Status == targetStatus);
        if (!string.IsNullOrWhiteSpace(normalisedEntityType))
            query = query.Where(item => item.EntityType == normalisedEntityType);

        var offset = (page - 1) * pageSize;

        if (planningDate is DateOnly targetPlanningDate && targetStatus == StagingStatus.PendingReview && normalisedEntityType == "order")
        {
            var targetText = targetPlanningDate.ToString("yyyy-MM-dd");
            var nextText = targetPlanningDate.AddDays(1).ToString("yyyy-MM-dd");

            // SQL narrows to records that could match the selected planning date. The +1 day candidate
            // keeps delivery-date-only PM work visible on the prior PM planning board.
            var candidates = await query
                .Where(item => item.PayloadJson.Contains(targetText) || item.PayloadJson.Contains(nextText))
                .OrderByDescending(item => item.ReceivedAtUtc)
                .ThenByDescending(item => item.Id)
                .Select(item => new StagingQueueRawRow(
                    item.Id,
                    item.EntityType,
                    item.IdempotencyKey,
                    item.PayloadJson,
                    item.Status,
                    item.Source,
                    item.ReceivedAtUtc,
                    item.ReviewedAtUtc,
                    item.ReviewedBy,
                    item.ReviewNote))
                .Take(3000)
                .ToListAsync(ct);

            var matchingRows = candidates
                .Where(item => StagingQueueProjection.MatchesPlanningDate(item.PayloadJson, targetPlanningDate))
                .ToList();
            var totalForDate = matchingRows.Count;
            var dateRows = matchingRows.Skip(offset).Take(pageSize).ToList();
            return Ok(new StagingQueuePage(
                page,
                pageSize,
                totalForDate,
                offset + dateRows.Count < totalForDate,
                dateRows.Select(StagingQueueProjection.ToSummary).ToList()));
        }

        var total = await query.CountAsync(ct);
        var rowsQuery = query;

        // Backwards-compatible safety net for older portal builds that do not yet send planningDate.
        if (targetStatus == StagingStatus.PendingReview && normalisedEntityType == "order")
        {
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var todayText = today.ToString("yyyy-MM-dd");
            var tomorrowText = today.AddDays(1).ToString("yyyy-MM-dd");
            var yesterdayText = today.AddDays(-1).ToString("yyyy-MM-dd");

            rowsQuery = rowsQuery
                .OrderByDescending(item => item.PayloadJson.Contains(todayText) || item.PayloadJson.Contains(tomorrowText) || item.PayloadJson.Contains(yesterdayText))
                .ThenByDescending(item => item.PayloadJson.Contains(tomorrowText))
                .ThenByDescending(item => item.PayloadJson.Contains(todayText))
                .ThenByDescending(item => item.ReceivedAtUtc)
                .ThenByDescending(item => item.Id);
        }
        else
        {
            rowsQuery = rowsQuery
                .OrderByDescending(item => item.ReceivedAtUtc)
                .ThenByDescending(item => item.Id);
        }

        var rows = await rowsQuery
            .Skip(offset)
            .Take(pageSize)
            .Select(item => new StagingQueueRawRow(
                item.Id,
                item.EntityType,
                item.IdempotencyKey,
                item.PayloadJson,
                item.Status,
                item.Source,
                item.ReceivedAtUtc,
                item.ReviewedAtUtc,
                item.ReviewedBy,
                item.ReviewNote))
            .ToListAsync(ct);

        var records = rows.Select(StagingQueueProjection.ToSummary).ToList();
        return Ok(new StagingQueuePage(
            page,
            pageSize,
            total,
            offset + rows.Count < total,
            records));
    }
}

internal sealed record StagingQueuePage(
    int page,
    int pageSize,
    int total,
    bool hasMore,
    IReadOnlyList<object> records);

internal sealed record StagingQueueRawRow(
    Guid Id,
    string EntityType,
    string IdempotencyKey,
    string PayloadJson,
    StagingStatus Status,
    string? Source,
    DateTimeOffset ReceivedAtUtc,
    DateTimeOffset? ReviewedAtUtc,
    string? ReviewedBy,
    string? ReviewNote);

internal static class StagingQueueProjection
{
    private static readonly string[] SummaryFields =
    [
        "poNumber", "customerPo", "customerRef", "poRef", "productPo", "cratePo", "transportPo",
        "customerCode", "collectionDate", "deliveryDate", "pallets", "sellerName", "stallNumber",
        "requestedTime", "overnightRoute", "wave", "routeTiming", "jobType", "driverInstructions",
        "plannerReady", "intakeStatus", "intakeConfidence", "intakeWarnings", "intakeParser",
        "emailRouteMatched", "emailRouteId", "emailRouteSender", "emailRouteIdentityOnly", "emailRouteRequiresReview",
        "orderIntakeRouteRuleId", "orderIntakeRouteConfidenceScore", "orderIntakeRouteMatchedDimensions",
        "orderIntakeRouteRequiresReview", "orderIntakeRouteExplanation", "orderIntakeRouteAlternatives",
        "sourceSubject", "sourceEmailSubject", "sourceMessageId", "sourceEmailMessageId",
        "sourceInternetMessageId", "sourceReceivedAtUtc", "sourceEmailReceivedAt", "sourceWebLink",
        "sourceEmailWebLink", "sourceAttachmentName"
    ];

    public static object ToSummary(StagingQueueRawRow row) => new
    {
        id = row.Id,
        entityType = row.EntityType,
        idempotencyKey = row.IdempotencyKey,
        payloadJson = BuildPayloadSummary(row.PayloadJson),
        status = row.Status.ToString(),
        source = row.Source,
        receivedAtUtc = row.ReceivedAtUtc,
        reviewedAtUtc = row.ReviewedAtUtc,
        reviewedBy = row.ReviewedBy,
        reviewNote = row.ReviewNote
    };

    internal static bool MatchesPlanningDate(string payloadJson, DateOnly planningDate)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return PlanningDates(document.RootElement).Contains(planningDate);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal static string BuildPayloadSummary(string payloadJson)
    {
        try
        {
            var source = JsonNode.Parse(payloadJson)?.AsObject();
            if (source is null) return "{}";

            var summary = new JsonObject();
            foreach (var field in SummaryFields)
            {
                var match = source.FirstOrDefault(property => string.Equals(property.Key, field, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match.Key))
                    summary[field] = match.Value?.DeepClone();
            }

            if (!summary.ContainsKey("sourceAttachmentName") &&
                source.FirstOrDefault(property => string.Equals(property.Key, "sourceAttachments", StringComparison.OrdinalIgnoreCase)).Value is JsonArray attachments)
            {
                var attachmentName = FirstNonInlineAttachmentName(attachments);
                if (!string.IsNullOrWhiteSpace(attachmentName))
                    summary["sourceAttachmentName"] = attachmentName;
            }

            ApplyConfirmedMorrisonsFollowUp(summary, source);
            ApplyPlannerReviewProjection(summary, source);
            return summary.ToJsonString();
        }
        catch (JsonException)
        {
            return "{}";
        }
        catch (InvalidOperationException)
        {
            return "{}";
        }
    }

    private static void ApplyConfirmedMorrisonsFollowUp(JsonObject summary, JsonObject source)
    {
        var customer = Text(summary, "customerCode") ?? Text(source, "customerCode", "customer");
        if (!string.Equals(customer, "MORRISONS", StringComparison.OrdinalIgnoreCase)) return;

        var haystack = string.Join(' ', new[]
        {
            Text(source, "sourceSubject", "sourceEmailSubject"),
            Text(source, "sourceBodyText", "bodyText", "driverInstructions"),
            Text(source, "customerPo", "poNumber"),
            Text(summary, "customerPo", "poNumber")
        });

        if (!haystack.Contains("Morrisons", StringComparison.OrdinalIgnoreCase)) return;
        if (!haystack.Contains("Aldi", StringComparison.OrdinalIgnoreCase)) return;
        if (!haystack.Contains("follow", StringComparison.OrdinalIgnoreCase)) return;

        summary["plannerReady"] = true;
        summary["intakeStatus"] = "ReadyForReview";

        var warnings = summary["intakeWarnings"] as JsonArray ?? [];
        if (!warnings.Any(node => TextValue(node).Contains("Aldi", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Aldi follow-up is pending; confirmed Morrisons order remains planner-actionable.");
        summary["intakeWarnings"] = warnings;
    }

    /// <summary>
    /// A sender/customer or route mapping review is a planner confirmation flag, not a
    /// customer pre-order. Keep genuine PreOrder rows blocked, but make mapping-review
    /// rows selectable in Order Review so the existing explicit bulk-approval path can
    /// acknowledge the flag and promote them safely.
    /// </summary>
    private static void ApplyPlannerReviewProjection(JsonObject summary, JsonObject source)
    {
        var intakeStatus = Text(summary, "intakeStatus") ?? Text(source, "intakeStatus");
        if (string.Equals(intakeStatus, "PreOrder", StringComparison.OrdinalIgnoreCase)) return;

        var plannerReady = Bool(summary, "plannerReady") ?? Bool(source, "plannerReady");
        if (plannerReady != false) return;

        var senderReview = Bool(summary, "emailRouteRequiresReview") ?? Bool(source, "emailRouteRequiresReview");
        var routeReview = Bool(summary, "orderIntakeRouteRequiresReview") ?? Bool(source, "orderIntakeRouteRequiresReview");
        if (senderReview != true && routeReview != true) return;

        summary["plannerReady"] = true;
        // The existing portal already treats this field as an amber, explicitly-reviewable
        // state. Reuse it so the row is selectable but never included in "Select all clean".
        summary["orderIntakeRouteRequiresReview"] = true;
        if (string.IsNullOrWhiteSpace(Text(summary, "intakeStatus")))
            summary["intakeStatus"] = "Review";
    }

    private static string? FirstNonInlineAttachmentName(JsonArray attachments)
    {
        foreach (var item in attachments.OfType<JsonObject>())
        {
            if (Bool(item, "isInline") == true) continue;
            var name = Text(item, "name");
            if (!string.IsNullOrWhiteSpace(name)) return name;
        }
        return null;
    }

    private static IReadOnlyCollection<DateOnly> PlanningDates(JsonElement root)
    {
        var dates = new HashSet<DateOnly>();
        var collection = Date(root, "collectionDate");
        var delivery = Date(root, "deliveryDate");
        var window = InferredPlanningWindow(root);
        var runsOvernight = RunsOvernight(root, collection, delivery);

        if (collection is DateOnly collectionDate && delivery is DateOnly deliveryDate && collectionDate < deliveryDate)
            return [collectionDate];
        if ((window == "PM" || window == "Market") && runsOvernight && collection is DateOnly overnightCollection)
            return [overnightCollection];
        if ((window == "PM" || window == "Market") && collection is null && delivery is DateOnly deliveryOnly)
            return [deliveryOnly.AddDays(-1), deliveryOnly];

        if (collection is DateOnly c) dates.Add(c);
        if (delivery is DateOnly d) dates.Add(d);
        return dates;
    }

    private static bool RunsOvernight(JsonElement root, DateOnly? collection, DateOnly? delivery) =>
        Bool(root, "runsOvernight", "overnightRoute") == true ||
        Normalise(Text(root, "routeTiming", "suggestedRouteType")).Contains("OVERNIGHT", StringComparison.OrdinalIgnoreCase) ||
        collection is DateOnly c && delivery is DateOnly d && c < d;

    private static string InferredPlanningWindow(JsonElement root)
    {
        var explicitWindow = Text(root, "suggestedPlanningWindow", "planningWindow");
        if (string.Equals(explicitWindow, "AM", StringComparison.OrdinalIgnoreCase)) return "AM";
        if (string.Equals(explicitWindow, "PM", StringComparison.OrdinalIgnoreCase)) return "PM";
        if (string.Equals(explicitWindow, "Market", StringComparison.OrdinalIgnoreCase)) return "Market";
        if (string.Equals(explicitWindow, "Transfer", StringComparison.OrdinalIgnoreCase)) return "Transfer";

        var haystack = Normalise(string.Join(' ', new[]
        {
            Text(root, "customerCode", "customer"),
            Text(root, "sellerName", "stallNumber", "marketName"),
            Text(root, "jobType", "driverInstructions"),
            Text(root, "sourceSubject", "sourceAttachmentName"),
            Text(root, "requestedTime")
        }));

        if (ContainsAny(haystack, "MARKET", "COVENT", "SPITALFIELDS", "SPIT", "WESTERNINTERNATIONAL")) return "Market";
        if (haystack.Contains("TRANSFER", StringComparison.OrdinalIgnoreCase)) return "Transfer";
        if (ContainsAny(haystack, "PM", "AFTERNOON", "EVENING", "NIGHT", "OVERNIGHT", "BACKHAUL", "BACKLOAD")) return "PM";
        if (haystack.Contains("BAREFOOTS", StringComparison.OrdinalIgnoreCase) && !haystack.Contains("AM", StringComparison.OrdinalIgnoreCase)) return "PM";
        return "AM";
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string? Text(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                return property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString()?.Trim(),
                    JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.Value.ToString(),
                    _ => null
                };
            }
        }
        return null;
    }

    private static string? Text(JsonObject root, params string[] names)
    {
        foreach (var name in names)
        {
            var match = root.FirstOrDefault(property => string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(match.Key) || match.Value is null) continue;
            var value = TextValue(match.Value);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private static string TextValue(JsonNode? node)
    {
        if (node is null) return string.Empty;
        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.String => node.GetValue<string>()?.Trim() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => node.ToJsonString(),
                _ => string.Empty
            };
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static DateOnly? Date(JsonElement root, string name) =>
        DateOnly.TryParse(Text(root, name), out var value) ? value : null;

    private static bool? Bool(JsonElement root, params string[] names) =>
        bool.TryParse(Text(root, names), out var value) ? value : null;

    private static bool? Bool(JsonObject root, params string[] names) =>
        bool.TryParse(Text(root, names), out var value) ? value : null;

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
