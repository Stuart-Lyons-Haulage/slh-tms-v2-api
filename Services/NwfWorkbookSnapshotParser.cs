using ExcelDataReader;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Parses the authoritative NWF Daily Control Tracker workbook rather than only
/// the descriptive email body. The workbook is a versioned snapshot: rows may
/// start life as pre-orders and later gain Transport PO, Load Ref, depot splits
/// or crate-return detail. Those later values enrich the same logical movement.
/// </summary>
public sealed class NwfWorkbookSnapshotParser
{
    static NwfWorkbookSnapshotParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var subject = request.Subject ?? string.Empty;
        var attachment = (request.Attachments ?? []).FirstOrDefault(item =>
            item.IsInline != true &&
            !string.IsNullOrWhiteSpace(item.EffectiveContentBase64) &&
            IsWorkbook(item.Name) &&
            (LooksLikeNwfTracker(item.Name) || LooksLikeNwfTracker(subject)));

        if (attachment is null)
            return null;

        try
        {
            var bytes = DecodeBase64(attachment.EffectiveContentBase64!);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            var orders = new List<ParsedEmailOrder>();
            var warnings = new List<string>();
            var recognisedSheet = false;
            var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
            var snapshotDate = DateOnly.FromDateTime(received.UtcDateTime);
            var minDate = snapshotDate.AddDays(-2);
            var maxDate = snapshotDate.AddDays(60);

            do
            {
                var rows = ReadSheet(reader);
                var sheetName = reader.Name ?? string.Empty;
                var normalisedSheet = Normalise(sheetName);

                if (normalisedSheet.Contains("INBOUND", StringComparison.OrdinalIgnoreCase))
                {
                    recognisedSheet = true;
                    ParseInboundSheet(request, attachment, sheetName, rows, minDate, maxDate, orders, warnings);
                }
                else if (normalisedSheet.Contains("CRATE", StringComparison.OrdinalIgnoreCase))
                {
                    recognisedSheet = true;
                    ParseCrateSheet(request, attachment, sheetName, rows, minDate, maxDate, orders, warnings);
                }
            }
            while (reader.NextResult());

            if (!recognisedSheet)
                return null;

            if (orders.Count == 0)
            {
                return new EmailIntakeParseResult(
                    [],
                    warnings.Count == 0 ? ["NWF tracker workbook was recognised but no current/future order rows were found."] : warnings,
                    "NWF tracker contained no current/future movements that could be staged.");
            }

            return new EmailIntakeParseResult(orders, warnings, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new EmailIntakeParseResult(
                [],
                [$"NWF tracker workbook could not be parsed: {ex.GetBaseException().Message}"],
                "NWF workbook parsing failed; retain the email for manual review.");
        }
    }

    private static void ParseInboundSheet(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        string sheetName,
        IReadOnlyList<object?[]> rows,
        DateOnly minDate,
        DateOnly maxDate,
        List<ParsedEmailOrder> orders,
        List<string> globalWarnings)
    {
        var headerIndex = rows.ToList().FindIndex(row =>
        {
            var keys = row.Select(value => Normalise(CellText(value))).ToHashSet();
            return keys.Contains("DELIVERYDATE") && keys.Contains("TRANSPORTPO") && keys.Contains("LOADINGPLACE");
        });
        if (headerIndex < 0) return;

        var columns = HeaderMap(rows[headerIndex]);
        var dateIndex = Find(columns, "DELIVERYDATE");
        var transportIndex = Find(columns, "TRANSPORTPO");
        var loadIndex = Find(columns, "LOADREF");
        var productIndex = Find(columns, "PRODUCTPO");
        var loadingIndex = Find(columns, "LOADINGPLACE");
        var totalIndex = Find(columns, "TOTALPALLETSPACES", "TOTALSPACES");
        var usedIndex = Find(columns, "PALLETSPACESUSED");
        var crateIndex = Find(columns, "CRATECOLLECTIONSITE", "CRATECOLLECTION");
        var roundIndex = Find(columns, "ROUNDTRIPYN", "ROUNDTRIP");
        var commentsIndex = Find(columns, "COMMENTS", "COMMENT");
        var depotIndexes = new[]
        {
            (Name: "Drayton", Index: Find(columns, "DRAYTON")),
            (Name: "Merston", Index: Find(columns, "MERSTON")),
            (Name: "Runcton", Index: Find(columns, "RUNCTON")),
            (Name: "Selsey", Index: Find(columns, "SELSEY"))
        };

        for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var date = CellDate(row, dateIndex);
            if (date is null || date < minDate || date > maxDate) continue;

            var transportPo = Clean(CellText(row, transportIndex));
            var loadRef = Clean(CellText(row, loadIndex));
            var productPo = Clean(CellText(row, productIndex));
            var loadingPlace = Clean(CellText(row, loadingIndex));
            if (string.IsNullOrWhiteSpace(transportPo) && string.IsNullOrWhiteSpace(loadRef) &&
                string.IsNullOrWhiteSpace(productPo) && string.IsNullOrWhiteSpace(loadingPlace))
                continue;

            var totalSpaces = CellInt(row, totalIndex);
            var usedSpaces = CellInt(row, usedIndex);
            var crateSite = Clean(CellText(row, crateIndex));
            var roundTrip = Clean(CellText(row, roundIndex));
            var comments = Clean(CellText(row, commentsIndex));
            var matchKeys = BuildInboundMatchKeys(date.Value, productPo, transportPo, loadRef, loadingPlace);
            var movementKey = matchKeys.First();
            var baseReady = !string.IsNullOrWhiteSpace(transportPo) && IsMeaningfulLoadRef(loadRef) && !string.IsNullOrWhiteSpace(loadingPlace);

            var positiveDepots = depotIndexes
                .Select(depot => (depot.Name, Pallets: CellInt(row, depot.Index)))
                .Where(depot => depot.Pallets is > 0)
                .ToList();

            if (positiveDepots.Count == 0)
            {
                var rowWarnings = BaseInboundWarnings(transportPo, loadRef, loadingPlace, crateSite, comments);
                rowWarnings.Add("No positive NWF depot allocation is present yet; this row is retained as a pre-order awaiting instruction.");
                orders.Add(BuildInboundOrder(
                    request, attachment, sheetName, rowIndex + 1, date.Value,
                    transportPo, loadRef, productPo, loadingPlace, null, null,
                    totalSpaces, usedSpaces, crateSite, roundTrip, comments,
                    movementKey, matchKeys, false, rowWarnings));
                continue;
            }

            foreach (var depot in positiveDepots)
            {
                var rowWarnings = BaseInboundWarnings(transportPo, loadRef, loadingPlace, crateSite, comments);
                var plannerReady = baseReady;
                if (!plannerReady)
                    rowWarnings.Add("This NWF movement is a pre-order: Transport PO and a full SLH Load Ref are required before it can be accepted into live planning.");

                orders.Add(BuildInboundOrder(
                    request, attachment, sheetName, rowIndex + 1, date.Value,
                    transportPo, loadRef, productPo, loadingPlace, depot.Name, depot.Pallets,
                    totalSpaces, usedSpaces, crateSite, roundTrip, comments,
                    movementKey, matchKeys, plannerReady, rowWarnings));
            }
        }
    }

    private static ParsedEmailOrder BuildInboundOrder(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        string sheetName,
        int sourceRow,
        DateOnly date,
        string? transportPo,
        string? loadRef,
        string? productPo,
        string? loadingPlace,
        string? destination,
        int? pallets,
        int? totalSpaces,
        int? usedSpaces,
        string? crateSite,
        string? roundTrip,
        string? comments,
        string movementKey,
        IReadOnlyList<string> matchKeys,
        bool plannerReady,
        List<string> warnings)
    {
        var destinationToken = string.IsNullOrWhiteSpace(destination) ? "UNALLOCATED" : Normalise(destination);
        var naturalKey = $"{movementKey}|DEST:{destinationToken}";
        var bestReference = FirstUseful(productPo, transportPo, IsMeaningfulLoadRef(loadRef) ? loadRef : null)
            ?? $"NWF-{date:yyyyMMdd}-{Normalise(loadingPlace)}";
        var orderReference = BuildReference(bestReference, destination ?? "PREORDER");
        var status = plannerReady ? "ReadyForReview" : "PreOrder";
        var instructionParts = new[]
        {
            plannerReady ? "Order type: NWF inbound" : "Order type: NWF pre-order",
            string.IsNullOrWhiteSpace(transportPo) ? null : $"Transport PO: {transportPo}",
            string.IsNullOrWhiteSpace(loadRef) ? null : $"Load ref: {loadRef}",
            string.IsNullOrWhiteSpace(productPo) ? null : $"Product PO: {productPo}",
            string.IsNullOrWhiteSpace(destination) ? null : $"NWF depot: {destination}",
            totalSpaces is null ? null : $"Total pallet spaces: {totalSpaces}",
            usedSpaces is null ? null : $"Pallet spaces used: {usedSpaces}",
            string.IsNullOrWhiteSpace(crateSite) ? null : $"Crate collection site: {crateSite}",
            string.IsNullOrWhiteSpace(roundTrip) ? null : $"Round trip: {roundTrip}",
            string.IsNullOrWhiteSpace(comments) ? null : $"NWF comments: {comments}",
            $"Source snapshot: {attachment.Name} / {sheetName} row {sourceRow}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        };
        var instructions = string.Join(" · ", instructionParts.Where(value => !string.IsNullOrWhiteSpace(value)));

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerPo"] = transportPo ?? productPo,
            ["customerCode"] = "NWF",
            ["collectionDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = loadingPlace,
            ["marketName"] = "NWF",
            ["stallNumber"] = destination,
            ["jobType"] = plannerReady ? "NWF inbound" : "NWF pre-order",
            ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
            ["transportPo"] = transportPo,
            ["loadRef"] = loadRef,
            ["productPo"] = productPo,
            ["totalPalletSpaces"] = totalSpaces,
            ["palletSpacesUsed"] = usedSpaces,
            ["crateCollectionSite"] = crateSite,
            ["roundTrip"] = roundTrip,
            ["nwfComments"] = comments,
            ["plannerReady"] = plannerReady,
            ["intakeStatus"] = status,
            ["intakeMovementKey"] = movementKey,
            ["intakeMatchKeys"] = matchKeys,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = plannerReady && warnings.Count == 0 ? "High" : plannerReady ? "Medium" : "PreOrder",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = "NWF Workbook Snapshot",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["sourceAttachmentName"] = attachment.Name,
            ["sourceSheet"] = sheetName,
            ["sourceRow"] = sourceRow
        };

        return new ParsedEmailOrder(
            $"nwf-inbound-{sourceRow}-{destinationToken}",
            naturalKey,
            JsonSerializer.SerializeToElement(payload),
            warnings);
    }

    private static void ParseCrateSheet(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        string sheetName,
        IReadOnlyList<object?[]> rows,
        DateOnly minDate,
        DateOnly maxDate,
        List<ParsedEmailOrder> orders,
        List<string> globalWarnings)
    {
        var headerIndex = rows.ToList().FindIndex(row =>
        {
            var keys = row.Select(value => Normalise(CellText(value))).ToHashSet();
            return (keys.Contains("CRATELOADINGDATE") && keys.Contains("TRANSPORTPO")) ||
                   (keys.Contains("NWFTRANSPORTPO") && keys.Contains("REQUIREDCOLLECTIONDATE"));
        });
        if (headerIndex < 0) return;

        var columns = HeaderMap(rows[headerIndex]);
        if (columns.ContainsKey("CRATELOADINGDATE"))
            ParseCurrentCrateReturns(request, attachment, sheetName, rows, headerIndex, columns, minDate, maxDate, orders);
        else
            ParseLegacyCrateDump(request, attachment, sheetName, rows, headerIndex, columns, minDate, maxDate, orders);
    }

    private static void ParseCurrentCrateReturns(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        string sheetName,
        IReadOnlyList<object?[]> rows,
        int headerIndex,
        Dictionary<string, int> columns,
        DateOnly minDate,
        DateOnly maxDate,
        List<ParsedEmailOrder> orders)
    {
        var dateIndex = Find(columns, "CRATELOADINGDATE");
        var transportIndex = Find(columns, "TRANSPORTPO");
        var returningIndex = Find(columns, "RETURNINGTO");
        var referenceIndex = Find(columns, "COLLECTIONREFERENCE");
        var collectionDepots = new[]
        {
            (Name: "Selsey", Index: Find(columns, "SELSEYNUMBEROFPALLETS")),
            (Name: "Runcton", Index: Find(columns, "RUNCTONNUMBEROFPALLETS"))
        };

        for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var date = CellDate(row, dateIndex);
            if (date is null || date < minDate || date > maxDate) continue;
            var transportPo = Clean(CellText(row, transportIndex));
            var returningTo = Clean(CellText(row, returningIndex));
            var collectionReference = Clean(CellText(row, referenceIndex));
            if (string.IsNullOrWhiteSpace(transportPo) && string.IsNullOrWhiteSpace(returningTo) && string.IsNullOrWhiteSpace(collectionReference)) continue;

            foreach (var depot in collectionDepots)
            {
                var pallets = CellInt(row, depot.Index);
                if (pallets is not > 0) continue;
                var ready = !string.IsNullOrWhiteSpace(transportPo) && !string.IsNullOrWhiteSpace(returningTo);
                var warnings = new List<string>();
                if (!ready) warnings.Add("Crate return is incomplete and remains a pre-order until Transport PO and return destination are supplied.");
                var matchKeys = BuildCrateMatchKeys(date.Value, transportPo, collectionReference, returningTo, depot.Name);
                orders.Add(BuildCrateOrder(
                    request, attachment, sheetName, rowIndex + 1, date.Value, date.Value,
                    transportPo, collectionReference, null, depot.Name, returningTo, pallets,
                    matchKeys.First(), matchKeys, ready, warnings));
            }
        }
    }

    private static void ParseLegacyCrateDump(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        string sheetName,
        IReadOnlyList<object?[]> rows,
        int headerIndex,
        Dictionary<string, int> columns,
        DateOnly minDate,
        DateOnly maxDate,
        List<ParsedEmailOrder> orders)
    {
        var transportIndex = Find(columns, "NWFTRANSPORTPO");
        var collectionDateIndex = Find(columns, "REQUIREDCOLLECTIONDATE");
        var deliveryDateIndex = Find(columns, "REQUIREDDELIVERYDATE");
        var collectionDepotIndex = Find(columns, "COLLECTIONDEPOT");
        var collectionRefIndex = Find(columns, "COLLECTIONREFERENCE");
        var cratePoIndex = Find(columns, "NWFCRATEPOFORGROWER");
        var deliveryIndex = Find(columns, "DELIVERYLOCATION");
        var palletsIndex = Find(columns, "PALLETS");

        for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var collectionDate = CellDate(row, collectionDateIndex);
            if (collectionDate is null || collectionDate < minDate || collectionDate > maxDate) continue;
            var deliveryDate = CellDate(row, deliveryDateIndex) ?? collectionDate;
            var transportPo = Clean(CellText(row, transportIndex));
            var collectionDepot = Clean(CellText(row, collectionDepotIndex));
            var collectionReference = Clean(CellText(row, collectionRefIndex));
            var cratePo = Clean(CellText(row, cratePoIndex));
            var delivery = Clean(CellText(row, deliveryIndex));
            var pallets = CellInt(row, palletsIndex);
            if (string.IsNullOrWhiteSpace(transportPo) && string.IsNullOrWhiteSpace(cratePo) && string.IsNullOrWhiteSpace(collectionReference)) continue;

            var ready = !string.IsNullOrWhiteSpace(transportPo) && !string.IsNullOrWhiteSpace(collectionDepot) &&
                        !string.IsNullOrWhiteSpace(delivery) && pallets is > 0;
            var warnings = new List<string>();
            if (!ready) warnings.Add("Crate return row is incomplete and remains a pre-order until collection, delivery and pallet detail are complete.");
            var matchKeys = BuildCrateMatchKeys(collectionDate.Value, transportPo, cratePo ?? collectionReference, delivery, collectionDepot);
            orders.Add(BuildCrateOrder(
                request, attachment, sheetName, rowIndex + 1, collectionDate.Value, deliveryDate.Value,
                transportPo, collectionReference, cratePo, collectionDepot, delivery, pallets,
                matchKeys.First(), matchKeys, ready, warnings));
        }
    }

    private static ParsedEmailOrder BuildCrateOrder(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        string sheetName,
        int sourceRow,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        string? transportPo,
        string? collectionReference,
        string? cratePo,
        string? collectionDepot,
        string? delivery,
        int? pallets,
        string movementKey,
        IReadOnlyList<string> matchKeys,
        bool plannerReady,
        List<string> warnings)
    {
        var sourceRef = FirstUseful(cratePo, transportPo, collectionReference) ?? $"NWF-CRATE-{collectionDate:yyyyMMdd}";
        var naturalKey = $"{movementKey}|DEST:{Normalise(delivery ?? "UNALLOCATED")}";
        var orderReference = BuildReference(sourceRef, delivery ?? "PREORDER");
        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerPo"] = transportPo ?? cratePo,
            ["customerCode"] = "NWF",
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collectionDepot,
            ["marketName"] = "NWF",
            ["stallNumber"] = delivery,
            ["jobType"] = plannerReady ? "NWF crate return" : "NWF crate pre-order",
            ["driverInstructions"] = $"NWF crate return · Collection ref: {collectionReference ?? "TBC"} · Crate PO: {cratePo ?? "TBC"} · Source snapshot: {attachment.Name} / {sheetName} row {sourceRow}",
            ["transportPo"] = transportPo,
            ["cratePo"] = cratePo,
            ["collectionReference"] = collectionReference,
            ["plannerReady"] = plannerReady,
            ["intakeStatus"] = plannerReady ? "ReadyForReview" : "PreOrder",
            ["intakeMovementKey"] = movementKey,
            ["intakeMatchKeys"] = matchKeys,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = plannerReady && warnings.Count == 0 ? "High" : plannerReady ? "Medium" : "PreOrder",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = "NWF Workbook Snapshot",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["sourceAttachmentName"] = attachment.Name,
            ["sourceSheet"] = sheetName,
            ["sourceRow"] = sourceRow
        };

        return new ParsedEmailOrder(
            $"nwf-crate-{sourceRow}-{Normalise(collectionDepot)}",
            naturalKey,
            JsonSerializer.SerializeToElement(payload),
            warnings);
    }

    private static List<string> BaseInboundWarnings(string? transportPo, string? loadRef, string? loadingPlace, string? crateSite, string? comments)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(transportPo)) warnings.Add("Transport PO is blank on this NWF snapshot row.");
        if (!IsMeaningfulLoadRef(loadRef)) warnings.Add("SLH Load Ref has not yet been assigned.");
        if (string.IsNullOrWhiteSpace(loadingPlace)) warnings.Add("Loading place is blank.");
        if (!string.IsNullOrWhiteSpace(crateSite) || (!string.IsNullOrWhiteSpace(comments) && comments.Contains("crate", StringComparison.OrdinalIgnoreCase)))
            warnings.Add("Crate-return detail is present; review the crate instruction alongside the inbound movement.");
        return warnings;
    }

    private static IReadOnlyList<string> BuildInboundMatchKeys(DateOnly date, string? productPo, string? transportPo, string? loadRef, string? loadingPlace)
    {
        var keys = new List<string>();
        AddKey(keys, date, "PRODUCT", productPo);
        AddKey(keys, date, "TRANSPORT", transportPo);
        if (IsMeaningfulLoadRef(loadRef)) AddKey(keys, date, "LOAD", loadRef);
        AddKey(keys, date, "LOADING", loadingPlace);
        if (keys.Count == 0) keys.Add($"NWF|INBOUND|{date:yyyy-MM-dd}|UNKNOWN");
        return keys;
    }

    private static IReadOnlyList<string> BuildCrateMatchKeys(DateOnly date, string? transportPo, string? crateOrCollectionRef, string? delivery, string? collectionDepot)
    {
        var keys = new List<string>();
        AddKey(keys, date, "CRATEREF", crateOrCollectionRef);
        AddKey(keys, date, "TRANSPORT", transportPo);
        if (!string.IsNullOrWhiteSpace(delivery) || !string.IsNullOrWhiteSpace(collectionDepot))
            keys.Add($"NWF|CRATE|{date:yyyy-MM-dd}|ROUTE:{Normalise(collectionDepot)}>{Normalise(delivery)}");
        if (keys.Count == 0) keys.Add($"NWF|CRATE|{date:yyyy-MM-dd}|UNKNOWN");
        return keys;
    }

    private static void AddKey(List<string> keys, DateOnly date, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var key = $"NWF|{date:yyyy-MM-dd}|{type}:{Normalise(value)}";
        if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
    }

    private static bool LooksLikeNwfTracker(string? value)
    {
        var text = value ?? string.Empty;
        return text.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("NWAY", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("DAILY TRACKER", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("DAILY CONTROL TRACKER", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkbook(string? name)
    {
        var extension = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
        return extension is ".xls" or ".xlsx" or ".xlsm";
    }

    private static List<object?[]> ReadSheet(IExcelDataReader reader)
    {
        var rows = new List<object?[]>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < reader.FieldCount; index++) values[index] = reader.GetValue(index);
            rows.Add(values);
        }
        return rows;
    }

    private static Dictionary<string, int> HeaderMap(object?[] row)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Length; index++)
        {
            var key = Normalise(CellText(row[index]));
            if (key.Length > 0 && !result.ContainsKey(key)) result[key] = index;
        }
        return result;
    }

    private static int Find(Dictionary<string, int> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(name, out var index)) return index;
        return -1;
    }

    private static string? CellText(object?[] row, int index) => index < 0 || index >= row.Length ? null : CellText(row[index]);
    private static string? CellText(object? value) => value is null || value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();

    private static int? CellInt(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is int intValue) return intValue;
        if (row[index] is double doubleValue) return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
        return int.TryParse(CellText(row[index]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static DateOnly? CellDate(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (row[index] is double serial && serial > 1 && serial < 100000) return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        var text = CellText(row[index]);
        if (DateOnly.TryParse(text, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var date)) return date;
        if (DateTime.TryParse(text, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.None, out var dateTimeText)) return DateOnly.FromDateTime(dateTimeText);
        return null;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static bool IsMeaningfulLoadRef(string? value) => !string.IsNullOrWhiteSpace(value) && !string.Equals(Normalise(value), "SLH", StringComparison.OrdinalIgnoreCase);
    private static string? FirstUseful(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string BuildReference(string sourceRef, string destination)
    {
        var left = SafeToken(sourceRef, 52);
        var right = SafeToken(destination, 20);
        var result = $"{left}/{right}";
        return result[..Math.Min(80, result.Length)];
    }

    private static string SafeToken(string value, int max)
    {
        var clean = new string(value.ToUpperInvariant().Select(character => char.IsLetterOrDigit(character) || character is '-' or '/' ? character : '-').ToArray());
        while (clean.Contains("--", StringComparison.Ordinal)) clean = clean.Replace("--", "-", StringComparison.Ordinal);
        clean = clean.Trim('-', '/');
        if (clean.Length == 0) clean = "ORDER";
        return clean[..Math.Min(max, clean.Length)];
    }

    private static byte[] DecodeBase64(string value)
    {
        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma >= 0 && trimmed[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[(comma + 1)..];
        return Convert.FromBase64String(trimmed);
    }
}
