using System.Text.Json.Nodes;

namespace Slh.Tms.Api.Services;

internal static class StagedOrderPayloadAmendment
{
    public static JsonObject Apply(JsonObject payload)
    {
        var collection = FirstText(payload, "collectionSiteName", "collectionSite", "collectionLocation", "sellerName");
        var delivery = FirstText(payload, "deliverySiteName", "deliverySite", "deliveryLocation", "stallNumber", "destination");
        var temperature = FirstText(payload, "temperature", "temperatureRequirement", "temperatureC", "temp");
        var orderType = FirstText(payload, "orderType", "jobType", "palletType");
        var notes = FirstText(payload, "notes", "orderNotes", "driverInstructions");

        if (!string.IsNullOrWhiteSpace(collection))
        {
            payload["collectionSite"] = collection;
            payload["collectionLocation"] = collection;
            payload["sellerName"] = collection;
        }

        if (!string.IsNullOrWhiteSpace(delivery))
        {
            payload["deliverySite"] = delivery;
            payload["deliveryLocation"] = delivery;
            payload["stallNumber"] = delivery;
        }

        if (!string.IsNullOrWhiteSpace(temperature))
            payload["temperatureRequirement"] = temperature;
        if (!string.IsNullOrWhiteSpace(orderType))
            payload["jobType"] = orderType;
        if (!string.IsNullOrWhiteSpace(notes))
            payload["driverInstructions"] = notes;

        if (payload["sourceLines"] is JsonArray sourceLines && sourceLines.Count == 1 && sourceLines[0] is JsonObject sourceLine)
        {
            Apply(sourceLine);
            if (!string.IsNullOrWhiteSpace(collection)) sourceLine["collectionSite"] = collection;
            if (!string.IsNullOrWhiteSpace(delivery)) sourceLine["deliverySite"] = delivery;
            if (!string.IsNullOrWhiteSpace(temperature)) sourceLine["temperatureRequirement"] = temperature;
            if (!string.IsNullOrWhiteSpace(orderType)) sourceLine["jobType"] = orderType;
            if (!string.IsNullOrWhiteSpace(notes)) sourceLine["driverInstructions"] = notes;
        }

        return payload;
    }

    private static string? FirstText(JsonObject payload, params string[] names)
    {
        foreach (var name in names)
        {
            var property = payload.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
            if (property.Value is JsonValue number && number.TryGetValue<int>(out var numeric))
                return numeric.ToString();
        }

        return null;
    }
}
