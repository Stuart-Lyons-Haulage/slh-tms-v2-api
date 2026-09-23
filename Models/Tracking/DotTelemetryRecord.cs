using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Models.Tracking;

public sealed class RoadTechTelemetryItem
{
    public string VehCode { get; init; } = string.Empty;
    public long VehRtid { get; init; }
    public bool? Ign { get; init; }
    public bool? Moving { get; init; }
    public JsonElement? DataGps { get; init; }
    public JsonElement? DataCan { get; init; }
    public JsonElement? DataGaz { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record DotTelemetryRecord(
    string ProviderEventId,
    string VehicleIdentifier,
    DateTimeOffset EventTimeUtc,
    decimal? Latitude,
    decimal? Longitude,
    decimal? SpeedKph,
    bool? IgnitionOn,
    bool? IsMoving,
    string? Status,
    string RawPayload,
    string? DriverName = null,
    string? DriverCardNumber = null)
{
    public static DotTelemetryRecord FromProvider(RoadTechTelemetryItem item)
    {
        var rawPayload = JsonSerializer.Serialize(item);
        var gps = item.DataGps;
        var latitude = ReadDecimal(gps, "latitude", "lat");
        var longitude = ReadDecimal(gps, "longitude", "long", "lon", "lng");
        var speed = ReadDecimal(gps, "speedKph", "speed", "speedkmh", "kmh");
        var timestamp = ReadString(gps, "eventTimeUtc", "eventTime", "gpsTime", "positionTime", "timestamp", "time", "datetime", "dateTimeUtc", "t")
            ?? ReadExtraString(item.Extra, "eventTimeUtc", "eventTime", "gpsTime", "positionTime", "timestamp", "time", "datetime", "dateTimeUtc", "t");
        var eventTime = ParseRoadTechTimestamp(timestamp);
        var moving = item.Moving ?? ReadBoolean(gps, "isMoving", "moving");
        var ignitionOn = item.Ign
            ?? ReadBoolean(item.DataCan, "ignitionOn", "ignition", "ign", "engineOn", "engineRunning")
            ?? ReadBoolean(gps, "ignitionOn", "ignition", "ign", "engineOn", "engineRunning")
            ?? ReadBoolean(item.DataGaz, "ignitionOn", "ignition", "ign", "engineOn", "engineRunning");
        var driverName = ReadProviderDriverName(item);
        var driverCardNumber = ReadProviderDriverCard(item);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)))[..24];
        return new DotTelemetryRecord(
            fingerprint,
            string.IsNullOrWhiteSpace(item.VehCode) ? item.VehRtid.ToString() : item.VehCode,
            eventTime,
            latitude,
            longitude,
            speed,
            ignitionOn,
            moving,
            latitude is null || longitude is null ? "GPS coordinates unavailable" : "Received",
            rawPayload,
            driverName,
            driverCardNumber);
    }

    private static string? ReadProviderDriverName(RoadTechTelemetryItem item)
    {
        var direct = ReadExtraString(item.Extra, "DriverName", "driverName", "CurrentDriver", "currentDriver", "CurrentDriverName", "currentDriverName", "TachoDriver", "tachoDriver", "TachoDriverName", "tachoDriverName", "CardHolder", "cardHolder", "CardHolderName", "cardHolderName", "MemberName", "memberName", "Driver1Name", "driver1Name", "Driver", "driver");
        if (!string.IsNullOrWhiteSpace(direct) && !LooksLikeIdentifierOnly(direct)) return CleanDriverName(direct);
        foreach (var source in new[] { item.DataGps, item.DataCan, item.DataGaz })
        {
            var nested = ReadString(source, "driverName", "currentDriver", "currentDriverName", "tachoDriver", "tachoDriverName", "cardHolder", "cardHolderName", "memberName", "driver1Name", "driver");
            if (!string.IsNullOrWhiteSpace(nested) && !LooksLikeIdentifierOnly(nested)) return CleanDriverName(nested);
            var recursive = FindDriverNameRecursive(source, 0);
            if (!string.IsNullOrWhiteSpace(recursive)) return CleanDriverName(recursive);
        }
        foreach (var value in item.Extra.Values) { var recursive = FindDriverNameRecursive(value, 0); if (!string.IsNullOrWhiteSpace(recursive)) return CleanDriverName(recursive); }
        return null;
    }

    private static string? ReadProviderDriverCard(RoadTechTelemetryItem item)
    {
        var direct = ReadExtraString(item.Extra, "DriverCardNumber", "driverCardNumber", "DriverCard", "driverCard", "CardNumber", "cardNumber", "CardNo", "cardNo", "CardNoShort", "cardNoShort", "TachoCard", "tachoCard", "TachoCardNumber", "tachoCardNumber", "Driver1Card", "driver1Card", "Driver1CardNumber", "driver1CardNumber");
        var cleaned = CleanCardNumber(direct); if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
        foreach (var source in new[] { item.DataGps, item.DataCan, item.DataGaz })
        {
            var nested = ReadString(source, "driverCardNumber", "driverCard", "cardNumber", "cardNo", "cardNoShort", "tachoCard", "tachoCardNumber", "driver1Card", "driver1CardNumber");
            cleaned = CleanCardNumber(nested); if (!string.IsNullOrWhiteSpace(cleaned)) return cleaned;
            var recursive = FindDriverCardRecursive(source, 0); if (!string.IsNullOrWhiteSpace(recursive)) return recursive;
        }
        foreach (var value in item.Extra.Values) { var recursive = FindDriverCardRecursive(value, 0); if (!string.IsNullOrWhiteSpace(recursive)) return recursive; }
        return null;
    }

    private static string? FindDriverNameRecursive(JsonElement? source, int depth)
    {
        if (source is null || depth > 5) return null; var value = source.Value;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var key = property.Name.Replace("_", string.Empty).Replace("-", string.Empty);
                var looks = key.Contains("driver", StringComparison.OrdinalIgnoreCase) || key.Contains("cardholder", StringComparison.OrdinalIgnoreCase) || key.Contains("membername", StringComparison.OrdinalIgnoreCase) || key.Contains("tachoname", StringComparison.OrdinalIgnoreCase);
                if (looks)
                {
                    if (property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) { var candidate = CleanDriverName(property.Value.ToString()); if (!string.IsNullOrWhiteSpace(candidate) && !LooksLikeIdentifierOnly(candidate)) return candidate; }
                    if (property.Value.ValueKind == JsonValueKind.Object) { var candidate = CleanDriverName(ReadString(property.Value, "name", "displayName", "fullName", "driverName", "memberName", "cardHolderName")); if (!string.IsNullOrWhiteSpace(candidate) && !LooksLikeIdentifierOnly(candidate)) return candidate; }
                }
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { var nested = FindDriverNameRecursive(property.Value, depth + 1); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) { var nested = FindDriverNameRecursive(child, depth + 1); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static string? FindDriverCardRecursive(JsonElement? source, int depth)
    {
        if (source is null || depth > 5) return null; var value = source.Value;
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var key = property.Name.Replace("_", string.Empty).Replace("-", string.Empty);
                var looks = key.Contains("card", StringComparison.OrdinalIgnoreCase) && (key.Contains("driver", StringComparison.OrdinalIgnoreCase) || key.Contains("tacho", StringComparison.OrdinalIgnoreCase) || key.Contains("number", StringComparison.OrdinalIgnoreCase) || key.Contains("no", StringComparison.OrdinalIgnoreCase));
                if (looks && property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number) { var candidate = CleanCardNumber(property.Value.ToString()); if (!string.IsNullOrWhiteSpace(candidate)) return candidate; }
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) { var nested = FindDriverCardRecursive(property.Value, depth + 1); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) { var nested = FindDriverCardRecursive(child, depth + 1); if (!string.IsNullOrWhiteSpace(nested)) return nested; }
        return null;
    }

    private static bool LooksLikeIdentifierOnly(string value) { var compact = new string(value.Where(char.IsLetterOrDigit).ToArray()); return compact.Length == 0 || compact.All(char.IsDigit) || (compact.Length > 12 && compact.Count(char.IsDigit) > compact.Length / 2); }
    private static DateTimeOffset ParseRoadTechTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DateTimeOffset.UtcNow;
        if (Regex.IsMatch(value.Trim(), @"(?:Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase) &&
            DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var offset))
            return offset.ToUniversalTime();
        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var utc)
            ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc))
            : DateTimeOffset.UtcNow;
    }
    private static string? CleanCardNumber(string? value) { var compact = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()); return compact.Length < 8 ? null : compact; }
    private static string? ReadExtraString(IReadOnlyDictionary<string, JsonElement> values, params string[] names) { foreach (var name in names) { var pair = values.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)); if (string.IsNullOrWhiteSpace(pair.Key)) continue; var value = pair.Value; if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number) return value.ToString(); if (value.ValueKind == JsonValueKind.Object) { var nested = ReadString(value, "name", "displayName", "driverName", "fullName", "memberName", "cardHolderName", "cardNumber", "cardNo", "cardNoShort"); if (!string.IsNullOrWhiteSpace(nested)) return nested; } } return null; }
    private static string? CleanDriverName(string? value) { var cleaned = (value ?? string.Empty).Trim(); return cleaned.Length == 0 || cleaned == "0" || cleaned.Equals("unknown", StringComparison.OrdinalIgnoreCase) || cleaned.Equals("none", StringComparison.OrdinalIgnoreCase) ? null : cleaned; }
    private static string? ReadString(JsonElement? source, params string[] names) { if (source is not { ValueKind: JsonValueKind.Object } objectValue) return null; foreach (var property in objectValue.EnumerateObject()) if (names.Contains(property.Name, StringComparer.OrdinalIgnoreCase)) return property.Value.ToString(); return null; }
    private static decimal? ReadDecimal(JsonElement? source, params string[] names) => decimal.TryParse(ReadString(source, names), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? ReadBoolean(JsonElement? source, params string[] names) { var value = ReadString(source, names)?.Trim(); if (bool.TryParse(value, out var parsed)) return parsed; if (value is "1" or "on" or "ON" or "running" or "RUNNING") return true; if (value is "0" or "off" or "OFF" or "stopped" or "STOPPED") return false; return null; }
}
