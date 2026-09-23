using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Normalises NWF pallet-order rows that were staged before the PO-first reference
/// and review-eligibility rules were deployed. Only PendingReview rows are changed;
/// approved/live orders are deliberately left untouched.
/// </summary>
public static class NwfPendingOrderReferenceRepair
{
    public static async Task<int> Apply(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.StagedImports
            .Where(row => row.EntityType == "order" && row.Status == StagingStatus.PendingReview)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var row in rows)
        {
            JsonObject payload;
            try { payload = JsonNode.Parse(row.PayloadJson)?.AsObject() ?? new JsonObject(); }
            catch (JsonException) { continue; }

            if (!LooksLikeNwfPalletOrder(payload))
                continue;

            var poRef = Text(payload, "poRef") ?? Text(payload, "customerPo");
            var currentReference = Text(payload, "poNumber");
            var salesOrderId = Text(payload, "salesOrderId") ?? ExtractSalesOrder(currentReference) ?? ExtractTaggedValue(Text(payload, "driverInstructions"), "Sales order:");
            var collection = Text(payload, "collectionSite") ?? Text(payload, "sellerName");
            var depot = Text(payload, "depotId") ?? Text(payload, "depotDescription") ?? Text(payload, "stallNumber");
            var date = Text(payload, "collectionDate");
            var pallet = Text(payload, "palletName");
            var pallets = Int(payload, "pallets") ?? Int(payload, "palletQty") ?? Int(payload, "palletQuantity");

            var changed = false;

            if (!string.IsNullOrWhiteSpace(poRef))
            {
                changed |= SetText(payload, "customerPo", poRef);
                changed |= SetText(payload, "poRef", poRef);
                changed |= RemoveResolvedWarning(payload, "PO REF is missing");
            }

            if (!string.IsNullOrWhiteSpace(salesOrderId))
                changed |= SetText(payload, "salesOrderId", salesOrderId);

            var structurallyComplete = !string.IsNullOrWhiteSpace(poRef)
                && !string.IsNullOrWhiteSpace(salesOrderId)
                && !string.IsNullOrWhiteSpace(collection)
                && !string.IsNullOrWhiteSpace(depot)
                && !string.IsNullOrWhiteSpace(date)
                && pallets is > 0;

            if (structurallyComplete)
            {
                var expectedReference = Clip($"{poRef}/{salesOrderId}/{collection}/{depot}", 80);
                changed |= SetText(payload, "poNumber", expectedReference);

                var poToken = Normalise(poRef);
                var salesToken = Normalise(salesOrderId);
                var collectionToken = Normalise(collection);
                var depotToken = Normalise(depot);
                var palletToken = Normalise(pallet);
                changed |= SetText(payload, "intakeNaturalKey", $"NWFCSV|{date}|PO:{poToken}|SO:{salesToken}|{collectionToken}|{depotToken}|{palletToken}");

                var expectedMatchKeys = new JsonArray(
                    $"NWF|{date}|PO:{poToken}:{collectionToken}:{depotToken}",
                    $"NWF|{date}|SALES:{salesToken}:{collectionToken}:{depotToken}");
                if (!JsonNode.DeepEquals(payload["intakeMatchKeys"], expectedMatchKeys))
                {
                    payload["intakeMatchKeys"] = expectedMatchKeys;
                    changed = true;
                }

                if (payload["plannerReady"]?.GetValue<bool?>() != true)
                {
                    payload["plannerReady"] = true;
                    changed = true;
                }
                changed |= SetText(payload, "intakeStatus", "ReadyForReview");

                var confidence = WarningCount(payload) == 0 ? "High" : "Medium";
                changed |= SetText(payload, "intakeConfidence", confidence);

                if (string.IsNullOrWhiteSpace(Text(payload, "intakeParser")))
                    changed |= SetText(payload, "intakeParser", "NWF Pallet Order CSV (normalised)");
            }
            else
            {
                // Incomplete NWF work stays reviewable/editable but is never silently
                // elevated to a clean/high-confidence order.
                if (string.IsNullOrWhiteSpace(Text(payload, "intakeConfidence")))
                    changed |= SetText(payload, "intakeConfidence", "Medium");
            }

            if (!changed)
                continue;

            row.PayloadJson = payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            row.ReviewedAtUtc = DateTimeOffset.UtcNow;
            row.ReviewNote = structurallyComplete
                ? "Pending NWF order normalised automatically: PO-first TMS reference and review eligibility refreshed from retained source fields."
                : "Pending NWF order review metadata refreshed; incomplete source fields still require planner correction.";
            repaired++;
        }

        if (repaired > 0)
            await db.SaveChangesAsync(ct);

        return repaired + await NwfCrateReferenceLinker.RepairPendingAsync(db, ct);
    }

    private static bool LooksLikeNwfPalletOrder(JsonObject payload)
    {
        var parser = Text(payload, "intakeParser");
        if (!string.IsNullOrWhiteSpace(parser) && parser.Contains("NWF Pallet Order", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.Equals(Text(payload, "customerCode"), "NWF", StringComparison.OrdinalIgnoreCase))
            return false;

        var jobType = Text(payload, "jobType");
        var sales = Text(payload, "salesOrderId") ?? ExtractSalesOrder(Text(payload, "poNumber"));
        return (!string.IsNullOrWhiteSpace(jobType) && jobType.Contains("pallet", StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrWhiteSpace(sales);
    }

    private static string? ExtractSalesOrder(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = value.Split(new[] { '/', ' ', '·', '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith("SO", StringComparison.OrdinalIgnoreCase)
                && part.Skip(2).All(char.IsDigit));
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private static string? ExtractTaggedValue(string? value, string tag)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var index = value.IndexOf(tag, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var remainder = value[(index + tag.Length)..].Trim();
        var token = remainder.Split(new[] { ' ', '·', '|', ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token.Trim();
    }

    private static int WarningCount(JsonObject payload)
    {
        if (payload["intakeWarnings"] is not JsonArray warnings) return 0;
        return warnings.Count(item => item is not null && !string.IsNullOrWhiteSpace(item.ToString()));
    }

    private static bool RemoveResolvedWarning(JsonObject payload, string phrase)
    {
        if (payload["intakeWarnings"] is not JsonArray warnings) return false;
        var retained = warnings
            .Where(item => item is not null && !item.ToString().Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .Select(item => item!.DeepClone())
            .ToList();
        if (retained.Count == warnings.Count) return false;
        payload["intakeWarnings"] = new JsonArray(retained.ToArray());
        return true;
    }

    private static int? Int(JsonObject payload, string name)
    {
        var value = Text(payload, name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, out var number)) return number;
        return decimal.TryParse(value, out var decimalValue) ? (int)Math.Round(decimalValue, MidpointRounding.AwayFromZero) : null;
    }

    private static bool SetText(JsonObject payload, string name, string value)
    {
        if (string.Equals(Text(payload, name), value, StringComparison.Ordinal)) return false;
        payload[name] = value;
        return true;
    }

    private static string? Text(JsonObject payload, string name)
    {
        var property = payload.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
        if (property.Value is null) return null;
        var value = property.Value.ToString().Trim();
        return value.Length == 0 ? null : value;
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
