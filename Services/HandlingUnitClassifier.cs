using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Labels each staged order with what is actually being moved: pallets, trolleys, trays
/// or crates. The planning screens already colour and count these separately (via
/// <c>palletType</c> on the order and its source lines); the parsers just were not
/// saying which one they had, so trolleys and tray collections looked like pallets.
///
/// Trays and crates are only ever collected southbound, on the way back from a
/// delivery, so they are also grouped as "Southbound collections" for the planners.
/// </summary>
public static class HandlingUnitClassifier
{
    public const string SouthboundGroup = "Southbound collections";

    // "26 pallets of rental trays", "3 pallets of crates": pallet spaces of trays/crates.
    private static readonly Regex PalletsOfRegex = new(
        @"\b(?<qty>\d{1,4})\s*(?:pallets?|plts?)\s+of\s+(?:[a-z]+\s+){0,2}?(?<unit>trays?|crates?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex QuantityUnitRegex = new(
        @"\b(?<qty>\d{1,4})\s*(?<unit>euro\s*pool\s+trays?|pallets?|plts?|trolleys?|dollies|dolly|trays?|crates?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Each quantity stated with a unit, in order, one entry per unit type (first wins).</summary>
    public static IReadOnlyList<(string Unit, int Quantity)> UnitQuantities(string? text)
    {
        var source = text ?? string.Empty;
        var result = new List<(string Unit, int Quantity)>();
        var consumed = new List<(int Start, int End)>();

        foreach (Match match in PalletsOfRegex.Matches(source))
        {
            Add(result, Canonical(match.Groups["unit"].Value), match.Groups["qty"].Value);
            consumed.Add((match.Index, match.Index + match.Length));
        }
        foreach (Match match in QuantityUnitRegex.Matches(source))
        {
            if (consumed.Any(range => match.Index >= range.Start && match.Index < range.End)) continue;
            Add(result, Canonical(match.Groups["unit"].Value), match.Groups["qty"].Value);
        }
        return result;

        static void Add(List<(string Unit, int Quantity)> list, string unit, string raw)
        {
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0) return;
            if (list.Any(item => item.Unit == unit)) return;
            list.Add((unit, quantity));
        }
    }

    public static string Canonical(string? unit)
    {
        var value = (unit ?? string.Empty).ToLowerInvariant();
        if (value.Contains("tray")) return "Trays";
        if (value.Contains("crate") || value.Contains("ifco")) return "Crates";
        if (value.Contains("trolley") || value.Contains("dolly") || value.Contains("dollies")) return "Trolleys";
        return "Pallets";
    }

    /// <summary>Unit implied by how the order was identified (job type / parser profile).</summary>
    public static string? UnitFromOrderDescription(JsonElement payload)
    {
        var description = string.Join(" ",
            Text(payload, "palletType"), Text(payload, "unitType"), Text(payload, "handlingUnitType"),
            Text(payload, "jobType"), Text(payload, "intakeProfile")).ToLowerInvariant();
        if (description.Contains("tray") || description.Contains("euro pool") || description.Contains("europool")) return "Trays";
        if (description.Contains("crate") || description.Contains("ifco")) return "Crates";
        if (description.Contains("trolley") || description.Contains("dolly") || description.Contains("dollies")) return "Trolleys";
        return null;
    }

    // A count of the items themselves: "8,316 trays", "110 Potato Shipper", "600 IFCO crates".
    // "26 pallets of trays" is a pallet count, not a tray count, so it does not match.
    private static readonly Regex ItemCountRegex = new(
        @"(?<![\d,.])(?<qty>\d{1,3}(?:,\d{3})+|\d{1,6})\s*(?:x\s*)?(?<unit>(?:(?:euro\s*pool|green|black|ifco|potato|rental|dirty|empty)\s+){0,2}(?:trays?|crates?|shippers?))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// How many trays / crates / shippers the order is for, when known - kept apart from the
    /// pallet (space) count, which customers often don't give for these jobs.
    /// </summary>
    public static (int Count, string Unit)? UnitCount(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        var pallets = Int(payload, "pallets");
        var declared = Text(payload, "handlingUnitType") ?? Text(payload, "unitType");
        var declaredUnit = UnitName(declared);
        foreach (var field in new[] { "unitCount", "handlingUnitQuantity" })
        {
            var count = Int(payload, field);
            if (count is > 0 && declaredUnit is not null && !(field == "handlingUnitQuantity" && count == pallets))
                return (count.Value, Text(payload, "unitCountType") ?? declaredUnit);
        }
        foreach (var (field, unit) in new[] { ("trays", "Trays"), ("crates", "Crates") })
            if (Int(payload, field) is > 0 and var explicitCount) return (explicitCount.Value, unit);
        var text = string.Join("\n", Text(payload, "driverInstructions"), Text(payload, "sourceBlockText"), Text(payload, "sourceBodyText"), Text(payload, "notes"));
        var match = ItemCountRegex.Match(text);
        if (!match.Success) return null;
        var qty = int.Parse(match.Groups["qty"].Value.Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return qty > 0 ? (qty, UnitName(match.Groups["unit"].Value) ?? "Units") : null;
    }

    private static string? UnitName(string? value)
    {
        var lower = (value ?? string.Empty).ToLowerInvariant();
        if (lower.Contains("shipper")) return "Shippers";
        if (lower.Contains("tray") || lower.Contains("euro pool") || lower.Contains("europool")) return "Trays";
        if (lower.Contains("crate") || lower.Contains("ifco")) return "Crates";
        return null;
    }

    public static bool IsSouthboundCollectionUnit(string? unit) => unit is "Trays" or "Crates";

    /// <summary>Label every order in a parse result. Pallet orders are left as they are.</summary>
    public static EmailIntakeParseResult Apply(EmailIntakeParseResult parsed)
    {
        if (parsed.Orders.Count == 0) return parsed;
        var orders = parsed.Orders.Select(Apply).ToList();
        return parsed with { Orders = orders };
    }

    public static ParsedEmailOrder Apply(ParsedEmailOrder order)
    {
        var unit = UnitFromOrderDescription(order.Payload);
        var hasLines = order.Payload.TryGetProperty("sourceLines", out var lines) && lines.ValueKind == JsonValueKind.Array && lines.GetArrayLength() > 1;
        if (unit is null && !hasLines && UnitCount(order.Payload) is null) return order;

        var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject();
        if (root is null) return order;

        if (unit is not null)
        {
            root["palletType"] ??= unit;
            root["unitType"] ??= unit;
            root["handlingUnitType"] ??= unit;
            var quantity = Int(order.Payload, "handlingUnitQuantity") ?? Int(order.Payload, "pallets");
            if (unit == "Trolleys" && quantity is > 0) root["trolleys"] ??= quantity;
        }

        if (UnitCount(order.Payload) is { } items && Int(order.Payload, "unitCount") is null)
        {
            root["unitCount"] = items.Count;
            root["unitCountType"] = items.Unit;
        }

        var southbound = IsSouthboundCollectionUnit(unit) ||
                         (hasLines && lines.EnumerateArray().Any(line => IsSouthboundCollectionUnit(Text(line, "palletType"))));
        if (southbound)
        {
            root["southboundCollection"] = true;
            root["routeLeg"] ??= "Southbound";
            if (string.IsNullOrWhiteSpace(Text(order.Payload, "planningGroup"))) root["planningGroup"] = SouthboundGroup;
        }

        return order with { Payload = JsonSerializer.SerializeToElement(root) };
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Int(JsonElement payload, string name)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }
}
