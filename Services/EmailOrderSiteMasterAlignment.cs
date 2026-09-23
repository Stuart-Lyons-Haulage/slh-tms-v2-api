using System.Text.Json;
using System.Text.Json.Nodes;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Canonicalises every parsed mailbox order against Site Master before it is shown in
/// Order Review or written to staging. Source wording is retained as evidence, while
/// planner-facing fields use the canonical Site/driver wording.
/// </summary>
public static class EmailOrderSiteMasterAlignment
{
    public static async Task<EmailIntakeParseResult> AlignAsync(
        TmsDbContext db,
        EmailIntakeParseResult parsed,
        CancellationToken ct)
    {
        if (parsed.Orders.Count == 0) return parsed;

        var resolver = await PlannerSourceMasterDataResolver.CreateAsync(db, ct);
        var orders = new List<ParsedEmailOrder>(parsed.Orders.Count);
        foreach (var order in parsed.Orders)
        {
            var root = JsonNode.Parse(order.Payload.GetRawText())?.AsObject() ?? new JsonObject();
            AlignObject(root, resolver);

            if (FindNode(root, "sourceLines") is JsonArray sourceLines)
            {
                foreach (var line in sourceLines.OfType<JsonObject>())
                    AlignObject(line, resolver);
            }

            orders.Add(new ParsedEmailOrder(
                order.SourceKey,
                order.NaturalKey,
                JsonSerializer.SerializeToElement(root),
                order.Warnings));
        }

        return new EmailIntakeParseResult(orders, parsed.Warnings, parsed.IgnoredReason);
    }

    private static void AlignObject(JsonObject root, PlannerSourceMasterDataResolver resolver)
    {
        var rawCollection = FirstText(root, "collectionSite", "collectionLocation", "sellerName");
        var rawDelivery = FirstText(root, "deliverySite", "deliveryLocation", "stallNumber", "destination");
        var rawDepot = FirstText(root, "depot", "depotName", "marketName");

        var collection = resolver.Resolve(rawCollection);
        var delivery = resolver.Resolve(rawDelivery);
        var depot = resolver.Resolve(rawDepot);
        var evidence = new JsonArray();
        var marketInternalDestination = IsMarketDepot(rawDepot)
            && !IsMarketDepot(rawDelivery)
            && depot.SiteMatched
            && delivery.SiteMatched
            && depot.SiteId is not null
            && depot.SiteId == delivery.SiteId;

        if (collection.SiteMatched && !string.IsNullOrWhiteSpace(collection.SiteName))
        {
            PreserveSource(root, "sourceSellerName", rawCollection, collection.SiteName);
            root["sellerName"] = collection.SiteName;
            root["collectionSite"] = collection.SiteName;
            root["collectionSiteId"] = collection.SiteId?.ToString();
            root["collectionSiteCode"] = collection.SiteNumber;
            root["collectionGeofenceId"] = collection.GeofenceId?.ToString();
            root["collectionGeofenceName"] = collection.GeofenceName;
            evidence.Add($"Collection: {collection.EvidenceNote}");
        }

        if (delivery.SiteMatched && !string.IsNullOrWhiteSpace(delivery.SiteName))
        {
            PreserveSource(root, "sourceStallNumber", rawDelivery, delivery.SiteName);
            // Market jobs have two identities: the physical Market Site for routing/geofence
            // and the Markets Master trader/stall for the driver. Do not overwrite stallNumber
            // with the physical market name or the downstream MarketContact lookup loses the
            // trader/stall before approval.
            if (!marketInternalDestination)
                root["stallNumber"] = delivery.SiteName;
            else if (!string.IsNullOrWhiteSpace(rawDelivery))
                root["stallNumber"] = rawDelivery;
            root["deliverySite"] = delivery.SiteName;
            root["deliverySiteId"] = delivery.SiteId?.ToString();
            root["deliverySiteCode"] = delivery.SiteNumber;
            root["deliveryGeofenceId"] = delivery.GeofenceId?.ToString();
            root["deliveryGeofenceName"] = delivery.GeofenceName;
            if (!string.IsNullOrWhiteSpace(delivery.Address)) root["masterDeliveryAddress"] = delivery.Address;
            evidence.Add($"Destination: {delivery.EvidenceNote}");
        }

        if (depot.SiteMatched && !string.IsNullOrWhiteSpace(depot.SiteName))
        {
            PreserveSource(root, "sourceMarketName", rawDepot, depot.SiteName);
            root["marketName"] = depot.SiteName;
            root["depotSiteId"] = depot.SiteId?.ToString();
            root["depotSiteCode"] = depot.SiteNumber;
            root["depotResolvedFromDestination"] = false;
            evidence.Add($"Depot: {depot.EvidenceNote}");
        }
        else if (delivery.SiteMatched && !string.IsNullOrWhiteSpace(delivery.SiteName) && !string.IsNullOrWhiteSpace(rawDepot))
        {
            // A generic depot/customer label (for example "Morrisons") must not create
            // a false Site-not-recognised warning when the destination identifies the
            // physical Site unambiguously. Never use this fallback if the depot itself
            // resolved to a different canonical Site.
            PreserveSource(root, "sourceMarketName", rawDepot, delivery.SiteName);
            root["marketName"] = delivery.SiteName;
            root["depotSiteId"] = delivery.SiteId?.ToString();
            root["depotSiteCode"] = delivery.SiteNumber;
            root["depotResolvedFromDestination"] = true;
            evidence.Add($"Depot fallback from destination: {delivery.EvidenceNote}");
        }

        var anyMatched = collection.SiteMatched || delivery.SiteMatched || depot.SiteMatched;
        if (anyMatched)
        {
            root["masterDataAligned"] = true;
            root["masterDataAlignmentEvidence"] = evidence;
        }
    }

    private static bool IsMarketDepot(string? value)
    {
        var key = Normalize(value);
        return key.Contains("COVENT", StringComparison.Ordinal)
            || key.Contains("SPITALFIELDS", StringComparison.Ordinal)
            || key == "SPIT"
            || key.Contains("WESTERN", StringComparison.Ordinal)
            || key.Contains("SENDER", StringComparison.Ordinal);
    }

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static void PreserveSource(JsonObject root, string key, string? raw, string canonical)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw.Trim(), canonical.Trim(), StringComparison.OrdinalIgnoreCase)) return;
        if (FindNode(root, key) is null) root[key] = raw.Trim();
    }

    private static string? FirstText(JsonObject root, params string[] names)
    {
        foreach (var name in names)
        {
            var value = FindNode(root, name);
            if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }
        return null;
    }

    private static JsonNode? FindNode(JsonObject root, string name)
    {
        if (root.TryGetPropertyValue(name, out var direct)) return direct;
        foreach (var item in root)
            if (string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase))
                return item.Value;
        return null;
    }
}
