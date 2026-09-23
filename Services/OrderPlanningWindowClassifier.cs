using System.Text.Json;
using System.Text.Json.Nodes;

namespace Slh.Tms.Api.Services;

public static class OrderPlanningWindowClassifier
{
    private static readonly string[] PmWords = ["pm", "afternoon", "evening", "night", "overnight", "backhaul", "backload", "back haul", "back load", "pm load", "pm route"];
    private static readonly string[] MarketWords = ["market", "covent", "spitalfields", "spit", "western international"];

    public static JsonElement Enrich(JsonElement payload)
    {
        var node = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
        var result = Classify(payload);

        node["planningWindow"] = result.PlanningWindow;
        node["suggestedPlanningWindow"] = result.PlanningWindow;
        node["runsOvernight"] = result.RunsOvernight;
        node["routeTiming"] = result.RunsOvernight ? "Overnight" : "SameDay";
        node["suggestedRouteType"] = result.SuggestedRouteType;
        node["planningWindowConfidence"] = result.Confidence;
        node["planningWindowReason"] = result.Reason;
        node["pmCandidate"] = result.PlanningWindow == "PM" || result.PlanningWindow == "Market";
        node["pmConfidence"] = result.PlanningWindow == "PM" || result.PlanningWindow == "Market" ? result.Confidence : "Low";
        node["pmReason"] = result.Reason;

        if (result.RunsOvernight) node["overnightRoute"] = true;
        if (result.RequiresPlannerReview) node["planningWindowRequiresReview"] = true;

        // Next-day delivery is not a pre-order in SLH planning. Leave genuine NWF awaiting-instruction rows alone,
        // but release route-ready orders that were only blocked because the delivery date is tomorrow.
        if (result.PlanningWindow is "PM" or "Market" && result.RunsOvernight && HasCollectionAndDelivery(payload))
        {
            if (Text(payload, "intakeStatus")?.Equals("PreOrder", StringComparison.OrdinalIgnoreCase) == true)
                node["intakeStatus"] = "ReadyForReview";
            if (BoolOrNull(payload, "plannerReady") == false)
                node["plannerReady"] = true;
        }

        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    public static PlanningWindowClassification Classify(JsonElement payload)
    {
        var customer = Normalise(Text(payload, "customerCode") ?? Text(payload, "customer") ?? Text(payload, "customerName"));
        var collectSite = Normalise(Text(payload, "collectionSite") ?? Text(payload, "collectionLocation") ?? Text(payload, "sellerName"));
        var deliverySite = Normalise(Text(payload, "deliverySite") ?? Text(payload, "deliveryLocation") ?? Text(payload, "stallNumber"));
        var haystack = Normalise(string.Join(' ', Text(payload, "sourceSubject"), Text(payload, "jobType"), Text(payload, "driverInstructions"), Text(payload, "routeTiming"), Text(payload, "marketName"), Text(payload, "sourceAttachmentName"), Text(payload, "requestedTime"), collectSite, deliverySite, customer));
        var collectionDate = DateOnlyOrNull(payload, "collectionDate");
        var deliveryDate = DateOnlyOrNull(payload, "deliveryDate");
        var collectionTime = TimeOnlyOrNull(payload, "collectionTimeFrom") ?? TimeOnlyOrNull(payload, "requestedTime");
        var reasons = new List<string>();
        var score = 0;
        var window = "AM";
        var isMarket = ContainsAny(haystack, MarketWords);

        if (isMarket)
        {
            score += 100;
            window = "Market";
            reasons.Add("Market wording/site matched");
        }

        if (collectionDate is not null && deliveryDate is not null && collectionDate.Value < deliveryDate.Value)
        {
            score += 95;
            window = isMarket ? "Market" : "PM";
            reasons.Add($"Collection date {collectionDate:dd/MM/yyyy} is before delivery date {deliveryDate:dd/MM/yyyy}");
        }

        if (ContainsAny(haystack, PmWords))
        {
            score += 80;
            if (!isMarket) window = "PM";
            reasons.Add("PM/overnight/backhaul wording matched");
        }

        if (customer.Contains("barefoot") || collectSite.Contains("barefoot") || deliverySite.Contains("barefoot"))
        {
            score += haystack.Contains(" am ") ? 25 : 70;
            if (!haystack.Contains(" am ")) window = "PM";
            reasons.Add(haystack.Contains(" am ") ? "Barefoots AM wording matched" : "Barefoots PM/customer pattern matched");
        }

        if (collectionTime is not null && collectionTime.Value.Hour >= 15)
        {
            score += 70;
            if (!isMarket) window = "PM";
            reasons.Add($"Collection/requested time {collectionTime:HH:mm} is PM");
        }
        else if (collectionTime is not null && collectionTime.Value.Hour >= 12)
        {
            score += 50;
            if (!isMarket) window = "PM";
            reasons.Add($"Collection/requested time {collectionTime:HH:mm} is afternoon");
        }

        if (deliveryDate is not null && collectionDate is null)
        {
            score += 40;
            window = isMarket ? "Market" : "PM";
            reasons.Add("Delivery date supplied but collection date is missing; treat as PM candidate rather than pre-order");
        }

        var runsOvernight = collectionDate is not null && deliveryDate is not null && collectionDate.Value < deliveryDate.Value;
        if (!runsOvernight && Text(payload, "overnightRoute")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true) runsOvernight = true;
        if (!runsOvernight && Text(payload, "routeTiming")?.Contains("overnight", StringComparison.OrdinalIgnoreCase) == true) runsOvernight = true;

        if (window == "AM" && score >= 70) window = "PM";
        var confidence = score >= 80 ? "High" : score >= 40 ? "Medium" : "Low";
        var routeType = window switch
        {
            "Market" when runsOvernight => "Market Overnight",
            "Market" => "Market",
            "PM" when runsOvernight => "PM Overnight",
            "PM" => "PM",
            _ => "AM"
        };

        if (reasons.Count == 0) reasons.Add("No PM, market or overnight pattern matched");
        return new PlanningWindowClassification(window, runsOvernight, routeType, confidence, string.Join("; ", reasons.Distinct()), confidence != "High" && window != "AM");
    }

    private static bool HasCollectionAndDelivery(JsonElement payload) => DateOnlyOrNull(payload, "collectionDate") is not null && DateOnlyOrNull(payload, "deliveryDate") is not null;
    private static bool ContainsAny(string value, IEnumerable<string> needles) => needles.Any(needle => value.Contains(NormaliseToken(needle), StringComparison.Ordinal));
    private static string Normalise(string? value) => $" {new string((value ?? string.Empty).ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())} ";
    private static string NormaliseToken(string value) => $" {new string(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray()).Trim()} ";
    private static string? Text(JsonElement payload, string name)
    {
        if (!TryGetProperty(payload, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString()!.Trim(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }
    private static bool TryGetProperty(JsonElement payload, string name, out JsonElement value)
    {
        if (payload.TryGetProperty(name, out value)) return true;
        var normal = NormaliseKey(name);
        foreach (var property in payload.EnumerateObject())
        {
            if (NormaliseKey(property.Name) == normal)
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
    private static string NormaliseKey(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static DateOnly? DateOnlyOrNull(JsonElement payload, string name) => DateOnly.TryParse(Text(payload, name), out var value) ? value : null;
    private static TimeOnly? TimeOnlyOrNull(JsonElement payload, string name) => TimeOnly.TryParse(Text(payload, name), out var value) ? value : null;
    private static bool? BoolOrNull(JsonElement payload, string name) => bool.TryParse(Text(payload, name), out var value) ? value : null;
}

public sealed record PlanningWindowClassification(string PlanningWindow, bool RunsOvernight, string SuggestedRouteType, string Confidence, string Reason, bool RequiresPlannerReview);
