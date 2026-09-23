using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slh.Tms.Api.Contracts;

public sealed record PlannerPlanImportRequest(
    string? Schema,
    DateOnly PlanningDate,
    List<PlannerPlanRunRequest> Runs,
    List<PlannerPlanExceptionRequest>? Exceptions = null);

[JsonConverter(typeof(PlannerPlanRunRequestConverter))]
public sealed record PlannerPlanRunRequest(
    string RunRef,
    string? PlannerRun,
    string? RunType,
    DateOnly PlanningDate,
    string? Driver,
    string? Vehicle,
    string? Trailer,
    string? PlannerNote,
    bool IncludeInImport,
    string? ReconciliationStatus,
    PlannerPlanSourceRequest? Source,
    List<PlannerPlanStopRequest> Stops);

[JsonConverter(typeof(PlannerPlanStopRequestConverter))]
public sealed record PlannerPlanStopRequest(
    int Sequence,
    string? CollectionSite,
    string? DeliverySite,
    decimal? Pallets,
    string? Reference,
    string? PalletType,
    string? CollectFrom,
    string? CollectTo,
    string? Deadline,
    int? SourceRow,
    string? CollectionSiteArrDate = null,
    string? CollectionSiteArrTime = null,
    string? DespatchedDate = null,
    string? DespatchedTime = null,
    string? DeliveredDate = null,
    string? DeliveryArrivalTime = null,
    string? DeliveryDepartTime = null,
    string? ReasonForLate = null);

public sealed record PlannerPlanSourceRequest(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Workbook,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Sheet);

public sealed record PlannerPlanExceptionRequest(
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Severity,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? RunRef,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Code,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Detail,
    [property: JsonConverter(typeof(FlexibleStringJsonConverter))] string? Source);

public sealed record PlannerPlanImportSummary(
    DateOnly PlanningDate,
    int Received,
    int Created,
    int Updated,
    int Unchanged,
    int Held,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> UnresolvedDrivers,
    IReadOnlyList<string> UnresolvedVehicles,
    IReadOnlyList<string> UnresolvedTrailers,
    IReadOnlyList<PlannerPlanRunResult> Runs);

public sealed record PlannerPlanRunResult(
    string RunRef,
    string TmsReference,
    string Outcome,
    string CapacityStatus,
    decimal UtilisationPercent,
    string? Detail);

internal sealed class PlannerPlanRunRequestConverter : JsonConverter<PlannerPlanRunRequest>
{
    public override PlannerPlanRunRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var runRef = PlannerImportJson.Text(root, "runRef", "runReference", "runId", "run", "runNumber", "runNo") ?? string.Empty;
        var planningDate = PlannerImportJson.Date(root, "planningDate", "operatingDate", "date") ?? default;
        var stopsElement = PlannerImportJson.Property(root, "stops", "sourceLines", "lines", "jobs");
        var stops = stopsElement is { ValueKind: JsonValueKind.Array }
            ? JsonSerializer.Deserialize<List<PlannerPlanStopRequest>>(stopsElement.Value.GetRawText(), options) ?? []
            : [];
        var sourceElement = PlannerImportJson.Property(root, "source");
        var source = sourceElement is { ValueKind: JsonValueKind.Object }
            ? JsonSerializer.Deserialize<PlannerPlanSourceRequest>(sourceElement.Value.GetRawText(), options)
            : null;

        return new PlannerPlanRunRequest(
            runRef,
            PlannerImportJson.Text(root, "plannerRun", "runName", "displayRun"),
            PlannerImportJson.Text(root, "runType", "period", "shift"),
            planningDate,
            PlannerImportJson.Text(root, "driver", "driverName", "assignedDriver", "driverDisplayName"),
            PlannerImportJson.Text(root, "vehicle", "vehicleRegistration", "vehicleReg", "registration", "reg"),
            PlannerImportJson.Text(root, "trailer", "trailerNumber", "trailerNo", "trailerRef"),
            PlannerImportJson.Text(root, "plannerNote", "plannerNotes", "notes"),
            PlannerImportJson.Bool(root, true, "includeInImport", "include", "import", "selected"),
            PlannerImportJson.Text(root, "reconciliationStatus", "reconciliation", "status"),
            source,
            stops);
    }

    public override void Write(Utf8JsonWriter writer, PlannerPlanRunRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("runRef", value.RunRef);
        PlannerImportJson.WriteString(writer, "plannerRun", value.PlannerRun);
        PlannerImportJson.WriteString(writer, "runType", value.RunType);
        writer.WriteString("planningDate", value.PlanningDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        PlannerImportJson.WriteString(writer, "driver", value.Driver);
        PlannerImportJson.WriteString(writer, "vehicle", value.Vehicle);
        PlannerImportJson.WriteString(writer, "trailer", value.Trailer);
        PlannerImportJson.WriteString(writer, "plannerNote", value.PlannerNote);
        writer.WriteBoolean("includeInImport", value.IncludeInImport);
        PlannerImportJson.WriteString(writer, "reconciliationStatus", value.ReconciliationStatus);
        if (value.Source is not null) { writer.WritePropertyName("source"); JsonSerializer.Serialize(writer, value.Source, options); }
        writer.WritePropertyName("stops");
        JsonSerializer.Serialize(writer, value.Stops, options);
        writer.WriteEndObject();
    }
}

internal sealed class PlannerPlanStopRequestConverter : JsonConverter<PlannerPlanStopRequest>
{
    public override PlannerPlanStopRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new PlannerPlanStopRequest(
            PlannerImportJson.Int(root, "sequence", "seq", "stopSequence", "line", "lineNumber") ?? 0,
            PlannerImportJson.Text(root, "collectionSite", "collectionLocation", "collection", "collectSite", "pickupLocation", "pickupSite", "sellerName"),
            PlannerImportJson.Text(root, "deliverySite", "deliveryLocation", "delivery", "deliverSite", "destination", "depot", "stallNumber"),
            PlannerImportJson.Decimal(root, "pallets", "palletCount", "palletQty", "palletQuantity", "qty", "quantity", "palletsOrdered"),
            PlannerImportJson.Text(root, "reference", "poNumber", "po", "orderReference", "orderRef", "customerReference"),
            PlannerImportJson.Text(root, "palletType", "palletFormat", "palletKind", "palletSize"),
            PlannerImportJson.Text(root, "collectFrom", "collectionFrom", "collectStart", "collectionStart", "collectionTimeFrom"),
            PlannerImportJson.Text(root, "collectTo", "collectionTo", "collectEnd", "collectionEnd", "collectionTimeTo"),
            PlannerImportJson.Text(root, "deadline", "deliverBy", "deliveryBy", "deliveryDeadline"),
            PlannerImportJson.Int(root, "sourceRow", "row", "rowNumber"),
            PlannerImportJson.Text(root, "collectionSiteArrDate", "collectionArrivalDate", "collectionDate"),
            PlannerImportJson.Text(root, "collectionSiteArrTime", "collectionArrivalTime"),
            PlannerImportJson.Text(root, "despatchedDate", "dispatchedDate"),
            PlannerImportJson.Text(root, "despatchedTime", "dispatchedTime"),
            PlannerImportJson.Text(root, "deliveredDate", "deliveryDate"),
            PlannerImportJson.Text(root, "deliveryArrivalTime", "deliveredTime"),
            PlannerImportJson.Text(root, "deliveryDepartTime", "deliveryDepartureTime"),
            PlannerImportJson.Text(root, "reasonForLate", "lateReason", "etaNote"));
    }

    public override void Write(Utf8JsonWriter writer, PlannerPlanStopRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("sequence", value.Sequence);
        PlannerImportJson.WriteString(writer, "collectionSite", value.CollectionSite);
        PlannerImportJson.WriteString(writer, "deliverySite", value.DeliverySite);
        if (value.Pallets is decimal pallets) writer.WriteNumber("pallets", pallets); else writer.WriteNull("pallets");
        PlannerImportJson.WriteString(writer, "reference", value.Reference);
        PlannerImportJson.WriteString(writer, "palletType", value.PalletType);
        PlannerImportJson.WriteString(writer, "collectFrom", value.CollectFrom);
        PlannerImportJson.WriteString(writer, "collectTo", value.CollectTo);
        PlannerImportJson.WriteString(writer, "deadline", value.Deadline);
        if (value.SourceRow is int sourceRow) writer.WriteNumber("sourceRow", sourceRow); else writer.WriteNull("sourceRow");
        PlannerImportJson.WriteString(writer, "collectionSiteArrDate", value.CollectionSiteArrDate);
        PlannerImportJson.WriteString(writer, "collectionSiteArrTime", value.CollectionSiteArrTime);
        PlannerImportJson.WriteString(writer, "despatchedDate", value.DespatchedDate);
        PlannerImportJson.WriteString(writer, "despatchedTime", value.DespatchedTime);
        PlannerImportJson.WriteString(writer, "deliveredDate", value.DeliveredDate);
        PlannerImportJson.WriteString(writer, "deliveryArrivalTime", value.DeliveryArrivalTime);
        PlannerImportJson.WriteString(writer, "deliveryDepartTime", value.DeliveryDepartTime);
        PlannerImportJson.WriteString(writer, "reasonForLate", value.ReasonForLate);
        writer.WriteEndObject();
    }
}

internal static class PlannerImportJson
{
    public static JsonElement? Property(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var wanted = names.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in root.EnumerateObject())
            if (wanted.Contains(Normalize(property.Name))) return property.Value;
        return null;
    }

    public static string? Text(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString()?.Trim() : value.Value.ToString().Trim();
    }

    public static int? Int(JsonElement root, params string[] names) => int.TryParse(Text(root, names), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    public static decimal? Decimal(JsonElement root, params string[] names)
    {
        var value = Property(root, names);
        if (value is null) return null;
        if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDecimal(out var number)) return number;
        var text = Text(root, names);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out number) || decimal.TryParse(text, out number) ? number : null;
    }

    public static DateOnly? Date(JsonElement root, params string[] names) => DateOnly.TryParse(Text(root, names), CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) ? value : null;

    public static bool Bool(JsonElement root, bool fallback, params string[] names)
    {
        var value = Property(root, names);
        if (value is null) return fallback;
        if (value.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.Value.GetBoolean();
        var text = Text(root, names);
        if (bool.TryParse(text, out var result)) return result;
        if (string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) || text == "1") return true;
        if (string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) || text == "0") return false;
        return fallback;
    }

    public static void WriteString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static string Normalize(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
