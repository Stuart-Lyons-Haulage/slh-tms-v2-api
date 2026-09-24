using ExcelDataReader;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Controller-local intake guard. Because OrderIntakeController resolves unqualified
/// types in its own namespace first, this wrapper becomes the parser used by the
/// production email intake without changing the controller contract. Existing
/// specialist body parsers remain authoritative and workbook-specific corrections
/// are applied before the generic EmailOrderIntakeService fallback.
/// </summary>
public sealed class SpecialistMailboxOrderParser
{
    private readonly Slh.Tms.Api.Services.SpecialistMailboxOrderParser inner = new();

    private static readonly Regex NumericDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])[./-](?<year>20\d{2}|\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PurchaseOrderRegex = new(
        @"\b(?<po>PORD[A-Z0-9/-]+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static SpecialistMailboxOrderParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        // Verified workbook profiles are deliberately evaluated before the older
        // body parsers. A known sender + subject family + workbook structure is the
        // safest automatic intake lane and keeps retailer-name noise out of Order Review.
        var summerBerry = TryParseSummerBerryMorrisonsAldi(request);
        if (summerBerry is not null)
            return summerBerry;

        var greenhouse = TryParseGreenhouseAldiWorkbook(request);
        if (greenhouse is not null)
            return greenhouse;

        var barfootsAldi = TryParseBarfootsAldiWorkbook(request);
        if (barfootsAldi is not null)
            return barfootsAldi;

        var wealmoorWaitrose = TryParseWealmoorWaitroseWorkbook(request);
        if (wealmoorWaitrose is not null)
            return wealmoorWaitrose;

        var vitacress = TryParseVitacressWaitroseWorkbook(request);
        if (vitacress is not null)
            return vitacress;

        return inner.TryParse(request);
    }

    private static EmailIntakeParseResult? TryParseSummerBerryMorrisonsAldi(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        var attachments = (request.Attachments ?? [])
            .Where(item => item.IsInline != true && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64))
            .Where(item => IsExcel(item.Name))
            .ToList();

        if (!sender.EndsWith("@summerberry.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("ALDI", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("Morrisons", StringComparison.OrdinalIgnoreCase) ||
            attachments.Count == 0)
            return null;

        var planningDate = ExtractPlanningDate(request);
        if (planningDate is null)
            return null;

        var rawPo = Match(PurchaseOrderRegex, $"{request.Subject}\n{request.BodyText}\n{request.BodyHtml}", "po");
        var orders = new List<ParsedEmailOrder>();
        var globalWarnings = new List<string>();

        foreach (var attachment in attachments)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(attachment.EffectiveContentBase64!));
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var sheetNumber = 0;
                do
                {
                    sheetNumber++;
                    var rows = ReadRows(reader);
                    var headerIndex = rows.FindIndex(IsSummerBerryBookingHeader);
                    if (headerIndex < 0)
                        continue;

                    var headers = HeaderMap(rows[headerIndex]);
                    var collectionIndex = FindColumn(headers, "collectionsite", "collection", "collectfrom");
                    var dateIndex = FindColumn(headers, "date", "deliverydate", "bookingdate");
                    var depotIndex = FindColumn(headers, "depotdescription", "depot", "destination", "deliverysite");
                    var palletsIndex = FindColumn(headers, "pallets", "pallet", "qty", "quantity");
                    var requestTimeIndex = FindColumn(headers, "requesttime", "requestedtime", "bookingtime", "deliverytime");
                    var availableTimeIndex = FindColumn(headers, "availabletime", "collectiontime", "readytime");

                    if (depotIndex < 0 || palletsIndex < 0)
                        continue;

                    var staleRows = 0;
                    for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var depot = CellText(row, depotIndex);
                        var pallets = CellInt(row, palletsIndex);
                        if (string.IsNullOrWhiteSpace(depot) || pallets is null or <= 0)
                            continue;

                        var rowDate = CellDate(row, dateIndex) ?? planningDate;
                        if (rowDate is null)
                            continue;

                        // The source email/attachment planning date is authoritative.
                        // Permit adjacent-day overnight work only. This blocks stale
                        // historical tabs such as the 02/12/2024 Orders sheet inside
                        // the 29/08/2026 Summer Berry booking workbook.
                        if (Math.Abs(rowDate.Value.DayNumber - planningDate.Value.DayNumber) > 1)
                        {
                            staleRows++;
                            continue;
                        }

                        var identity = SummerBerryIdentity(depot);
                        if (identity is null)
                            continue;

                        var collection = CellText(row, collectionIndex) ?? "SB-Groves Farm";
                        var destination = depot.Trim();
                        var requestedTime = CellTime(row, requestTimeIndex);
                        var availableTime = CellTime(row, availableTimeIndex);
                        var warnings = new List<string>();
                        if (string.IsNullOrWhiteSpace(rawPo))
                            warnings.Add("No customer PO/reference was found in the email.");

                        var baseReference = rawPo ?? StableEmailReference(request.MessageId);
                        var reference = BuildReference(baseReference, destination);
                        var naturalKey = NaturalKey(request, identity.Value.CustomerCode, collection, destination, rowDate.Value, pallets.Value);
                        var payload = BuildPayload(
                            request,
                            reference,
                            rawPo,
                            identity.Value.CustomerCode,
                            rowDate.Value,
                            rowDate.Value,
                            pallets.Value,
                            collection,
                            destination,
                            requestedTime,
                            availableTime,
                            attachment.Name,
                            reader.Name,
                            rowIndex + 1,
                            "Summer Berry Morrisons/Aldi workbook",
                            warnings,
                            identity.Value.RetailerCode);

                        orders.Add(new ParsedEmailOrder(
                            $"summerberry-{sheetNumber}-{rowIndex + 1}-{NormaliseKey(destination)}",
                            naturalKey,
                            payload,
                            warnings));
                    }

                    if (staleRows > 0)
                    {
                        globalWarnings.Add($"Skipped {staleRows} stale workbook row(s) on sheet '{reader.Name}' because their dates did not match the source planning date {planningDate:dd/MM/yyyy}.");
                    }
                }
                while (reader.NextResult());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalWarnings.Add($"Attachment '{attachment.Name}' could not be parsed by the Summer Berry workbook guard: {ex.GetBaseException().Message}");
            }
        }

        if (orders.Count == 0)
            return null;

        return new EmailIntakeParseResult(orders, globalWarnings, null);
    }

    internal static (string CustomerCode, string RetailerCode)? SummerBerryIdentity(string depot)
    {
        if (depot.StartsWith("ALDI", StringComparison.OrdinalIgnoreCase))
            return ("SUMMERBERRY", "ALDI");
        if (depot.StartsWith("MORRISONS", StringComparison.OrdinalIgnoreCase))
            return ("SUMMERBERRY", "MORRISONS");
        return null;
    }

    private static EmailIntakeParseResult? TryParseGreenhouseAldiWorkbook(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        if (!sender.EndsWith("@thegreenhousesussex.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("ALDI", StringComparison.OrdinalIgnoreCase))
            return null;

        var attachments = (request.Attachments ?? [])
            .Where(item => item.IsInline != true && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64))
            .Where(item => IsExcel(item.Name))
            .Where(item => (item.Name ?? string.Empty).Contains("GHS Aldi Bookings", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (attachments.Count == 0)
            return null;

        var orders = new List<ParsedEmailOrder>();
        var warnings = new List<string>();
        foreach (var attachment in attachments)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(attachment.EffectiveContentBase64!));
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var sheetNumber = 0;
                do
                {
                    sheetNumber++;
                    var rows = ReadRows(reader);
                    var headerIndex = rows.FindIndex(row =>
                        RowContains(row, "Date") &&
                        RowContains(row, "Collection Site") &&
                        RowContains(row, "Depot Description") &&
                        RowContains(row, "Pallets") &&
                        RowContains(row, "Temperature") &&
                        RowContains(row, "Pallet Type"));
                    if (headerIndex < 0)
                        continue;

                    var headers = HeaderMap(rows[headerIndex]);
                    var dateIndex = FindColumn(headers, "date");
                    var collectionIndex = FindColumn(headers, "collectionsite");
                    var depotIndex = FindColumn(headers, "depotdescription");
                    var palletsIndex = FindColumn(headers, "pallets");
                    var temperatureIndex = FindColumn(headers, "temperature");
                    var palletTypeIndex = FindColumn(headers, "pallettype");
                    if (dateIndex < 0 || collectionIndex < 0 || depotIndex < 0 || palletsIndex < 0)
                        continue;

                    for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var collection = CellText(row, collectionIndex);
                        var destination = CellText(row, depotIndex);
                        var pallets = CellInt(row, palletsIndex);
                        var date = CellDate(row, dateIndex);
                        if (date is null || pallets is null or <= 0 ||
                            !string.Equals(collection, "Greenhouse", StringComparison.OrdinalIgnoreCase) ||
                            string.IsNullOrWhiteSpace(destination) ||
                            !destination.StartsWith("ALDI-", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var temperature = CellText(row, temperatureIndex);
                        var palletType = CellText(row, palletTypeIndex);
                        var rowWarnings = new List<string>();
                        var reference = $"GHS-{date:yyyyMMdd}-{NormaliseKey(destination)}";
                        var naturalKey = NaturalKey(request, "GHS", collection, destination, date.Value, pallets.Value);
                        var payload = BuildPayload(
                            request,
                            reference,
                            null,
                            "GHS",
                            date.Value,
                            date.Value,
                            pallets.Value,
                            "Greenhouse",
                            destination,
                            null,
                            null,
                            attachment.Name,
                            reader.Name,
                            rowIndex + 1,
                            "Greenhouse Aldi workbook",
                            rowWarnings,
                            "ALDI");

                        var root = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
                        root["temperatureRequirement"] = temperature;
                        root["palletType"] = palletType;
                        payload = JsonSerializer.SerializeToElement(root);

                        orders.Add(new ParsedEmailOrder(
                            $"greenhouse-aldi-{sheetNumber}-{rowIndex + 1}-{NormaliseKey(destination)}",
                            naturalKey,
                            payload,
                            rowWarnings));
                    }
                }
                while (reader.NextResult());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                warnings.Add($"Attachment '{attachment.Name}' could not be parsed by the Greenhouse Aldi workbook parser: {ex.GetBaseException().Message}");
            }
        }

        return orders.Count == 0 ? null : new EmailIntakeParseResult(orders, warnings, null);
    }

    private static EmailIntakeParseResult? TryParseBarfootsAldiWorkbook(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        if (!sender.EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("Aldi Confirmed Booking", StringComparison.OrdinalIgnoreCase))
            return null;

        var planningDate = ExtractPlanningDate(request);
        if (planningDate is null)
            return null;

        var attachments = (request.Attachments ?? [])
            .Where(item => item.IsInline != true && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64))
            .Where(item => IsExcel(item.Name))
            .ToList();
        if (attachments.Count == 0)
            return null;

        var orders = new List<ParsedEmailOrder>();
        var globalWarnings = new List<string>();

        foreach (var attachment in attachments)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(attachment.EffectiveContentBase64!));
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var sheetNumber = 0;
                do
                {
                    sheetNumber++;
                    var rows = ReadRows(reader);
                    var headerIndex = rows.FindIndex(row =>
                        RowContains(row, "Depot Description") &&
                        RowContains(row, "Collection Site") &&
                        (RowContains(row, "Temp.") || RowContains(row, "Temperature")) &&
                        RowContains(row, "Pallets"));
                    if (headerIndex < 0)
                        continue;

                    var headers = HeaderMap(rows[headerIndex]);
                    var depotIndex = FindColumn(headers, "depotdescription", "depot", "destination");
                    var collectionIndex = FindColumn(headers, "collectionsite", "collection");
                    var temperatureIndex = FindColumn(headers, "temp", "temperature");
                    var palletsIndex = FindColumn(headers, "pallets", "pallet", "qty", "quantity");
                    if (depotIndex < 0 || collectionIndex < 0 || palletsIndex < 0)
                        continue;

                    for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var destination = CellText(row, depotIndex);
                        var collection = CellText(row, collectionIndex);
                        var pallets = CellInt(row, palletsIndex);
                        var date = CellDate(row, 0) ?? planningDate;
                        if (date is null || pallets is null or <= 0 ||
                            string.IsNullOrWhiteSpace(collection) ||
                            string.IsNullOrWhiteSpace(destination) ||
                            !destination.StartsWith("ALDI", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (Math.Abs(date.Value.DayNumber - planningDate.Value.DayNumber) > 1)
                            continue;

                        var temperature = CellText(row, temperatureIndex);
                        var discriminator = string.Join("|", new[] { collection, temperature }.Where(value => !string.IsNullOrWhiteSpace(value)));
                        var naturalKey = WorkbookNaturalKey(request, "BARFOOTS", collection, destination, date.Value, discriminator);
                        var reference = $"BARFOOTS-ALDI-{date:yyyyMMdd}-{NormaliseKey(destination)}-{NormaliseKey(discriminator)}";
                        if (reference.Length > 120) reference = reference[..120];

                        var rowWarnings = new List<string>();
                        var payload = BuildPayload(
                            request,
                            reference,
                            null,
                            "BARFOOTS",
                            date.Value,
                            date.Value,
                            pallets.Value,
                            collection.Trim(),
                            destination.Trim(),
                            null,
                            null,
                            attachment.Name,
                            reader.Name,
                            rowIndex + 1,
                            "Barfoots Aldi confirmed workbook",
                            rowWarnings,
                            "ALDI");

                        var root = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
                        root["temperatureRequirement"] = temperature;
                        root["intakeNaturalKey"] = naturalKey;
                        root["intakeProfile"] = "BARFOOTS_ALDI_CONFIRMED_WORKBOOK";
                        payload = JsonSerializer.SerializeToElement(root);

                        orders.Add(new ParsedEmailOrder(
                            $"barfoots-aldi-{sheetNumber}-{rowIndex + 1}-{NormaliseKey(destination)}-{NormaliseKey(discriminator)}",
                            naturalKey,
                            payload,
                            rowWarnings));
                    }
                }
                while (reader.NextResult());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalWarnings.Add($"Attachment '{attachment.Name}' could not be parsed by the Barfoots Aldi workbook parser: {ex.GetBaseException().Message}");
            }
        }

        return orders.Count == 0 ? null : new EmailIntakeParseResult(orders, globalWarnings, null);
    }

    private static EmailIntakeParseResult? TryParseWealmoorWaitroseWorkbook(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        if (!sender.EndsWith("@wealmoor.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("WAITROSE PALLET ESTIMATE", StringComparison.OrdinalIgnoreCase))
            return null;

        var deliveryDate = ExtractPlanningDate(request);
        if (deliveryDate is null)
            return null;

        var attachments = (request.Attachments ?? [])
            .Where(item => item.IsInline != true && !string.IsNullOrWhiteSpace(item.EffectiveContentBase64))
            .Where(item => IsExcel(item.Name))
            .Where(item => (item.Name ?? string.Empty).Contains("WAITROSE PALLET ESTIMATE", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (attachments.Count == 0)
            return null;

        var orders = new List<ParsedEmailOrder>();
        var globalWarnings = new List<string>();

        foreach (var attachment in attachments)
        {
            try
            {
                using var stream = new MemoryStream(Convert.FromBase64String(attachment.EffectiveContentBase64!));
                using var reader = ExcelReaderFactory.CreateReader(stream);
                var sheetNumber = 0;
                do
                {
                    sheetNumber++;
                    var rows = ReadRows(reader);
                    var headerIndex = rows.FindIndex(row =>
                        RowContains(row, "Depot") &&
                        FindColumnPrefix(row, "FRV") >= 0 &&
                        FindColumnPrefix(row, "CHL") >= 0);
                    if (headerIndex < 0)
                        continue;

                    var depotIndex = FindColumn(HeaderMap(rows[headerIndex]), "depot");
                    var frvIndex = FindColumnPrefix(rows[headerIndex], "FRV");
                    var chilledIndex = FindColumnPrefix(rows[headerIndex], "CHL");
                    if (depotIndex < 0 || frvIndex < 0 || chilledIndex < 0)
                        continue;

                    var formDate = rows
                        .Select(FirstDate)
                        .FirstOrDefault(value => value is not null);
                    var workbookDeliveryDate = formDate ?? deliveryDate;
                    if (workbookDeliveryDate is null)
                        continue;

                    // These estimates arrive the day before the Waitrose depot date.
                    // Keep the depot date as delivery and the preceding day as collection.
                    var collectionDate = workbookDeliveryDate.Value.AddDays(-1);

                    for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
                    {
                        var row = rows[rowIndex];
                        var destination = CellText(row, depotIndex);
                        if (string.IsNullOrWhiteSpace(destination))
                            continue;

                        var frvPallets = CellInt(row, frvIndex) ?? 0;
                        var chilledPallets = CellInt(row, chilledIndex) ?? 0;
                        var pallets = frvPallets + chilledPallets;
                        if (pallets <= 0)
                            continue;

                        var naturalKey = WorkbookNaturalKey(
                            request,
                            "WEALMOOR",
                            "Wealmoor Greenford",
                            destination,
                            workbookDeliveryDate.Value,
                            "WAITROSE");
                        var reference = $"WEALMOOR-WR-{workbookDeliveryDate:yyyyMMdd}-{NormaliseKey(destination)}";
                        var rowWarnings = new List<string>();
                        var payload = BuildPayload(
                            request,
                            reference,
                            null,
                            "WEALMOOR",
                            collectionDate,
                            workbookDeliveryDate.Value,
                            pallets,
                            "Wealmoor Greenford",
                            destination.Trim(),
                            null,
                            null,
                            attachment.Name,
                            reader.Name,
                            rowIndex + 1,
                            "Wealmoor Waitrose pallet estimate workbook",
                            rowWarnings,
                            "WAITROSE");

                        var temperature = frvPallets > 0 && chilledPallets > 0
                            ? "+12°C / +2°C"
                            : frvPallets > 0 ? "+12°C" : "+2°C";
                        var root = JsonNode.Parse(payload.GetRawText())?.AsObject() ?? new JsonObject();
                        root["temperatureRequirement"] = temperature;
                        root["intakeNaturalKey"] = naturalKey;
                        root["intakeProfile"] = "WEALMOOR_WAITROSE_PALLET_ESTIMATE";
                        root["palletBreakdown"] = new JsonObject
                        {
                            ["frvPlus12"] = frvPallets,
                            ["chilledPlus2"] = chilledPallets
                        };
                        payload = JsonSerializer.SerializeToElement(root);

                        orders.Add(new ParsedEmailOrder(
                            $"wealmoor-waitrose-{sheetNumber}-{rowIndex + 1}-{NormaliseKey(destination)}",
                            naturalKey,
                            payload,
                            rowWarnings));
                    }
                }
                while (reader.NextResult());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalWarnings.Add($"Attachment '{attachment.Name}' could not be parsed by the Wealmoor Waitrose workbook parser: {ex.GetBaseException().Message}");
            }
        }

        return orders.Count == 0 ? null : new EmailIntakeParseResult(orders, globalWarnings, null);
    }

    private static EmailIntakeParseResult? TryParseVitacressWaitroseWorkbook(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        if (!sender.EndsWith("@vitacress.com", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("VITACRESS", StringComparison.OrdinalIgnoreCase))
            return null;

        var attachment = (request.Attachments ?? [])
            .FirstOrDefault(item => item.IsInline != true &&
                                    !string.IsNullOrWhiteSpace(item.EffectiveContentBase64) &&
                                    IsExcel(item.Name) &&
                                    (item.Name ?? string.Empty).Contains("WAITROSE", StringComparison.OrdinalIgnoreCase));
        if (attachment is null)
            return null;

        var orders = new List<ParsedEmailOrder>();
        var globalWarnings = new List<string>();
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(attachment.EffectiveContentBase64!));
            using var reader = ExcelReaderFactory.CreateReader(stream);
            var sheetNumber = 0;
            do
            {
                sheetNumber++;
                var rows = ReadRows(reader);
                var anchorIndex = rows.FindIndex(row =>
                    RowContains(row, "COLLECTION DATE") &&
                    RowContains(row, "Waitrose PO number") &&
                    RowContains(row, "DELIVERY DATE"));
                if (anchorIndex < 0)
                    continue;

                var anchor = rows[anchorIndex];
                var collectionDate = FirstDate(anchor) ?? ExtractPlanningDate(request)?.AddDays(-1);
                var deliveryDate = LastDate(anchor) ?? ExtractPlanningDate(request) ?? collectionDate;
                if (collectionDate is null || deliveryDate is null)
                    continue;

                var sourceCollection = rows.Take(anchorIndex)
                    .SelectMany(row => row)
                    .Select(CellText)
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value.Contains("Runcton", StringComparison.OrdinalIgnoreCase));
                var collectionSite = string.IsNullOrWhiteSpace(sourceCollection) ? "Vitacress Runcton" : "Vitacress Runcton";

                // Legacy Vitacress workbook layout:
                // col B = depot, col C = pallets, col D = Waitrose PO, col E = collection time.
                for (var rowIndex = anchorIndex + 1; rowIndex < rows.Count; rowIndex++)
                {
                    var row = rows[rowIndex];
                    var destination = CellText(row, 1);
                    var pallets = CellInt(row, 2);
                    var customerPo = CellText(row, 3);
                    var requestedTime = CellTime(row, 4);

                    if (string.IsNullOrWhiteSpace(destination) || pallets is null or <= 0)
                        continue;
                    if (destination.Equals("TOTAL", StringComparison.OrdinalIgnoreCase) ||
                        destination.Equals("WAITROSE", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var warnings = new List<string>();
                    if (string.IsNullOrWhiteSpace(customerPo))
                        warnings.Add("Waitrose PO number was blank in the source workbook.");

                    var baseReference = customerPo ?? StableEmailReference(request.MessageId);
                    var reference = BuildReference(baseReference, destination);
                    var naturalKey = NaturalKey(request, "WAITROSE", collectionSite, destination, deliveryDate.Value, pallets.Value);
                    var payload = BuildPayload(
                        request,
                        reference,
                        customerPo,
                        "WAITROSE",
                        collectionDate.Value,
                        deliveryDate.Value,
                        pallets.Value,
                        collectionSite,
                        destination,
                        requestedTime,
                        null,
                        attachment.Name,
                        reader.Name,
                        rowIndex + 1,
                        "Vitacress Waitrose legacy workbook",
                        warnings);

                    orders.Add(new ParsedEmailOrder(
                        $"vitacress-waitrose-{sheetNumber}-{rowIndex + 1}-{NormaliseKey(destination)}",
                        naturalKey,
                        payload,
                        warnings));
                }
            }
            while (reader.NextResult());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            globalWarnings.Add($"Attachment '{attachment.Name}' could not be parsed by the Vitacress Waitrose workbook parser: {ex.GetBaseException().Message}");
        }

        if (orders.Count == 0)
            return null;

        return new EmailIntakeParseResult(orders, globalWarnings, null);
    }

    private static JsonElement BuildPayload(
        MailboxEmailIntakeRequest request,
        string reference,
        string? customerPo,
        string customer,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        int pallets,
        string collection,
        string destination,
        string? requestedTime,
        string? availableTime,
        string? attachmentName,
        string? sheetName,
        int sourceRow,
        string parser,
        IReadOnlyList<string> warnings,
        string? retailer = null)
    {
        var instructions = string.Join(" · ", new[]
        {
            "Order type: Delivery",
            string.IsNullOrWhiteSpace(customerPo) ? null : $"PO ref: {customerPo}",
            string.IsNullOrWhiteSpace(requestedTime) ? null : $"Requested time: {requestedTime}",
            string.IsNullOrWhiteSpace(availableTime) ? null : $"Available time: {availableTime}",
            $"Source email: {request.Subject}",
            string.IsNullOrWhiteSpace(attachmentName) ? null : $"Source attachment: {attachmentName}",
            string.IsNullOrWhiteSpace(sheetName) ? null : $"Source sheet: {sheetName}",
            $"Source row: {sourceRow}",
            $"Parser: {parser}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = reference,
            ["customerPo"] = customerPo,
            ["customerCode"] = customer,
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["unitType"] = "Pallets",
            ["palletType"] = "Pallets",
            ["sellerName"] = collection,
            ["marketName"] = retailer ?? customer,
            ["retailerCode"] = retailer,
            ["stallNumber"] = destination,
            ["destination"] = destination,
            ["requestedTime"] = requestedTime,
            ["availableTime"] = availableTime,
            ["jobType"] = "Delivery",
            ["driverInstructions"] = instructions,
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["sourceAttachmentName"] = attachmentName,
            ["sourceSheet"] = sheetName,
            ["sourceRow"] = sourceRow,
            ["intakeNaturalKey"] = NaturalKey(request, customer, collection, destination, deliveryDate, pallets),
            ["intakeParser"] = parser,
            ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
            ["intakeWarnings"] = warnings,
            ["plannerReady"] = warnings.Count == 0,
            ["intakeStatus"] = warnings.Count == 0 ? "Ready" : "Review"
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static DateOnly? ExtractPlanningDate(MailboxEmailIntakeRequest request)
    {
        var source = $"{request.Subject}\n{string.Join("\n", (request.Attachments ?? []).Where(item => item.IsInline != true).Select(item => item.Name))}\n{request.BodyText}";
        var match = NumericDateRegex.Match(source);
        if (!match.Success)
            return null;

        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture);
        var yearText = match.Groups["year"].Value;
        var year = yearText.Length == 2 ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture) : int.Parse(yearText, CultureInfo.InvariantCulture);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static List<object?[]> ReadRows(IExcelDataReader reader)
    {
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var row = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++)
                row[index] = reader.GetValue(index);
            rows.Add(row);
        }
        return rows;
    }

    private static bool IsSummerBerryBookingHeader(object?[] row)
    {
        var keys = row.Select(CellText).Where(value => !string.IsNullOrWhiteSpace(value)).Select(NormaliseKey).ToHashSet();
        return keys.Contains("pallets") && keys.Contains("collectionsite") && keys.Contains("depotdescription");
    }

    private static Dictionary<string, int> HeaderMap(object?[] row)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Length; index++)
        {
            var key = NormaliseKey(CellText(row[index]));
            if (!string.IsNullOrWhiteSpace(key) && !map.ContainsKey(key))
                map[key] = index;
        }
        return map;
    }

    private static int FindColumn(IReadOnlyDictionary<string, int> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(name, out var index))
                return index;
        return -1;
    }

    private static int FindColumnPrefix(object?[] row, string prefix)
    {
        var normalisedPrefix = NormaliseKey(prefix);
        for (var index = 0; index < row.Length; index++)
        {
            var key = NormaliseKey(CellText(row[index]));
            if (!string.IsNullOrWhiteSpace(key) &&
                key.StartsWith(normalisedPrefix, StringComparison.OrdinalIgnoreCase))
                return index;
        }
        return -1;
    }

    private static bool RowContains(object?[] row, string value) =>
        row.Select(CellText).Any(text => string.Equals(text?.Trim(), value, StringComparison.OrdinalIgnoreCase));

    private static DateOnly? FirstDate(object?[] row) => row.Select(CellDate).FirstOrDefault(value => value is not null);
    private static DateOnly? LastDate(object?[] row) => row.Select(CellDate).LastOrDefault(value => value is not null);

    private static string? CellText(object?[] row, int index) => index < 0 || index >= row.Length ? null : CellText(row[index]);

    private static string? CellText(object? value)
    {
        if (value is null || value is DBNull) return null;
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            TimeSpan time => time.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : null
        };
    }

    private static int? CellInt(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        return row[index] switch
        {
            int value => value,
            long value when value <= int.MaxValue && value >= int.MinValue => (int)value,
            double value => (int)Math.Round(value, MidpointRounding.AwayFromZero),
            decimal value => (int)Math.Round(value, MidpointRounding.AwayFromZero),
            _ => int.TryParse(CellText(row[index]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
        };
    }

    private static DateOnly? CellDate(object?[] row, int index) => index < 0 || index >= row.Length ? null : CellDate(row[index]);

    private static DateOnly? CellDate(object? value)
    {
        if (value is null || value is DBNull) return null;
        if (value is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (value is double serial && serial > 1 && serial < 100000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        var text = CellText(value);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var parsed))
            return DateOnly.FromDateTime(parsed);
        return null;
    }

    private static string? CellTime(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        var value = row[index];
        if (value is DateTime dateTime) return dateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (value is TimeSpan timeSpan) return timeSpan.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        if (value is double serial && serial >= 0 && serial < 1)
            return DateTime.FromOADate(serial).ToString("HH:mm", CultureInfo.InvariantCulture);
        var text = CellText(value);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TimeOnly.TryParse(text, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var time))
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        return text;
    }

    private static string? Match(Regex regex, string? value, string group)
    {
        var match = regex.Match(value ?? string.Empty);
        return match.Success ? match.Groups[group].Value.Trim() : null;
    }

    private static bool IsExcel(string? name)
    {
        var extension = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        return extension is ".xls" or ".xlsx" or ".xlsm";
    }

    private static string BuildReference(string sourceReference, string destination)
    {
        var suffix = NormaliseKey(destination);
        var value = string.IsNullOrWhiteSpace(suffix) ? sourceReference : $"{sourceReference}/{suffix}";
        return value.Length <= 120 ? value : value[..120];
    }

    private static string NaturalKey(MailboxEmailIntakeRequest request, string customer, string collection, string destination, DateOnly date, int pallets) =>
        $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{customer}|{date:yyyy-MM-dd}|{NormaliseKey(collection)}|{NormaliseKey(destination)}";

    private static string WorkbookNaturalKey(
        MailboxEmailIntakeRequest request,
        string customer,
        string collection,
        string destination,
        DateOnly date,
        string? discriminator) =>
        $"{NaturalKey(request, customer, collection, destination, date, 0)}|{NormaliseKey(discriminator)}";

    private static string StableEmailReference(string? messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId ?? string.Empty));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }

    private static string NormaliseKey(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
