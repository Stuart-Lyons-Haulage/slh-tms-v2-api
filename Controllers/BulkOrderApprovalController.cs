using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging/orders")]
[Authorize]
public sealed class BulkOrderApprovalController(TmsDbContext db, StagingService stagingService) : ControllerBase
{
    [HttpPost("bulk-approve"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> BulkApprove([FromBody] BulkApproveOrdersRequest request, CancellationToken ct)
    {
        if (request.Ids is null || request.Ids.Count == 0)
            return BadRequest(new { message = "Select at least one staged order to approve." });
        if (request.Ids.Count > 500)
            return BadRequest(new { message = "A maximum of 500 orders can be approved at once." });
        if (request.Ids.Distinct().Count() != request.Ids.Count)
            return BadRequest(new { message = "The approval request contains duplicate staging IDs." });

        var items = await db.StagedImports
            .Where(x => request.Ids.Contains(x.Id))
            .OrderBy(x => x.ReceivedAtUtc)
            .ToListAsync(ct);

        var approved = 0;
        var approvedIds = new List<Guid>();
        var skipped = new List<object>();
        var failed = new List<object>();

        foreach (var item in items)
        {
            var eligibility = CheckEligibility(item, request.Date, request.AcknowledgeReviewFlags);
            if (eligibility is not null)
            {
                skipped.Add(new { id = item.Id, reason = eligibility });
                continue;
            }

            try
            {
                if (request.AcknowledgeReviewFlags)
                    MarkPlannerApproved(item, request.Date);

                var note = request.AcknowledgeReviewFlags
                    ? $"Explicitly selected and approved from Order Control for {request.Date:yyyy-MM-dd}; visible review flags, pre-order status and planner-ready warnings were acknowledged by the planner."
                    : $"Approved from Order Control for {request.Date:yyyy-MM-dd}. Clean, planner-ready order.";
                var promoted = await stagingService.ReviewAndPromote(item.Id, true, note, User, ct);
                if (promoted.Status != StagingStatus.Promoted)
                {
                    failed.Add(new { id = item.Id, reason = $"Approval completed without promotion; final status was {promoted.Status}." });
                    continue;
                }
                approved++;
                approvedIds.Add(item.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or JsonException)
            {
                failed.Add(new { id = item.Id, reason = ex.GetBaseException().Message });
            }
        }

        var missing = request.Ids.Count - items.Count;
        return Ok(new
        {
            date = request.Date,
            requested = request.Ids.Count,
            approved,
            approvedIds,
            skipped = skipped.Count,
            failed = failed.Count,
            missing,
            skippedItems = skipped.Take(100).ToList(),
            failedItems = failed.Take(100).ToList(),
            message = approved == 0
                ? "No selected orders were approved. Blocked or incomplete work remains in Order Control."
                : $"{approved} selected order{(approved == 1 ? "" : "s")} approved into live Orders and removed from the review queue."
        });
    }

    private static string? CheckEligibility(StagedImport item, DateOnly requestedDate, bool acknowledgeReviewFlags)
    {
        if (item.EntityType != "order") return "Not an order staging record.";
        if (item.Status != StagingStatus.PendingReview) return $"Status is {item.Status}, not PendingReview.";

        try
        {
            using var document = JsonDocument.Parse(item.PayloadJson);
            var payload = document.RootElement;

            var poNumber = Text(payload, "poNumber");
            var customerCode = Text(payload, "customerCode");
            var collectionDateText = Text(payload, "collectionDate");
            if (string.IsNullOrWhiteSpace(poNumber)) return "PO/order reference is missing.";
            if (string.IsNullOrWhiteSpace(customerCode)) return "Customer code is missing.";
            if (!DateOnly.TryParse(collectionDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var collectionDate))
                return "Collection date is missing or invalid.";
            if (collectionDate != requestedDate && !IsPmOvernightCarryIn(payload, collectionDate, requestedDate))
                return $"Order belongs to {collectionDate:yyyy-MM-dd}, not the selected planning date.";

            var pallets = Int(payload, "pallets", "palletQty", "palletQuantity", "quantity");
            if (!IsBackhaul(payload) && (pallets is null or <= 0)) return "Zero or missing pallet quantity.";

            var isPreOrder = string.Equals(Text(payload, "intakeStatus"), "PreOrder", StringComparison.OrdinalIgnoreCase);
            var notPlannerReady = Bool(payload, "plannerReady") == false;
            if ((notPlannerReady || isPreOrder) && !acknowledgeReviewFlags)
                return isPreOrder ? "Pre-order awaiting customer instruction." : "Order is not planner-ready.";

            if (!acknowledgeReviewFlags)
            {
                var confidence = Text(payload, "intakeConfidence");
                if (!string.Equals(confidence, "High", StringComparison.OrdinalIgnoreCase))
                    return $"Intake confidence is {confidence ?? "not set"}; explicit planner review is required.";
                if (HasWarnings(payload)) return "Source/intake warnings require explicit planner review.";
            }

            return null;
        }
        catch (JsonException)
        {
            return "Staged payload is not valid JSON.";
        }
    }

    private static void MarkPlannerApproved(StagedImport item, DateOnly requestedDate)
    {
        var root = JsonNode.Parse(item.PayloadJson)?.AsObject() ?? new JsonObject();
        var warnings = root["intakeWarnings"] as JsonArray ?? [];
        warnings.Add($"Planner explicitly approved this order for {requestedDate:yyyy-MM-dd}; pre-order/not planner-ready status was treated as a review flag, not a blocker.");
        root["intakeWarnings"] = warnings;
        root["plannerReady"] = true;
        if (string.Equals(root["intakeStatus"]?.GetValue<string>(), "PreOrder", StringComparison.OrdinalIgnoreCase))
            root["intakeStatus"] = "PlannerApproved";
        root["plannerApprovedAtUtc"] = DateTimeOffset.UtcNow;
        root["plannerApprovalOverride"] = true;
        item.PayloadJson = root.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static bool IsPmOvernightCarryIn(JsonElement payload, DateOnly collectionDate, DateOnly requestedDate)
    {
        if (collectionDate.AddDays(1) != requestedDate) return false;
        var deliveryDateText = Text(payload, "deliveryDate");
        if (!DateOnly.TryParse(deliveryDateText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var deliveryDate) || deliveryDate != requestedDate)
            return false;

        if (Bool(payload, "overnightRoute") == true)
            return true;

        var requestedTime = Text(payload, "requestedTime");
        if (string.IsNullOrWhiteSpace(requestedTime)) return false;
        var match = Regex.Match(requestedTime, @"(?<!\d)(\d{1,2})(?::(\d{2}))?\s*(am|pm)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hour)) return false;
        if (hour is < 0 or > 23) return false;

        var meridiem = match.Groups[3].Value;
        if (meridiem.Equals("pm", StringComparison.OrdinalIgnoreCase) && hour < 12) hour += 12;
        else if (meridiem.Equals("am", StringComparison.OrdinalIgnoreCase) && hour == 12) hour = 0;
        return hour >= 12;
    }

    private static bool IsBackhaul(JsonElement payload)
    {
        var jobType = Text(payload, "jobType");
        if (string.IsNullOrWhiteSpace(jobType)) return false;
        var normal = new string(jobType.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normal.Contains("backhaul", StringComparison.Ordinal) || normal.Contains("backload", StringComparison.Ordinal);
    }

    private static bool HasWarnings(JsonElement payload)
    {
        if (!TryGetProperty(payload, "intakeWarnings", out var value)) return false;
        return value.ValueKind == JsonValueKind.Array && value.GetArrayLength() > 0;
    }

    private static bool? Bool(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static int? Int(JsonElement payload, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(payload, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
        }
        return null;
    }

    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        foreach (var property in payload.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

public sealed record BulkApproveOrdersRequest(DateOnly Date, List<Guid> Ids, bool AcknowledgeReviewFlags = false);
