using ExcelDataReader;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public sealed class SainsburyHaulierPlanParser
{
    private static readonly Regex SubjectDateRegex = new(
        @"\b(?<day>\d{1,2})[./-](?<month>\d{1,2})(?:[./-](?<year>\d{2,4}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static SainsburyHaulierPlanParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        if (!IsSainsburyPlanEmail(request))
            return null;

        var attachment = (request.Attachments ?? []).FirstOrDefault(item =>
            item.IsInline != true &&
            !string.IsNullOrWhiteSpace(item.EffectiveContentBase64) &&
            (item.Name ?? string.Empty).EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase));

        if (attachment is null)
        {
            return new EmailIntakeParseResult(
                [],
                ["Sainsbury transport plan email detected but the Haulier Plan workbook content was not supplied to the TMS."],
                "Sainsbury transport plan requires its workbook attachment for safe order creation.");
        }

        try
        {
            var bytes = DecodeBase64(attachment.EffectiveContentBase64!);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var orders = new List<ParsedEmailOrder>();
            var warnings = new List<string>();

            do
            {
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var values = new object?[reader.FieldCount];
                    for (var index = 0; index < reader.FieldCount; index++) values[index] = reader.GetValue(index);
                    rows.Add(values);
                }

                var headerIndex = rows.FindIndex(IsHeader);
                if (headerIndex < 0) continue;
                var map = HeaderMap(rows[headerIndex]);
                var screenshotSchema = IsScreenshotSchema(map);
                var haulierIndex = Find(map, screenshotSchema ? "collectingdepothaulier" : "depothaulier");
                var commentIndex = Find(map, "commentsfordepot");
                var collectIndex = Find(map, screenshotSchema ? "collectionsite" : "collectfrom");
                var deliverIndex = Find(map, screenshotSchema ? "destination" : "deliverto");
                var supplierRefIndex = Find(map, screenshotSchema ? "scionordernumber" : "collectionsupplierref");
                var deliveryRefIndex = Find(map, "sainsburysdeliveryref");
                var collectDateIndex = Find(map, screenshotSchema ? "collectiondate" : "targetcollectiondatetime");
                var deliveryDateIndex = Find(map, screenshotSchema ? "originaldeliverydate" : "targetdeliverydatetime");
                var collectTimeIndex = Find(map, screenshotSchema ? "collectiontime" : string.Empty);
                var deliveryTimeIndex = Find(map, screenshotSchema
                    ? new[] { "originaldeliverytime", "originaldelivery" }
                    : new[] { string.Empty });
                var palletsIndex = Find(map, "pallets", "pallet", "palletcount");
                if (haulierIndex < 0 || collectIndex < 0 || deliverIndex < 0 || collectDateIndex < 0) continue;

                for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex];
                    var haulier = CellText(row, haulierIndex);
                    var collectFrom = CellText(row, collectIndex);
                    var deliverTo = CellText(row, deliverIndex);
                    if (!Normalise(haulier).Contains("stuartlyons", StringComparison.OrdinalIgnoreCase)) continue;
                    if (IsTemplateValue(collectFrom) || IsTemplateValue(deliverTo)) continue;

                    var collectionAt = screenshotSchema
                        ? CombineDateAndTime(CellDate(row, collectDateIndex), CellTime(row, collectTimeIndex))
                        : CellDateTime(row, collectDateIndex);
                    var deliveryAt = screenshotSchema
                        ? CombineDateAndTime(SubjectDate(request.Subject, request.ReceivedAtUtc), CellTime(row, deliveryTimeIndex))
                        : CellDateTime(row, deliveryDateIndex);
                    if (screenshotSchema && deliveryAt is null)
                        deliveryAt = CombineDateAndTime(collectionAt is null ? null : DateOnly.FromDateTime(collectionAt.Value), CellTime(row, deliveryTimeIndex));
                    if (collectionAt is null || deliveryAt is null) continue;

                    var supplierRef = CleanRef(CellText(row, supplierRefIndex));
                    var deliveryRef = CleanRef(CellText(row, deliveryRefIndex));
                    var customerPo = deliveryRef ?? supplierRef;
                    var orderReference = BuildReference(customerPo ?? StableEmailReference(request.MessageId), collectFrom!, deliverTo!);
                    var pallets = palletsIndex < 0 ? (int?)null : CellInt(row, palletsIndex);
                    var itemWarnings = new List<string>();
                    if (pallets is null or <= 0)
                        itemWarnings.Add("Pallet quantity is not supplied on the Sainsbury Haulier Plan and must be confirmed if required for capacity planning.");
                    var comments = CellText(row, commentIndex);
                    var naturalKey = $"sainsbury|{deliveryRef ?? supplierRef ?? orderReference}|{collectionAt.Value:yyyy-MM-dd}|{Normalise(collectFrom)}|{Normalise(deliverTo)}";
                    var instructions = string.Join(" · ", new[]
                    {
                        "Order type: Sainsbury backhaul",
                        string.IsNullOrWhiteSpace(supplierRef) ? null : $"Supplier ref: {supplierRef}",
                        string.IsNullOrWhiteSpace(deliveryRef) ? null : $"Sainsbury ref: {deliveryRef}",
                        $"Collection time: {collectionAt.Value:HH:mm}",
                        $"Delivery time: {deliveryAt.Value:HH:mm}",
                        string.IsNullOrWhiteSpace(comments) ? null : $"Depot comments: {comments}",
                        $"Source email: {request.Subject}",
                        $"Source attachment: {attachment.Name}",
                        "Intake warning: Pallet quantity not supplied on source plan"
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));

                    var payload = new Dictionary<string, object?>
                    {
                        ["poNumber"] = orderReference,
                        ["customerPo"] = customerPo,
                        ["customerCode"] = "SAINSBURY",
                        ["collectionDate"] = DateOnly.FromDateTime(collectionAt.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["deliveryDate"] = DateOnly.FromDateTime(deliveryAt.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["pallets"] = pallets,
                        ["sellerName"] = collectFrom,
                        ["marketName"] = "SAINSBURY",
                        ["stallNumber"] = deliverTo,
                        ["requestedTime"] = deliveryAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                        ["availableTime"] = collectionAt.Value.ToString("HH:mm", CultureInfo.InvariantCulture),
                        ["jobType"] = "Sainsbury backhaul",
                        ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
                        ["sourceMessageId"] = request.MessageId,
                        ["sourceInternetMessageId"] = request.InternetMessageId,
                        ["sourceSender"] = request.SenderAddress,
                        ["sourceSenderName"] = request.SenderName,
                        ["sourceSubject"] = request.Subject,
                        ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                        ["sourceWebLink"] = request.WebLink,
                        ["sourceAttachmentName"] = attachment.Name,
                        ["sourceSheet"] = reader.Name,
                        ["sourceRow"] = rowIndex + 1,
                        ["intakeNaturalKey"] = naturalKey,
                        ["intakeConfidence"] = itemWarnings.Count == 0 ? "High" : "Medium",
                        ["intakeWarnings"] = itemWarnings,
                        ["intakeParser"] = "Sainsbury Haulier Plan"
                    };
                    orders.Add(new ParsedEmailOrder($"sainsbury-row-{rowIndex + 1}", naturalKey, JsonSerializer.SerializeToElement(payload), itemWarnings));
                }
            }
            while (reader.NextResult());

            if (orders.Count == 0)
                return new EmailIntakeParseResult([], ["Sainsbury Haulier Plan workbook was read but no populated STUART LYONS rows were found."], "No Stuart Lyons transport rows were found in the attached Sainsbury plan.");

            return new EmailIntakeParseResult(orders, warnings, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EmailIntakeParseResult([], [$"Sainsbury Haulier Plan could not be parsed: {ex.GetBaseException().Message}"], "Sainsbury workbook parsing failed; manual review is required.");
        }
    }

    internal static bool IsSainsburyPlanEmail(MailboxEmailIntakeRequest request)
    {
        var subject = request.Subject?.Trim() ?? string.Empty;
        if (subject.Contains("Transport plan for STUART LYONS", StringComparison.OrdinalIgnoreCase))
            return true;

        // The Central Transport PCC/Bond messages use subjects such as:
        // [CrosspointPCC] STUART LYONS - Crosspoint PCC Plan for delivery date 19/09/2026
        // [DaventryBond] STUART LYONS - Daventry Bond Plan for delivery date 19/09/2026
        // [HDKPCC] STUART LYONS - Haydock PCC Plan for delivery date 19/09/2026
        // Scope the broader subject match to Sainsbury's own domain so another haulier/customer
        // email mentioning Stuart Lyons and "plan" cannot be claimed by this parser.
        var sender = request.SenderAddress?.Trim() ?? string.Empty;
        return sender.EndsWith("@sainsburys.co.uk", StringComparison.OrdinalIgnoreCase)
            && subject.Contains("STUART LYONS", StringComparison.OrdinalIgnoreCase)
            && subject.Contains("Plan", StringComparison.OrdinalIgnoreCase)
            && (subject.Contains("PCC", StringComparison.OrdinalIgnoreCase)
                || subject.Contains("Bond", StringComparison.OrdinalIgnoreCase)
                || subject.Contains("delivery date", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHeader(object?[] row)
    {
        var values = row.Select(value => Normalise(CellText(value))).ToHashSet();
        return (values.Contains("collectfrom") && values.Contains("deliverto") &&
                values.Contains("targetcollectiondatetime") && values.Contains("targetdeliverydatetime"))
            || IsScreenshotSchema(values);
    }

    private static bool IsScreenshotSchema(Dictionary<string, int> map) => IsScreenshotSchema(map.Keys);

    private static bool IsScreenshotSchema(IEnumerable<string> keys)
    {
        var values = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return values.Contains("collectingdepothaulier")
            && values.Contains("collectionsite")
            && values.Contains("destination")
            && values.Contains("scionordernumber")
            && values.Contains("collectiondate")
            && values.Contains("collectiontime");
    }

    private static Dictionary<string, int> HeaderMap(object?[] row)
    {
        var result = new Dictionary<string, int>();
        for (var index = 0; index < row.Length; index++)
        {
            var key = Normalise(CellText(row[index]));
            if (key.Length > 0 && !result.ContainsKey(key)) result[key] = index;
        }
        return result;
    }

    private static int Find(Dictionary<string, int> map, string key) => map.TryGetValue(key, out var index) ? index : -1;
    private static int Find(Dictionary<string, int> map, params string[] keys) =>
        keys.FirstOrDefault(key => !string.IsNullOrWhiteSpace(key) && map.TryGetValue(key, out _)) is { } match
            ? map[match]
            : -1;
    private static string? CellText(object?[] row, int index) => index < 0 || index >= row.Length ? null : CellText(row[index]);
    private static string? CellText(object? value) => value is null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();

    private static int? CellInt(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is double number) return (int)number;
        return int.TryParse(CellText(row[index]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static DateTime? CellDateTime(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return dateTime;
        if (row[index] is double serial && serial > 1 && serial < 100000) return DateTime.FromOADate(serial);
        if (DateTime.TryParse(CellText(row[index]), CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AssumeLocal, out var parsed)) return parsed;
        return null;
    }

    private static DateOnly? CellDate(object?[] row, int index)
    {
        var value = CellDateTime(row, index);
        return value is null ? null : DateOnly.FromDateTime(value.Value);
    }

    private static TimeOnly? CellTime(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return TimeOnly.FromDateTime(dateTime);
        if (row[index] is double serial && serial >= 0 && serial < 1) return TimeOnly.FromTimeSpan(TimeSpan.FromDays(serial));
        if (TimeOnly.TryParse(CellText(row[index]), CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var parsed)) return parsed;
        if (DateTime.TryParse(CellText(row[index]), CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date)) return TimeOnly.FromDateTime(date);
        return null;
    }

    private static DateTime? CombineDateAndTime(DateOnly? date, TimeOnly? time) =>
        date is null ? null : date.Value.ToDateTime(time ?? TimeOnly.MinValue);

    private static DateOnly? SubjectDate(string? subject, DateTimeOffset? receivedAt)
    {
        var match = SubjectDateRegex.Match(subject ?? string.Empty);
        if (!match.Success) return null;
        var yearText = match.Groups["year"].Value;
        var year = string.IsNullOrWhiteSpace(yearText)
            ? (receivedAt ?? DateTimeOffset.UtcNow).Year
            : int.Parse(yearText, CultureInfo.InvariantCulture) < 100
                ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture)
                : int.Parse(yearText, CultureInfo.InvariantCulture);
        return DateOnly.TryParse($"{match.Groups["day"].Value}/{match.Groups["month"].Value}/{year}", CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static bool IsTemplateValue(string? value) => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "27", StringComparison.OrdinalIgnoreCase);

    private static string? CleanRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.FirstOrDefault(part => part.Any(char.IsLetterOrDigit))?.Trim();
    }

    private static string BuildReference(string sourceRef, string collectFrom, string deliverTo)
    {
        var left = SafeToken(sourceRef, 34);
        var route = SafeToken($"{collectFrom}-{deliverTo}", 42);
        var result = $"{left}/{route}";
        return result[..Math.Min(80, result.Length)];
    }

    private static string StableEmailReference(string messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }

    private static string SafeToken(string value, int max)
    {
        var clean = new string(value.ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) || character is '-' or '/' ? character : '-').ToArray());
        while (clean.Contains("--", StringComparison.Ordinal)) clean = clean.Replace("--", "-", StringComparison.Ordinal);
        clean = clean.Trim('-', '/');
        if (clean.Length == 0) clean = "ORDER";
        return clean[..Math.Min(max, clean.Length)];
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static byte[] DecodeBase64(string value)
    {
        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma >= 0 && trimmed[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[(comma + 1)..];
        return Convert.FromBase64String(trimmed);
    }
}
