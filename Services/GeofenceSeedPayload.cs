using System.Text.Json.Nodes;

namespace Slh.Tms.Api.Services;

internal static class GeofenceSeedPayload
{
    // Canonical operational geofence payload rebuilt from the reviewed DOT/Falcon
    // category exports and master-data alignment completed on 25 August 2026.
    internal const int SupplementalFenceCount = 0;
    internal static int ExpectedFenceCount => OperationalGeofencePayload.ExpectedFenceCount + SupplementalFenceCount;

    private static readonly Lazy<string> Payload = new(Build);

    internal static string Json => Payload.Value;

    private static string Build()
    {
        return OperationalGeofencePayload.Json;
    }

    private static JsonObject Fence(string name, JsonObject[] points) => new()
    {
        ["name"] = name,
        ["category"] = "SLH supplemental",
        ["category_max_wait_time"] = null,
        ["max_wait_time"] = null,
        ["pending_entry_minutes"] = 0,
        ["pending_exit_minutes"] = 0,
        ["site_no"] = null,
        ["points"] = new JsonArray(points.Cast<JsonNode?>().ToArray())
    };

    private static JsonObject Point(double longitude, double latitude) => new()
    {
        ["longitude"] = longitude,
        ["latitude"] = latitude
    };

    private static void AddIfMissing(JsonArray root, JsonObject fence)
    {
        var name = fence["name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name)) return;
        var key = Normalize(name);
        var exists = root.OfType<JsonObject>()
            .Any(item => Normalize(item["name"]?.GetValue<string>()) == key);
        if (!exists) root.Add(fence);
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
