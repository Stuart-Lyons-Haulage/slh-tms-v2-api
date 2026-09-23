using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Parses the Natures Way Foods pallet-order CSV attached to the Info mailbox.
/// The CSV is a daily customer snapshot. Zero-pallet rows are evidence only and
/// are deliberately not staged as transport orders.
/// </summary>
public sealed class NwfPalletOrderCsvParser
{
    private static readonly string[] RequiredHeaders =
    [
        "HAULIERNAME", "REQUESTEDSHIPDATE", "04COLLECTIONSITE", "CUSTOMERNAME",
        "DEPOTID", "DEPOTDESCRIPTION", "DELIVERYADDRESS", "SALESORDERID",
        "CUSTOMERREF", "PALLETNAME", "PALLETQTY", "POREF"
    ];

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
        => TryParse(request, allowBodyFallback: true);

    private EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request, bool allowBodyFallback)
    {
        var candidates = (request.Attachments ?? [])
            .Where(item => item.IsInline != true
                && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64)
                && string.Equals(Path.GetExtension(item.Name ?? string.Empty), ".csv", StringComparison.OrdinalIgnoreCase)
                // Do not classify an arbitrary customer CSV as NWF solely because
                // it happens to use similar column headings. The source email or
                // attachment must carry an NWF/NWAY/Natures Way signal.
                && IsNwfSource(request, item.Name))
            .ToList();

        foreach (var attachment in candidates)
        {
            try
            {
                var text = DecodeText(attachment.EffectiveContentBase64!);
                var rows = ParseCsv(text);
                if (rows.Count == 0) continue;

                var header = HeaderMap(rows[0]);
                if (!RequiredHeaders.All(header.ContainsKey)) continue;

                var orders = new List<ParsedEmailOrder>();
                var warnings = new List<string>();
                var zeroPalletRows = 0;
                var invalidRows = 0;

                for (var index = 1; index < rows.Count; index++)
                {
                    var row = rows[index];
                    if (row.All(string.IsNullOrWhiteSpace)) continue;

                    var haulier = Cell(row, header, "HAULIERNAME");
                    if (!string.IsNullOrWhiteSpace(haulier)
                        && !haulier.Contains("Stuart Lyons", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var date = ParseDate(Cell(row, header, "REQUESTEDSHIPDATE"));
                    var collection = Cell(row, header, "04COLLECTIONSITE");
                    var customerName = Cell(row, header, "CUSTOMERNAME");
                    var depotId = Cell(row, header, "DEPOTID");
                    var depotDescription = Cell(row, header, "DEPOTDESCRIPTION");
                    var deliveryAddress = Cell(row, header, "DELIVERYADDRESS");
                    var salesOrderId = Cell(row, header, "SALESORDERID");
                    var customerRef = Cell(row, header, "CUSTOMERREF");
                    var palletName = Cell(row, header, "PALLETNAME");
                    var palletQty = ParseInt(Cell(row, header, "PALLETQTY"));
                    var poRef = Cell(row, header, "POREF");

                    if (palletQty is null or <= 0)
                    {
                        zeroPalletRows++;
                        continue;
                    }

                    var rowWarnings = new List<string>();
                    if (date is null) rowWarnings.Add("Requested Ship Date is missing or invalid.");
                    if (string.IsNullOrWhiteSpace(collection)) rowWarnings.Add("Collection Site is missing.");
                    if (string.IsNullOrWhiteSpace(depotDescription)) rowWarnings.Add("Depot Description is missing.");
                    if (string.IsNullOrWhiteSpace(salesOrderId)) rowWarnings.Add("Sales Order ID is missing.");
                    if (string.IsNullOrWhiteSpace(poRef))
                        rowWarnings.Add("PO REF is missing; Sales Order ID is being used only as a fallback TMS reference and must be checked before approval.");
                    if (date is null || string.IsNullOrWhiteSpace(collection)
                        || string.IsNullOrWhiteSpace(depotDescription) || string.IsNullOrWhiteSpace(salesOrderId))
                    {
                        invalidRows++;
                        continue;
                    }

                    var collectionToken = Normalise(collection);
                    var depotToken = Normalise(depotId ?? depotDescription);
                    var palletToken = Normalise(palletName);
                    var poToken = Normalise(poRef);
                    var salesToken = Normalise(salesOrderId);
                    var naturalKey = $"NWFCSV|{date:yyyy-MM-dd}|PO:{poToken}|SO:{salesToken}|{collectionToken}|{depotToken}|{palletToken}";
                    var matchKeys = new List<string>
                    {
                        $"NWF|{date:yyyy-MM-dd}|SALES:{salesToken}:{collectionToken}:{depotToken}"
                    };
                    if (!string.IsNullOrWhiteSpace(poToken))
                        matchKeys.Insert(0, $"NWF|{date:yyyy-MM-dd}|PO:{poToken}:{collectionToken}:{depotToken}");

                    // PO REF is the primary TMS identity. Sales Order ID is retained in
                    // the reference as a subordinate discriminator because one NWF PO can
                    // contain multiple sales orders for the same collection/depot route.
                    var referenceRoot = !string.IsNullOrWhiteSpace(poRef)
                        ? $"{poRef}/{salesOrderId}"
                        : salesOrderId;
                    var reference = Clip($"{referenceRoot}/{collection}/{depotId ?? depotDescription}", 80);
                    var instructions = string.Join(" · ", new[]
                    {
                        "Order type: NWF pallet order",
                        string.IsNullOrWhiteSpace(poRef) ? null : $"PO ref: {poRef}",
                        $"Sales order: {salesOrderId}",
                        string.IsNullOrWhiteSpace(customerRef) ? null : $"Customer ref: {customerRef}",
                        string.IsNullOrWhiteSpace(palletName) ? null : $"Pallet type: {palletName}",
                        string.IsNullOrWhiteSpace(depotId) ? null : $"Depot ID: {depotId}",
                        $"Delivery: {depotDescription}",
                        string.IsNullOrWhiteSpace(deliveryAddress) ? null : $"Delivery address: {deliveryAddress}",
                        $"Source snapshot: {attachment.Name} row {index + 1}"
                    }.Where(value => !string.IsNullOrWhiteSpace(value)));

                    var payload = new Dictionary<string, object?>
                    {
                        ["poNumber"] = reference,
                        ["customerPo"] = poRef,
                        ["customerCode"] = "NWF",
                        ["collectionDate"] = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["deliveryDate"] = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        ["pallets"] = palletQty.Value,
                        ["sellerName"] = collection,
                        ["marketName"] = customerName ?? "NWF",
                        ["stallNumber"] = depotDescription,
                        ["jobType"] = "NWF pallet order",
                        ["driverInstructions"] = Clip(instructions, 1000),
                        ["haulierName"] = haulier,
                        ["collectionSite"] = collection,
                        ["customerName"] = customerName,
                        ["depotId"] = depotId,
                        ["depotDescription"] = depotDescription,
                        ["deliveryAddress"] = deliveryAddress,
                        ["salesOrderId"] = salesOrderId,
                        ["customerRef"] = customerRef,
                        ["palletName"] = palletName,
                        ["palletQty"] = palletQty.Value,
                        ["poRef"] = poRef,
                        ["plannerReady"] = true,
                        ["intakeStatus"] = "ReadyForReview",
                        ["intakeNaturalKey"] = naturalKey,
                        ["intakeMatchKeys"] = matchKeys,
                        ["intakeConfidence"] = rowWarnings.Count == 0 ? "High" : "Medium",
                        ["intakeWarnings"] = rowWarnings,
                        ["intakeParser"] = "NWF Pallet Order CSV",
                        ["sourceMessageId"] = request.MessageId,
                        ["sourceInternetMessageId"] = request.InternetMessageId,
                        ["sourceSender"] = request.SenderAddress,
                        ["sourceSenderName"] = request.SenderName,
                        ["sourceSubject"] = request.Subject,
                        ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                        ["sourceWebLink"] = request.WebLink,
                        ["sourceAttachmentName"] = attachment.Name,
                        ["sourceSheet"] = "CSV",
                        ["sourceRow"] = index + 1
                    };

                    orders.Add(new ParsedEmailOrder(
                        $"nwf-csv-{salesToken}-{collectionToken}-{depotToken}-{palletToken}",
                        naturalKey,
                        JsonSerializer.SerializeToElement(payload),
                        rowWarnings));
                }

                if (zeroPalletRows > 0)
                    warnings.Add($"{zeroPalletRows} zero-pallet row(s) were retained in the source evidence but not staged as transport orders.");
                if (invalidRows > 0)
                    warnings.Add($"{invalidRows} positive-pallet row(s) were not staged because mandatory routing fields were missing.");

                return orders.Count == 0
                    ? new EmailIntakeParseResult([], warnings, "NWF pallet-order CSV was recognised but contained no valid positive-pallet transport orders.")
                    : new EmailIntakeParseResult(orders, warnings, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new EmailIntakeParseResult(
                    [],
                    [$"NWF pallet-order CSV could not be parsed: {ex.GetBaseException().Message}"],
                    "NWF pallet-order CSV parsing failed; retain the email for manual review.");
            }
        }

        if (allowBodyFallback && LooksLikeNwfPalletOrder(request) && TryBuildBodyTableCsv(request) is { } bodyCsv)
        {
            var bodyRequest = request with
            {
                Attachments =
                [
                    new MailboxAttachmentRequest(
                        "NWF pallet order email body table.csv",
                        "text/csv",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes(bodyCsv)),
                        false)
                ]
            };
            return TryParse(bodyRequest, allowBodyFallback: false);
        }

        if (LooksLikeNwfPalletOrder(request))
        {
            var attachmentNames = string.Join(", ", (request.Attachments ?? [])
                .Where(item => item.IsInline != true)
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name)));
            var detail = string.IsNullOrWhiteSpace(attachmentNames)
                ? "No attachment metadata was supplied with the recognised NWF pallet-order email."
                : $"No readable NWF pallet-order CSV content was supplied. Attachments seen: {attachmentNames}.";
            return new EmailIntakeParseResult(
                [],
                [detail],
                "NWF pallet-order email was recognised but the CSV attachment content was not available for automatic mapping.");
        }

        return null;
    }

    private static string? TryBuildBodyTableCsv(MailboxEmailIntakeRequest request)
    {
        var htmlRows = ExtractHtmlTableRows(request.BodyHtml);
        if (LooksLikePalletOrderRows(htmlRows)) return ToCsv(htmlRows);
        var body = !string.IsNullOrWhiteSpace(request.BodyHtml) ? request.BodyHtml : request.BodyText;
        var pipeRows = ExtractPipeRows(body);
        return LooksLikePalletOrderRows(pipeRows) ? ToCsv(pipeRows) : null;
    }

    private static List<List<string>> ExtractHtmlTableRows(string? html)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrWhiteSpace(html)) return rows;
        foreach (Match rowMatch in Regex.Matches(html, @"<tr\b[^>]*>(?<row>.*?)</tr>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var cells = Regex.Matches(rowMatch.Groups["row"].Value, @"<t[dh]\b[^>]*>(?<cell>.*?)</t[dh]>", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                .Select(match => CleanHtmlCell(match.Groups["cell"].Value))
                .ToList();
            if (cells.Any(value => !string.IsNullOrWhiteSpace(value))) rows.Add(cells);
        }
        return rows;
    }

    private static List<List<string>> ExtractPipeRows(string? value)
    {
        var rows = new List<List<string>>();
        if (string.IsNullOrWhiteSpace(value)) return rows;
        var text = WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", "\n"));
        var buffered = new StringBuilder();
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.Contains('|') && buffered.Length == 0) continue;

            if (buffered.Length > 0) buffered.Append(' ');
            buffered.Append(line.Trim());

            // Outlook/Graph can wrap a wide 12-column NWF table inside a cell, so
            // one logical row can arrive as two or three physical lines. Rebuild
            // the row before header detection instead of silently losing it.
            var candidate = buffered.ToString();
            var cells = candidate.Split('|').Select(cell => cell.Replace("\\.", ".").Trim()).ToList();
            if (cells.Count < RequiredHeaders.Length) continue;
            if (cells.Count == RequiredHeaders.Length && string.IsNullOrWhiteSpace(cells[^1])) continue;

            buffered.Clear();
            if (cells.All(cell => cell.Length == 0 || cell.All(ch => ch == '-' || char.IsWhiteSpace(ch)))) continue;
            rows.Add(cells);
        }
        return rows;
    }

    private static bool LooksLikePalletOrderRows(IReadOnlyList<List<string>> rows)
    {
        if (rows.Count < 2) return false;
        return rows.Any(row =>
        {
            var header = HeaderMap(row);
            return RequiredHeaders.All(header.ContainsKey);
        });
    }

    private static string ToCsv(IEnumerable<IReadOnlyList<string>> rows) =>
        string.Join("\n", rows.Select(row => string.Join(",", row.Select(Csv))));

    private static string Csv(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : text;
    }

    private static string CleanHtmlCell(string value)
    {
        var text = Regex.Replace(value, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static bool LooksLikeNwfPalletOrder(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        var attachments = string.Join(" ", (request.Attachments ?? []).Select(item => item.Name));
        var value = $"{sender} {subject} {attachments}";
        var nwfSignal =
            value.Contains("NWAY", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Natures Way", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("Nature's Way", StringComparison.OrdinalIgnoreCase);

        // Direct NWF mail is common, but genuine NWF reports are also forwarded or
        // replied to internally. The body fallback still requires the complete
        // 12-column pallet-order table signature, so sender domain is not used as
        // an authority for creating an order.
        return nwfSignal &&
               value.Contains("pallet", StringComparison.OrdinalIgnoreCase) &&
               value.Contains("order", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNwfSource(MailboxEmailIntakeRequest request, string? attachmentName)
    {
        var source = string.Join("\n", new[]
        {
            request.Subject,
            request.SenderAddress,
            attachmentName,
            request.BodyText,
            request.BodyHtml
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return source.Contains("NWF", StringComparison.OrdinalIgnoreCase)
            || source.Contains("NWAY", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Natures Way", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Nature's Way", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecodeText(string base64)
    {
        var value = base64.Trim();
        var comma = value.IndexOf(',');
        if (comma >= 0 && value[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            value = value[(comma + 1)..];
        var bytes = Convert.FromBase64String(value);
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"')
                {
                    field.Append('"');
                    index++;
                }
                else quoted = !quoted;
                continue;
            }

            if (!quoted && ch == ',')
            {
                row.Add(field.ToString().Trim());
                field.Clear();
                continue;
            }

            if (!quoted && (ch == '\r' || ch == '\n'))
            {
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                row.Add(field.ToString().Trim());
                field.Clear();
                if (row.Any(value => !string.IsNullOrWhiteSpace(value))) rows.Add(row);
                row = new List<string>();
                continue;
            }

            field.Append(ch);
        }

        row.Add(field.ToString().Trim());
        if (row.Any(value => !string.IsNullOrWhiteSpace(value))) rows.Add(row);
        return rows;
    }

    private static Dictionary<string, int> HeaderMap(IReadOnlyList<string> header)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < header.Count; index++)
        {
            var key = Normalise(header[index]);
            if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key)) result[key] = index;
        }
        return result;
    }

    private static string? Cell(IReadOnlyList<string> row, IReadOnlyDictionary<string, int> header, string key)
    {
        if (!header.TryGetValue(key, out var index) || index < 0 || index >= row.Count) return null;
        var value = row[index].Trim();
        return value.Length == 0 ? null : value;
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var formats = new[] { "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "d-M-yyyy" };
        return DateOnly.TryParseExact(value.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out date) ? date : null;
    }

    private static int? ParseInt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        return decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number)
            ? (int)Math.Round(number, MidpointRounding.AwayFromZero)
            : null;
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
