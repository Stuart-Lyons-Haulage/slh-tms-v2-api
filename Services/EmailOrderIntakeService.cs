using ExcelDataReader;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public sealed class EmailOrderIntakeService
{
    private static readonly Regex DateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitPoRegex = new(
        @"\b(?:PORD[A-Z0-9/-]*|THE[A-Z0-9/-]+)\b|(?:\b(?:PO(?:\s+number)?|Purchase\s+Order)\b\s*[:#.-]?\s*(?<po>[A-Z0-9][A-Z0-9/-]{2,}))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TotalPalletsRegex = new(
        @"\bTotal\s+Pallets?\s*[:=-]?\s*(?<qty>\d+)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LabelledQuantityRegex = new(
        @"\b(?:Pallets?|Trolleys?)\s*[:=-]?\s*(?<qty>\d{1,3})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PalletQuantityRegex = new(
        @"\b(?<qty>\d{1,3})\s+(?:pallets?|trolleys?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MonthNameDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?\s+(?<month>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)(?:\s+(?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CollectionTimeRegex = new(
        @"\bCollection(?:\s+time)?\s*[:=-]?\s*(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReadyForCollectionTimeRegex = new(
        @"\bready(?:\s+for\s+collection)?\s+(?:from|at|about)\s*(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TemperatureRegex = new(
        @"\bTransport\s+at\s*(?<temp>[+-]?\d+(?:\.\d+)?)\s*(?:degrees?|°)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CollectFromRegex = new(
        @"\bCollect\s+from\s*[:=-]?\s*(?<site>[^\r\n]{2,120})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeliveryToRegex = new(
        @"\bdelivery\s+to\s+(?<site>[^\r\n.]{2,180})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LabelRegex = new(
        @"(?im)^\s*(?<label>customer|depot\s+date|delivery\s+date|deliver(?:y)?|collection|collect|pickup|pallets?|address\s+of\s+delivery|adress\s+of\s+delivery|delivery\s+address|deliver\s+to|destination|ship\s+to|depot)\s*[:=-]\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LabelStartRegex = new(
        @"(?im)^\s*(?:customer|depot\s+date|delivery\s+date|deliver(?:y)?|collection|collect|pickup|pallets?|address\s+of\s+delivery|adress\s+of\s+delivery|delivery\s+address|deliver\s+to|destination|ship\s+to|depot)\s*[:=-]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeliveryDeadlineRegex = new(
        @"\b(?:not\s+later\s+than|no\s+later\s+than|latest\s+by|before|by)\s*(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReFwRegex = new(@"^(?:(?:RE|FW|FWD)\s*:\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly IReadOnlyDictionary<string, string> SenderDomainCollectionSites = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["summerberry.co.uk"] = "Summer Berry",
        ["langmeadherbs.co.uk"] = "Ham Farm",
        ["langmeadfarms.co.uk"] = "Ham Farm",
        ["hillsplants.com"] = "Hill Brothers",
        ["doubleh.co.uk"] = "Double H"
    };

    private static readonly IReadOnlyList<KnownIntakeSignal> KnownSignals =
    [
        new("SAINSBURY", "Sainsbury", ["SAINSBURY", "SAINSBURYS", "SAINSBURY'S"]),
        new("WAITROSE", "Waitrose", ["WAITROSE", "WEIGHTROSE"]),
        new("NWF", "Natures Way", ["NATURES WAY", "NATURE'S WAY", "NWF", "NWAY"]),
        new("BARFOOTS", "Barfoots", ["BARFOOTS", "BARFOOT"]),
        new("LANGMEADS", "Langmeads", ["LANGMEAD", "LANGMEADS", "LANGMEAD HERBS", "HAM FARM"]),
        new("MORRISONS", "Morrisons", ["MORRISONS", "MORRISON'S"]),
        new("ALDI", "Aldi", ["ALDI"]),
        new("COOP", "COOP", ["COOP", "CO-OP", "CO OP"]),
        new("OCADO", "Ocado", ["OCADO"]),
        new("HILLBROTHERS", "Hams Hall", ["HAMS HALL", "HILL BROTHERS", "HILLBROTHERS", "HILLS PLANTS"])
    ];

    static EmailOrderIntakeService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public EmailIntakeParseResult Parse(MailboxEmailIntakeRequest request, IReadOnlyCollection<string>? masterSiteNames = null)
    {
        var subject = (request.Subject ?? string.Empty).Trim();
        var sender = (request.SenderAddress ?? string.Empty).Trim();
        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var attachmentNames = string.Join("\n", (request.Attachments ?? [])
            .Where(attachment => attachment.IsInline != true)
            .Select(attachment => attachment.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name)));
        var sourceText = $"{subject}\n{body}\n{attachmentNames}";

        if (string.IsNullOrWhiteSpace(request.MessageId))
            return EmailIntakeParseResult.Ignored("MessageId is required for idempotent mailbox intake.");

        if (LooksTmsLoopback(subject, sender, body))
            return EmailIntakeParseResult.Ignored("Internal TMS intake/test notification ignored so system outputs cannot loop back into Orders.");

        if (LooksOperationalOnly(subject, body))
            return EmailIntakeParseResult.Ignored("Operational request detected; it was not converted into a transport order automatically.");

        var receivedAt = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var sourceDate = ExtractDate(sourceText, receivedAt);
        var rawPo = ExtractPo(sourceText);
        var globalWarnings = new List<string>();
        var orders = new List<ParsedEmailOrder>();

        foreach (var attachment in request.Attachments ?? [])
        {
            if (attachment.IsInline == true || string.IsNullOrWhiteSpace(attachment.EffectiveContentBase64))
                continue;

            var extension = Path.GetExtension(attachment.Name ?? string.Empty).ToLowerInvariant();
            if (extension is not (".xls" or ".xlsx" or ".xlsm"))
                continue;

            try
            {
                orders.AddRange(ParseWorkbook(
                    request,
                    attachment,
                    sourceDate,
                    rawPo,
                    body));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                globalWarnings.Add($"Attachment '{attachment.Name}' could not be parsed: {ex.GetBaseException().Message}");
            }
        }

        if (orders.Count == 0)
        {
            orders.AddRange(ParseStructuredBodyOrders(request, sourceDate, rawPo, body, sourceText, receivedAt));
        }

        if (orders.Count == 0)
        {
            var bodyOrder = ParseBodyOrder(request, sourceDate, rawPo, body, sourceText, masterSiteNames ?? [], globalWarnings);
            if (bodyOrder is not null)
                orders.Add(bodyOrder);
        }

        if (orders.Count == 0)
            return new EmailIntakeParseResult([], globalWarnings, "No transport order could be identified from this email.");

        orders = orders
            .Select(order => ApplyPrecedenceOverrides(order, request, body, masterSiteNames ?? []))
            .ToList();
        return new EmailIntakeParseResult(orders, globalWarnings, null);
    }

    private static List<ParsedEmailOrder> ParseBarfootsWaitroseWaveBody(
        MailboxEmailIntakeRequest request,
        string body,
        DateTimeOffset receivedAt)
    {
        var source = $"{request.Subject}\n{request.SenderAddress}\n{body}";
        if (!source.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("WAVE", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("pallet", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("PO", StringComparison.OrdinalIgnoreCase) ||
            (!(request.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) &&
             !source.Contains("Barfoots", StringComparison.OrdinalIgnoreCase)))
            return [];

        var deliveryDate = ExtractDate(request.Subject ?? string.Empty, receivedAt)
                           ?? ExtractDateAfter(body, @"depot\s+date[^0-9\r\n]*");
        if (deliveryDate is null) return [];

        var explicitCollectionDate = ExtractDateAfter(body, @"collection(?:\s+date)?[^0-9\r\n]*");
        var collectionDate = explicitCollectionDate ?? LocalDate(receivedAt);
        var matches = Regex.Matches(
                body,
                @"(?<depot>Aylesford|Bracknell|Brinklow|Leyland)\s+WAVE\s+(?<wave>\d+)(?:\s+from\s+(?<collection>Sefter|Leythorne))?\s+(?<qty>\d{1,3})\s+pallets?\s+PO\s+(?<po>[A-Z0-9/-]+)",
                RegexOptions.IgnoreCase)
            .Cast<Match>();
        var plainWaveMatches = Regex.Matches(body,
                @"(?<depot>Aylesford|Bracknell|Brinklow|Leyland)\s+WAVE\s+(?<wave>\d+)\s+(?<qty>\d{1,3})\s+pallets?\s+PO\s+(?<po>[A-Z0-9/-]+)",
                RegexOptions.IgnoreCase).Cast<Match>();
        matches = matches.Concat(plainWaveMatches)
            .GroupBy(match => match.Index)
            .Select(group => group.First());
        var rows = new List<(string Depot, int Wave, string Collection, int Pallets, string Po)>();
        string? currentCollection = null;
        foreach (var match in matches)
        {
            if (match.Groups["collection"].Success)
                currentCollection = CleanSourceLine(match.Groups["collection"].Value)
                    .Replace("Barfoots ", string.Empty, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(currentCollection)) continue;
            var pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
            if (pallets <= 0) continue;
            rows.Add((
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["depot"].Value.ToLowerInvariant()),
                int.Parse(match.Groups["wave"].Value, CultureInfo.InvariantCulture),
                currentCollection,
                pallets,
                match.Groups["po"].Value.Trim().ToUpperInvariant()));
        }

        return rows.Select(row =>
        {
            var warnings = new List<string>();
            if (explicitCollectionDate is null)
                warnings.Add("Collection date inferred as the email received date from the Barfoots Waitrose wave template; confirm if collection occurs on a different day.");
            var order = BuildStructuredOrder(
                request,
                $"barfoots-waitrose-{NormaliseKey(row.Depot)}-wave-{row.Wave}-{NormaliseKey(row.Po)}",
                "WAITROSE", row.Po, collectionDate, deliveryDate.Value, row.Pallets,
                row.Collection, row.Depot, $"Waitrose Wave {row.Wave} depot delivery", warnings);
            var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(order.Payload.GetRawText()) ?? [];
            payload["wave"] = row.Wave;
            payload["overnightRoute"] = row.Wave >= 3;
            payload["routeTiming"] = row.Wave >= 3 ? "Overnight" : "SameDay";
            if (row.Wave >= 3)
            {
                payload["requestedTime"] = "17:00";
                payload["driverInstructions"] = $"Order type: Waitrose Wave {row.Wave} depot delivery · PM overnight departure · PO ref: {row.Po}";
            }
            return new ParsedEmailOrder(order.SourceKey, order.NaturalKey, JsonSerializer.SerializeToElement(payload), order.Warnings);
        }).ToList();
    }

    private static ParsedEmailOrder ApplyPrecedenceOverrides(
        ParsedEmailOrder order,
        MailboxEmailIntakeRequest request,
        string body,
        IReadOnlyCollection<string> masterSiteNames)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(order.Payload.GetRawText()) ?? [];
        var warnings = order.Warnings.ToList();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var receivedAt = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        if (string.IsNullOrWhiteSpace(PayloadText(payload, "stallNumber")) &&
            (string.Equals(PayloadText(payload, "customerCode"), "BARFOOTS", StringComparison.OrdinalIgnoreCase) ||
             Regex.IsMatch($"{request.Subject}\n{body}", @"\bBarfoots\b", RegexOptions.IgnoreCase)))
            payload["stallNumber"] = "Barfoots";

        var collectionLabel = ExtractLabelValue(body, "collection", "collect", "pickup");
        var collectFrom = ExtractMatch(CollectFromRegex, body, "site");
        var explicitCollection = ExtractBodyCollectionPoint(body)
            ?? (!string.IsNullOrWhiteSpace(collectFrom) ? CleanSourceLine(collectFrom) : ExtractCollectionSiteFromLabel(collectionLabel));

        // Summer Berry TSBC/CO-OP emails sometimes repeat the destination as
        // “Collect from” in the body. The sender/master-data mapping is authoritative:
        // Summer Berry is the collection site and TSBC CO-OP is the destination.
        var senderCollectionSite = InferCollectionSiteFromSender(request.SenderAddress);
        var isSummerBerryTsbcCoop = string.Equals(senderCollectionSite, "Summer Berry", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch($"{request.Subject}\n{body}", @"\bTSBC\s*[- ]?\s*CO[- ]?OP\b", RegexOptions.IgnoreCase);
        if (isSummerBerryTsbcCoop)
        {
            explicitCollection = senderCollectionSite;
            payload["stallNumber"] = "TSBC CO-OP";
        }
        if (explicitCollection is not null && Regex.IsMatch(explicitCollection, @"^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}", RegexOptions.IgnoreCase))
            explicitCollection = null;
        if (!string.IsNullOrWhiteSpace(explicitCollection) && !order.SourceKey.StartsWith("barfoots-waitrose-", StringComparison.OrdinalIgnoreCase))
        {
            payload["sellerName"] = explicitCollection;
            sources["collectionSite"] = "body.explicit";
            warnings.RemoveAll(x => x.Contains("Collection site was not explicit", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Collection site inferred as ", StringComparison.OrdinalIgnoreCase));
        }
        else sources["collectionSite"] = "template-or-fallback";

        var address = ExtractLabelBlock(body, "addressofdelivery", "adressofdelivery", "deliveryaddress", "deliverto", "destination", "shipto");
        var masterSiteMention = FindMasterSiteMention($"{request.Subject}\n{body}", masterSiteNames);
        var explicitDestination = CleanDeliveryAddressForSite(address)
            ?? DeliverySiteName(ExtractMatch(DeliveryToRegex, body, "site"))
            ?? masterSiteMention;
        if (!string.IsNullOrWhiteSpace(explicitDestination))
        {
            payload["stallNumber"] = explicitDestination;
            if (!string.IsNullOrWhiteSpace(address)) payload["deliveryAddress"] = address;
            sources["deliverySite"] = "body.explicit";
            warnings.RemoveAll(x => x.Contains("destination was not explicit", StringComparison.OrdinalIgnoreCase));
        }
        else sources["deliverySite"] = "template-or-subject";

        var collectionDate = ParseDateText(collectionLabel, receivedAt)
                             ?? ExtractDateAfter(body, @"collection(?:\s+date)?[^.\r\n]*?")
                             ?? ExtractDateAfter(body, @"collect[^.\r\n]*?");
        if (collectionDate is not null) payload["collectionDate"] = collectionDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sources["collectionDate"] = collectionDate is null ? "template-or-subject" : "body.explicit";

        var deliveryLabel = ExtractLabelValue(body, "deliverydate", "depotdate", "deliver", "delivery");
        var deliveryDate = ParseDateText(deliveryLabel, receivedAt)
                           ?? ExtractDateAfter(body, @"depot\s+date[^.\r\n]*?")
                           ?? ExtractDateAfter(body, @"delivery\s+date[^.\r\n]*?");
        if (deliveryDate is not null) payload["deliveryDate"] = deliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sources["deliveryDate"] = deliveryDate is null ? "template-or-subject" : "body.explicit";

        var collectionTime = NormaliseTime(ExtractTime(collectionLabel)
            ?? ExtractMatch(CollectionTimeRegex, body, "time")
            ?? ExtractMatch(ReadyForCollectionTimeRegex, body, "time"));
        if (!string.IsNullOrWhiteSpace(collectionTime)) payload["requestedTime"] = collectionTime;
        sources["collectionTime"] = string.IsNullOrWhiteSpace(collectionTime) ? "template-or-fallback" : "body.explicit";

        var explicitCustomer = CleanCustomerName(ExtractLabelValue(body, "customer"));
        var masterSite = masterSiteMention;
        var masterCustomer = string.IsNullOrWhiteSpace(masterSite) ? null : InferCustomerCode(masterSite, null, masterSite);
        if (string.Equals(masterCustomer, "EMAIL", StringComparison.OrdinalIgnoreCase)) masterCustomer = null;
        var bodySignal = DetectKnownSignal(body, []);
        var subjectSignal = DetectKnownSignal(request.Subject ?? string.Empty, []);
        var existingCustomer = PayloadText(payload, "customerCode");
        var senderCustomer = SenderAddressMatchesDomain(request.SenderAddress, "langmeadherbs.co.uk") ||
            SenderAddressMatchesDomain(request.SenderAddress, "langmeadfarms.co.uk")
            ? "LANGMEADS" : null;
        var resolvedCustomer = !string.IsNullOrWhiteSpace(explicitCustomer) ? CustomerCode(explicitCustomer)
            : !string.IsNullOrWhiteSpace(masterCustomer) ? masterCustomer
            : senderCustomer ?? bodySignal?.CustomerCode
            ?? (IsTemplateSource(order.SourceKey) && !string.Equals(existingCustomer, "EMAIL", StringComparison.OrdinalIgnoreCase) ? existingCustomer : null)
            ?? subjectSignal?.CustomerCode
            ?? existingCustomer;
        if (!string.IsNullOrWhiteSpace(resolvedCustomer)) payload["customerCode"] = resolvedCustomer;
        sources["customer"] = !string.IsNullOrWhiteSpace(explicitCustomer) ? "body.explicit"
            : !string.IsNullOrWhiteSpace(masterCustomer) ? "master-data.body-site"
            : senderCustomer is not null ? "sender.domain"
            : bodySignal is not null ? "template.body-signal"
            : IsTemplateSource(order.SourceKey) ? "template" : subjectSignal is not null ? "subject" : "fallback";

        warnings = warnings.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        payload["intakeWarnings"] = warnings;
        payload["intakeConfidence"] = PrecedenceConfidence(warnings);
        payload["intakeFieldSources"] = sources;
        var naturalKey = BuildPrecedenceNaturalKey(order, request, payload);
        payload["intakeNaturalKey"] = naturalKey;
        return new ParsedEmailOrder(order.SourceKey, naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static IEnumerable<ParsedEmailOrder> ParseStructuredBodyOrders(
        MailboxEmailIntakeRequest request,
        DateOnly? sourceDate,
        string? rawPo,
        string body,
        string sourceText,
        DateTimeOffset receivedAt)
    {
        var hallHunter = ParseHallHunterDirectDepot(request, rawPo, body, sourceText, receivedAt);
        if (hallHunter.Count > 0) return hallHunter;

        var barfootsWaitrose = ParseBarfootsWaitroseWaveBody(request, body, receivedAt);
        if (barfootsWaitrose.Count > 0) return barfootsWaitrose;

        var labelled = ParseLabelledBodyOrder(request, rawPo, body, sourceText, receivedAt);
        if (labelled is not null) return [labelled];

        var doubleHWaitrose = ParseDoubleHWaitroseColumnTable(request, rawPo, body, sourceText, receivedAt);
        if (doubleHWaitrose.Count > 0) return doubleHWaitrose;

        var depotSplit = ParseAndoverAvonmouthSplit(request, rawPo, body, receivedAt);
        if (depotSplit.Count > 0) return depotSplit;

        var waitrose = ParseWaitroseDepotTable(request, rawPo, body, sourceText, sourceDate, receivedAt);
        if (waitrose.Count > 0) return waitrose;

        var internalMorrisons = ParseInternalMorrisonsCollections(request, rawPo, body, receivedAt);
        if (internalMorrisons.Count > 0) return internalMorrisons;

        var vitacressLeyland = ParseVitacressWaitroseLeyland(request, rawPo, body, receivedAt);
        if (vitacressLeyland.Count > 0) return vitacressLeyland;

        var additionalMarket = ParsePmTransportAdditionalMarket(request, rawPo, body, receivedAt);
        if (additionalMarket.Count > 0) return additionalMarket;

        var simpleSplit = ParseSimpleSplitBody(request, rawPo, body, receivedAt);
        return simpleSplit;
    }

    private static ParsedEmailOrder? ParseLabelledBodyOrder(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        string sourceText,
        DateTimeOffset receivedAt)
    {
        var explicitCustomer = ExtractLabelValue(body, "customer");
        var explicitCollection = ExtractBodyCollectionPoint(body)
                                 ?? ExtractLabelValue(body, "collection", "collect", "pickup");
        if (explicitCollection is not null && Regex.IsMatch(explicitCollection, @"^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}", RegexOptions.IgnoreCase))
            explicitCollection = null;
        var explicitDeliveryDate = ExtractLabelValue(body, "deliverydate", "depotdate", "deliver", "delivery");
        var deliveryAddress = ExtractLabelBlock(body, "addressofdelivery", "adressofdelivery", "deliveryaddress", "deliverto", "destination", "shipto");
        var explicitPallets = ExtractInt(LabelledQuantityRegex, body, "qty")
                              ?? ExtractInt(PalletQuantityRegex, body, "qty")
                              ?? ExtractInt(TotalPalletsRegex, body, "qty");

        var hasStrongLabel = !string.IsNullOrWhiteSpace(explicitCustomer)
                             || !string.IsNullOrWhiteSpace(explicitCollection)
                             || !string.IsNullOrWhiteSpace(explicitDeliveryDate)
                             || !string.IsNullOrWhiteSpace(deliveryAddress);
        if (!hasStrongLabel) return null;

        var collectionDate = ExtractDate(explicitCollection ?? string.Empty, receivedAt)
                             ?? ExtractDateAfter(body, @"collection[^.\r\n]*?")
                             ?? ExtractDateAfter(body, @"collect[^.\r\n]*?");
        var deliveryDate = ExtractDate(explicitDeliveryDate ?? string.Empty, receivedAt)
                           ?? ExtractDateAfter(body, @"depot\s+date[^.\r\n]*?")
                           ?? ExtractDateAfter(body, @"delivery\s+date[^.\r\n]*?")
                           ?? ExtractDate(request.Subject ?? string.Empty, receivedAt);
        if (collectionDate is null && deliveryDate is null) return null;

        collectionDate ??= deliveryDate;
        deliveryDate ??= collectionDate;
        if (collectionDate is null || deliveryDate is null) return null;

        var collectionTime = NormaliseTime(ExtractTime(explicitCollection) ?? ExtractMatch(CollectionTimeRegex, body, "time"));
        var deliveryTime = NormaliseTime(ExtractMatch(DeliveryDeadlineRegex, body, "time"));
        var deliveryTimeConstraint = string.IsNullOrWhiteSpace(deliveryTime) ? null : "Not later than";
        var collectionSite = explicitCollection is not null ? CleanSourceLine(explicitCollection)
                             : InferCollectionSiteFromSender(request.SenderAddress)
                             ?? InferCollectionSite(request.Subject, body, InferJobType(request.Subject, body))
                             ?? (Regex.IsMatch(body, @"\bBarfoots\b", RegexOptions.IgnoreCase) ? "Barfoots" : null);
        var destination = CleanDeliveryAddressForSite(deliveryAddress)
                          ?? InferDestination(request.Subject, body, "Delivery");
        var customerDisplay = CleanCustomerName(explicitCustomer)
                              ?? InferCustomerCodeFromMasterOrSubject(request.Subject, sourceText, request.SenderAddress, destination);
        var customerCode = CustomerCode(customerDisplay);

        if (explicitPallets is null or <= 0) return null;

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(rawPo))
            warnings.Add("No customer PO/reference was found; a stable email reference was generated and should be checked before approval.");
        if (string.IsNullOrWhiteSpace(collectionSite))
            warnings.Add("Collection site was not explicit and no sender/domain mapping matched.");
        else if (!BodyMentionsCollectionSite(body, collectionSite))
            warnings.Add($"Collection site inferred as {collectionSite} from verified sender/domain mapping.");
        if (string.IsNullOrWhiteSpace(destination))
            warnings.Add("Delivery address/destination was not explicit in the email.");

        var baseReference = rawPo ?? StableEmailReference(request.MessageId);
        var orderReference = BuildRowReference(baseReference, customerCode, destination ?? customerDisplay, deliveryDate.Value, 1);
        var naturalKey = $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{customerCode}|{collectionDate:yyyy-MM-dd}|{deliveryDate:yyyy-MM-dd}|{NormaliseKey(collectionSite)}|{NormaliseKey(destination)}|{explicitPallets.Value}";
        var instructions = BuildInstructions(
            rawPo,
            collectionTime,
            null,
            ExtractTemperature(body),
            request,
            null,
            warnings,
            "Delivery",
            deliveryTime,
            deliveryTimeConstraint,
            deliveryAddress);

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerCode"] = customerCode,
            ["collectionDate"] = collectionDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = explicitPallets.Value,
            ["sellerName"] = collectionSite,
            ["marketName"] = customerDisplay,
            ["stallNumber"] = destination,
            ["deliveryAddress"] = deliveryAddress,
            ["driverInstructions"] = instructions,
            ["customerPo"] = rawPo,
            ["requestedTime"] = collectionTime,
            ["deliveryRequestedTime"] = deliveryTime,
            ["deliveryTimeConstraint"] = deliveryTimeConstraint,
            ["jobType"] = "Delivery",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = ConfidenceFor(warnings),
            ["intakeWarnings"] = warnings
        };

        return new ParsedEmailOrder("labelled-body-1", naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static List<ParsedEmailOrder> ParseDoubleHWaitroseColumnTable(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        string sourceText,
        DateTimeOffset receivedAt)
    {
        if (!string.Equals(SenderDomain(request.SenderAddress), "doubleh.co.uk", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.Contains("Double H", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!sourceText.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ||
            !sourceText.Contains("Depot date", StringComparison.OrdinalIgnoreCase) ||
            !sourceText.Contains("Trolleys", StringComparison.OrdinalIgnoreCase))
            return [];

        var deliveryDate = ExtractDateAfter(sourceText, @"depot\s+date[^0-9\r\n]*")
                           ?? ExtractDate(request.Subject ?? string.Empty, receivedAt);
        var collectionDate = ExtractDateAfter(sourceText, @"collection\s+today[^0-9\r\n]*")
                             ?? ExtractDateAfter(sourceText, @"collection[^0-9\r\n]*")
                             ?? LocalDate(receivedAt);
        if (deliveryDate is null) return [];

        var depotLine = FindLine(body, line => line.Contains("Aylesford", StringComparison.OrdinalIgnoreCase) &&
                                             line.Contains("Bracknell", StringComparison.OrdinalIgnoreCase));
        var refLine = FindLine(body, line => line.Contains("Order Ref", StringComparison.OrdinalIgnoreCase));
        var trolleyLine = FindLine(body, line => line.Contains("Trolleys", StringComparison.OrdinalIgnoreCase));
        if (depotLine is null || refLine is null || trolleyLine is null) return [];

        var depots = SplitTableLine(depotLine).Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
        var refs = SplitTableLine(refLine).Skip(1).ToList();
        var quantities = SplitTableLine(trolleyLine).Skip(1).ToList();
        var count = Math.Min(depots.Count, Math.Min(refs.Count, quantities.Count));
        var rows = new List<(string Depot, string? Po, int Trolleys)>();
        for (var index = 0; index < count; index++)
        {
            if (!int.TryParse(Regex.Match(quantities[index], @"\d+").Value, out var trolleys) || trolleys <= 0)
                continue;
            if (depots[index].Equals("TOTAL", StringComparison.OrdinalIgnoreCase))
                continue;
            rows.Add((CleanSourceLine(depots[index]), string.IsNullOrWhiteSpace(refs[index]) ? rawPo : refs[index].Trim(), trolleys));
        }

        return rows
            .Select((row, index) => BuildStructuredOrder(
                request,
                $"doubleh-waitrose-{NormaliseKey(row.Depot)}-{index + 1}",
                "WAITROSE",
                row.Po,
                collectionDate,
                deliveryDate.Value,
                row.Trolleys,
                "Double H",
                row.Depot,
                "Waitrose depot delivery",
                []))
            .ToList();
    }

    private static List<ParsedEmailOrder> ParseAttachmentDrivenRows(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        List<object?[]> rows,
        string body)
    {
        var source = string.Join("\n", request.Subject, request.SenderAddress, attachment.Name, body);
        var isNisa = source.Contains("NISA", StringComparison.OrdinalIgnoreCase) &&
                     (source.Contains("pallet booking", StringComparison.OrdinalIgnoreCase) ||
                      source.Contains("pallet bookings", StringComparison.OrdinalIgnoreCase) ||
                      source.Contains("Nissa", StringComparison.OrdinalIgnoreCase));
        var isCoop = source.Contains("Co-op", StringComparison.OrdinalIgnoreCase) ||
                     source.Contains("Coop", StringComparison.OrdinalIgnoreCase) ||
                     source.Contains("CO OP", StringComparison.OrdinalIgnoreCase);
        if (!isNisa && !isCoop) return [];

        var headerIndex = rows.FindIndex(row =>
        {
            var keys = row.Select(value => NormaliseKey(CellText(value))).Where(value => value.Length > 0).ToHashSet();
            return (keys.Contains("pallets") || keys.Contains("palletcount") || keys.Contains("quantity")) &&
                   (keys.Contains("location") || keys.Contains("deliverylocation") || keys.Contains("depot") ||
                    keys.Contains("destination") || keys.Contains("deliverysite"));
        });
        if (headerIndex < 0) return [];

        var columns = HeaderMap(rows[headerIndex]);
        var dateIndex = FindColumn(columns, "deliverydate", "date", "bookingdate", "depotdate");
        var destinationIndex = FindColumn(columns, "deliverylocation", "location", "depotdescription", "depot", "destination", "deliverysite");
        var attachmentPalletsIndex = FindColumn(columns, "pallets", "palletcount", "quantity", "qty");
        if (dateIndex < 0 || destinationIndex < 0) return [];

        var bodyPallets = ExtractInt(TotalPalletsRegex, body, "qty")
                          ?? ExtractInt(LabelledQuantityRegex, body, "qty")
                          ?? ExtractInt(PalletQuantityRegex, body, "qty");
        if (isNisa && bodyPallets is not > 0)
            return [];

        var collectionSite = ExtractBodyCollectionPoint(body)
                             ?? InferCollectionSiteFromSender(request.SenderAddress)
                             ?? InferCollectionSite(request.Subject, body, "Collection");
        var results = new List<ParsedEmailOrder>();
        for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var destination = CleanSourceLine(CellText(row, destinationIndex));
            if (string.IsNullOrWhiteSpace(destination)) continue;

            var deliveryDate = ParseDateText(CellText(row, dateIndex), request.ReceivedAtUtc ?? DateTimeOffset.UtcNow);
            if (deliveryDate is null) continue;

            var pallets = isNisa
                ? bodyPallets!.Value
                : attachmentPalletsIndex >= 0 ? CellInt(row, attachmentPalletsIndex) : null;
            if (pallets is not > 0) continue;

            var warnings = new List<string>
            {
                isNisa
                    ? "NISA booking locations and delivery dates were read from the attachment; pallet quantity was taken from the email body by rule."
                    : "Co-op delivery date, location and pallet quantity were read from the attachment; collection point was taken from the email body by rule."
            };
            if (isNisa && attachmentPalletsIndex >= 0 && CellInt(row, attachmentPalletsIndex) != bodyPallets)
                warnings.Add("The NISA attachment pallet quantity differed from the email body; the email body quantity was used.");
            if (string.IsNullOrWhiteSpace(collectionSite))
                warnings.Add("Collection site was not explicit in the email body and needs review.");

            results.Add(BuildStructuredOrder(
                request,
                $"attachment-driven-{(isNisa ? "nisa" : "coop")}-{NormaliseKey(destination)}-{rowIndex}",
                isNisa ? "BARFOOTS" : "COOP",
                ExtractPo($"{request.Subject}\n{body}"),
                deliveryDate.Value,
                deliveryDate.Value,
                pallets!.Value,
                collectionSite,
                destination,
                isNisa ? "NISA pallet booking" : "Co-op attachment booking",
                warnings));
        }
        return results;
    }

    private static string? ExtractBodyCollectionPoint(string body)
    {
        var labelled = Regex.Match(
            body,
            @"(?im)^\s*(?:collection\s+point|collection\s+from|collect\s+from|pickup)\s*[:=-]\s*(?<site>[^\r\n.]{2,120})",
            RegexOptions.IgnoreCase);
        if (labelled.Success)
        {
            var labelledSite = CleanSourceLine(labelled.Groups["site"].Value);
            if (Regex.IsMatch(labelledSite, @"\bSefter\b", RegexOptions.IgnoreCase))
                return "Barfoots Sefter";
            if (Regex.IsMatch(labelledSite, @"\bLeythorne\b", RegexOptions.IgnoreCase))
                return "Barfoots Leythorne";
            return labelledSite;
        }

        return null;
    }

    private static List<ParsedEmailOrder> ParseInternalMorrisonsCollections(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        DateTimeOffset receivedAt)
    {
        if (!(request.SenderAddress ?? string.Empty).EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!body.Contains("Collections list", StringComparison.OrdinalIgnoreCase) ||
            !body.Contains("Morrisons-Bridgwater", StringComparison.OrdinalIgnoreCase))
            return [];

        var collectionDate = body.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)
            ? LocalDate(receivedAt).AddDays(1)
            : ExtractDate(body, receivedAt) ?? LocalDate(receivedAt);
        var collectionTime = NormaliseTime(ExtractMatch(new Regex(@"\bfirst\s+collection\s+site\s+for\s+(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?)", RegexOptions.IgnoreCase), body, "time"));
        var destination = "Morrisons-Bridgwater";
        var po = ExtractMatch(new Regex(@"Morrisons-Bridgwater\s+booking\s+ref\s*[:=-]\s*(?<po>[A-Z0-9/-]+)", RegexOptions.IgnoreCase), body, "po")
                 ?? rawPo;
        var rows = Regex.Matches(body, @"(?im)^\s*(?<collection>Merston|Runcton|Selsey|Drayton)\s+(?<qty>\d{1,3})p?\s+Morrisons-Bridgwater\s*$")
            .Cast<Match>()
            .Select(match => (
                Collection: CleanSourceLine(match.Groups["collection"].Value),
                Pallets: int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture)))
            .ToList();
        if (rows.Count == 0) return [];

        return rows
            .Select((row, index) =>
            {
                var warnings = string.IsNullOrWhiteSpace(po)
                    ? ["No booking reference was found; a stable email reference was generated and should be checked before approval."]
                    : Array.Empty<string>();
                return BuildStructuredOrder(
                    request,
                    $"internal-morrisons-bridgwater-{NormaliseKey(row.Collection)}-{index + 1}",
                    "MORRISONS",
                    po,
                    collectionDate,
                    collectionDate,
                    row.Pallets,
                    row.Collection,
                    destination,
                    "Morrisons multi-collection delivery",
                    warnings,
                    collectionTime);
            })
            .ToList();
    }

    private static List<ParsedEmailOrder> ParseVitacressWaitroseLeyland(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        DateTimeOffset receivedAt)
    {
        var sourceText = $"{request.Subject}\n{request.SenderAddress}\n{body}";
        if (!sourceText.Contains("Vitacress", StringComparison.OrdinalIgnoreCase) ||
            !sourceText.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) ||
            !sourceText.Contains("Leyland", StringComparison.OrdinalIgnoreCase))
            return [];

        var match = Regex.Match(
            sourceText,
            @"drop\s+(?<qty>\d{1,3})\s*(?:plts?|pallets?)\s+into\s+(?<collection>Bracknell|Aylesford|Brinklow|Leyland)\s+(?<dateText>tomorrow\s+)?(?<date>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)?(?:[^.\r\n]*?\baround\s+(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?))?[^.\r\n]*?onward\s+delivery\s+to\s+(?<destination>Leyland|Bracknell|Aylesford|Brinklow)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success) return [];

        var pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
        var collection = CleanSourceLine(match.Groups["collection"].Value);
        var destination = CleanSourceLine(match.Groups["destination"].Value);
        var date = match.Groups["date"].Success
            ? ExtractDate(match.Groups["date"].Value, receivedAt)
            : match.Groups["dateText"].Success
                ? LocalDate(receivedAt).AddDays(1)
                : ExtractDate(sourceText, receivedAt) ?? LocalDate(receivedAt);
        if (date is null) return [];

        var time = NormaliseTime(match.Groups["time"].Success ? match.Groups["time"].Value : null);
        var warnings = string.IsNullOrWhiteSpace(rawPo)
            ? ["No customer PO/reference was found; a stable email reference was generated and should be checked before approval."]
            : Array.Empty<string>();

        return
        [
            BuildStructuredOrder(
                request,
                "vitacress-waitrose-leyland",
                "WAITROSE",
                rawPo,
                date.Value,
                date.Value,
                pallets,
                collection,
                destination,
                "Waitrose onward depot delivery",
                warnings,
                time)
        ];
    }

    private static List<ParsedEmailOrder> ParsePmTransportAdditionalMarket(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        DateTimeOffset receivedAt)
    {
        var sourceText = $"{request.Subject}\n{request.SenderAddress}\n{body}";
        if (!sourceText.Contains("@PMTransport.co.uk", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.Contains("pmtransport.co.uk", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!sourceText.Contains("additional market", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.Contains("sunstar", StringComparison.OrdinalIgnoreCase))
            return [];

        var match = Regex.Match(
            sourceText,
            @"another\s+(?<qty>\d{1,3})\s*(?:pt|plts?|pallets?)\s+(?<collection>sunstar)[^\r\n.]*?(?:spit|spitalfields)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return [];

        var pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
        var readyTime = NormaliseTime(ExtractMatch(new Regex(@"\bready\s+about\s+(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)", RegexOptions.IgnoreCase), body, "time"));
        var collectionDate = LocalDate(receivedAt);
        var deliveryDate = collectionDate;
        var warnings = new List<string>
        {
            "Short market amendment parsed from email body; check against existing market orders before approval."
        };
        if (string.IsNullOrWhiteSpace(rawPo))
            warnings.Add("No customer PO/reference was found; a stable email reference was generated and should be checked before approval.");
        if (Regex.IsMatch(body, @"\b11\s*(?:pt|plts?|pallets?)\b", RegexOptions.IgnoreCase))
            warnings.Add("Email also mentions 11 pallets may be ready; this was treated as availability information, not an additional order quantity.");

        return
        [
            BuildStructuredOrder(
                request,
                "pmtransport-additional-market-sunstar-spitalfields",
                "PMTRANSPORT",
                rawPo,
                collectionDate,
                deliveryDate,
                pallets,
                "Sunstar",
                "Spitalfields",
                "Additional market delivery",
                warnings,
                readyTime)
        ];
    }

    private static IEnumerable<ParsedEmailOrder> ParseWorkbook(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        DateOnly? emailDate,
        string? rawPo,
        string body)
    {
        var bytes = DecodeBase64(attachment.EffectiveContentBase64!);
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        var results = new List<ParsedEmailOrder>();
        var sheetIndex = 0;
        do
        {
            sheetIndex++;
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                    values[i] = reader.GetValue(i);
                rows.Add(values);
            }

            var attachmentDrivenRows = ParseAttachmentDrivenRows(request, attachment, rows, body);
            if (attachmentDrivenRows.Count > 0)
            {
                results.AddRange(attachmentDrivenRows);
                continue;
            }

            var legacyRows = ParseKnownWaitroseWorkbookRows(request, attachment, rows);
            if (legacyRows.Count > 0)
            {
                results.AddRange(legacyRows.Select((row, index) =>
                {
                    var warnings = new List<string>();
                    if (row.IsAmendment) warnings.Add("Source workbook is marked AMENDED; match this row to the existing PO before approval.");
                    return BuildStructuredOrder(
                        request,
                        $"waitrose-legacy-{sheetIndex}-{NormaliseKey(row.Depot)}-{index + 1}",
                        "WAITROSE",
                        row.Po,
                        row.CollectionDate,
                        row.DeliveryDate,
                        row.Pallets,
                        InferLegacyWaitroseCollectionSite(request, attachment),
                        row.Depot,
                        row.IsAmendment ? "Waitrose amended depot movement" : "Waitrose depot movement",
                        warnings,
                        row.ReadyTime?.ToString("HH:mm", CultureInfo.InvariantCulture));
                }));
                continue;
            }

            var wholesaleRows = ParseBarfootsWholesaleMarketRows(request, attachment.Name, reader.Name, rows);
            if (wholesaleRows.Count > 0)
            {
                results.AddRange(wholesaleRows);
                continue;
            }

            var headerIndex = rows.FindIndex(IsBookingHeader);
            if (headerIndex < 0)
                continue;

            var header = rows[headerIndex];
            var columns = HeaderMap(header);
            var collectionIndex = FindColumn(columns, "collectionsite", "collection", "collectfrom");
            var dateIndex = FindColumn(columns, "date", "deliverydate", "bookingdate");
            var depotIndex = FindColumn(columns, "depotdescription", "depot", "destination", "deliverysite");
            var palletsIndex = FindColumn(columns, "pallets", "pallet", "qty", "quantity");
            var requestTimeIndex = FindColumn(columns, "requesttime", "requestedtime", "bookingtime", "deliverytime");
            var availableTimeIndex = FindColumn(columns, "availabletime", "collectiontime", "readytime");

            if (depotIndex < 0 || palletsIndex < 0)
                continue;

            for (var rowIndex = headerIndex + 1; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var depot = CellText(row, depotIndex);
                var pallets = CellInt(row, palletsIndex);
                if (string.IsNullOrWhiteSpace(depot) || pallets is null or <= 0)
                    continue;

                var collection = CellText(row, collectionIndex);
                var rowDate = CellDate(row, dateIndex) ?? emailDate;
                if (rowDate is null)
                    continue;

                var requestedTime = CellTime(row, requestTimeIndex);
                var availableTime = CellTime(row, availableTimeIndex);
                var customer = InferCustomerCode(request.Subject, request.SenderAddress, depot);
                var destination = CleanDestination(depot, customer);
                var warnings = new List<string>();
                if (string.IsNullOrWhiteSpace(rawPo))
                    warnings.Add("No customer PO/reference was found in the email; a stable email reference was generated.");
                if (string.IsNullOrWhiteSpace(collection))
                    warnings.Add("Collection site was blank in the source workbook.");

                var baseReference = rawPo ?? StableEmailReference(request.MessageId);
                var orderReference = BuildRowReference(baseReference, customer, destination, rowDate.Value, rowIndex + 1);
                var naturalKey = NaturalKey(request, customer, destination, rowDate.Value);
                var instructions = BuildInstructions(
                    rawPo,
                    requestedTime,
                    availableTime,
                    ExtractTemperature(body),
                    request,
                    attachment.Name,
                    warnings,
                    "Delivery");

                var payload = new Dictionary<string, object?>
                {
                    ["poNumber"] = orderReference,
                    ["customerCode"] = customer,
                    ["collectionDate"] = rowDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["deliveryDate"] = rowDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["pallets"] = pallets.Value,
                    ["sellerName"] = collection,
                    ["marketName"] = customer,
                    ["stallNumber"] = destination,
                    ["driverInstructions"] = instructions,
                    ["customerPo"] = rawPo,
                    ["requestedTime"] = requestedTime,
                    ["availableTime"] = availableTime,
                    ["jobType"] = "Delivery",
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
                    ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
                    ["intakeWarnings"] = warnings
                };

                results.Add(new ParsedEmailOrder(
                    $"sheet-{sheetIndex}-row-{rowIndex + 1}",
                    naturalKey,
                    JsonSerializer.SerializeToElement(payload),
                    warnings));
            }
        }
        while (reader.NextResult());

        return results;
    }

    internal static List<ParsedEmailOrder> ParseBarfootsWholesaleMarketRows(
        MailboxEmailIntakeRequest request,
        string? attachmentName,
        string? sheetName,
        IReadOnlyList<object?[]> rows)
    {
        var source = $"{request.Subject}\n{request.SenderAddress}\n{attachmentName}";
        if (!source.Contains("Wholesale", StringComparison.OrdinalIgnoreCase) ||
            !source.Contains("Market", StringComparison.OrdinalIgnoreCase) ||
            (!(request.SenderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase) &&
             !source.Contains("Barfoots", StringComparison.OrdinalIgnoreCase)))
            return [];

        var headerIndex = rows.ToList().FindIndex(row =>
        {
            var keys = row.Select(value => NormaliseKey(CellText(value))).ToHashSet();
            return keys.Contains("market") && keys.Contains("customer") &&
                   keys.Any(key => key is "deliveryaddess" or "deliveryaddress") &&
                   keys.Any(key => key is "noofpallets" or "pallets");
        });
        if (headerIndex < 0) return [];

        var columns = HeaderMap(rows[headerIndex]);
        var marketIndex = FindColumn(columns, "market");
        var customerIndex = FindColumn(columns, "customer");
        var addressIndex = FindColumn(columns, "deliveryaddess", "deliveryaddress");
        var dateIndex = FindColumn(columns, "deliverydate", "date");
        var timeIndex = FindColumn(columns, "deliverytime");
        var temperatureIndex = FindColumn(columns, "temp", "temperature");
        var palletsIndex = FindColumn(columns, "noofpallets", "pallets");
        var salesOrderIndex = FindColumn(columns, "so", "salesorder");
        if (marketIndex < 0 || customerIndex < 0 || addressIndex < 0 || palletsIndex < 0) return [];

        var results = new List<ParsedEmailOrder>();
        string? collection = null;
        string? market = null;
        DateOnly? deliveryDate = null;
        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var row = rows[rowIndex];
            var first = CellText(row, 0);
            if (first?.StartsWith("COLLECTION ", StringComparison.OrdinalIgnoreCase) == true)
            {
                collection = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(CleanSourceLine(first["COLLECTION ".Length..]).ToLowerInvariant());
                market = null;
                deliveryDate = null;
                continue;
            }
            if (rowIndex <= headerIndex || string.IsNullOrWhiteSpace(collection)) continue;

            var statedMarket = CellText(row, marketIndex);
            if (!string.IsNullOrWhiteSpace(statedMarket)) market = CanonicalWholesaleMarket(statedMarket);
            deliveryDate = CellDate(row, dateIndex) ?? deliveryDate;
            var customer = CellText(row, customerIndex);
            var address = CellText(row, addressIndex);
            var pallets = CellInt(row, palletsIndex);
            if (string.IsNullOrWhiteSpace(market) || string.IsNullOrWhiteSpace(customer) ||
                string.IsNullOrWhiteSpace(address) || deliveryDate is null || pallets is null or <= 0)
                continue;

            var customerPo = CellText(row, salesOrderIndex)?.Replace(".0", string.Empty, StringComparison.Ordinal);
            var deliveryTime = CellText(row, timeIndex);
            var temperature = CellText(row, temperatureIndex);
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(customerPo))
                warnings.Add("Wholesale source row has no sales-order reference; a stable message-and-row reference was generated for review.");
            // Wholesale market work is commonly an overnight PM route. When the
            // workbook supplies delivery only, stage collection on the preceding
            // day so the planner can allocate the departure correctly.
            var collectionDate = deliveryDate.Value.AddDays(-1);
            if (collectionDate.DayOfWeek == DayOfWeek.Sunday)
                warnings.Add($"Overnight collection inferred as Sunday {collectionDate:dd/MM/yyyy} for Monday delivery {deliveryDate.Value:dd/MM/yyyy}; confirm Sunday PM operation before approval.");
            var baseReference = customerPo ?? StableEmailReference(request.MessageId);
            var orderReference = BuildRowReference(baseReference, "BARFOOTS", customer, deliveryDate.Value, rowIndex + 1);
            var naturalKey = $"barfoots|wholesale|{deliveryDate:yyyy-MM-dd}|{NormaliseKey(collection)}|{NormaliseKey(market)}|{NormaliseKey(customer)}|{NormaliseKey(customerPo)}";
            var instructions = string.Join(" · ", new[]
            {
                $"Market: {market}", $"Market customer: {customer}", $"Delivery address: {CleanSourceLine(address)}",
                string.IsNullOrWhiteSpace(deliveryTime) ? null : $"Delivery time: {deliveryTime}",
                string.IsNullOrWhiteSpace(temperature) ? null : $"Temperature: {temperature}°C",
                string.IsNullOrWhiteSpace(customerPo) ? null : $"Sales order: {customerPo}"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            var payload = new Dictionary<string, object?>
            {
                ["poNumber"] = orderReference,
                ["customerCode"] = "BARFOOTS",
                ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["deliveryDate"] = deliveryDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["pallets"] = pallets.Value,
                ["sellerName"] = collection,
                ["marketName"] = market,
                ["stallNumber"] = customer,
                ["deliveryAddress"] = CleanSourceLine(address),
                ["driverInstructions"] = instructions,
                ["customerPo"] = customerPo,
                ["deliveryRequestedTime"] = deliveryTime,
                ["temperature"] = temperature,
                ["jobType"] = "Wholesale market delivery",
                ["sourceMessageId"] = request.MessageId,
                ["sourceInternetMessageId"] = request.InternetMessageId,
                ["sourceSender"] = request.SenderAddress,
                ["sourceSenderName"] = request.SenderName,
                ["sourceSubject"] = request.Subject,
                ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                ["sourceWebLink"] = request.WebLink,
                ["sourceAttachmentName"] = attachmentName,
                ["sourceSheet"] = sheetName,
                ["sourceRow"] = rowIndex + 1,
                ["intakeNaturalKey"] = naturalKey,
                ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
                ["intakeWarnings"] = warnings
            };
            results.Add(new ParsedEmailOrder($"barfoots-wholesale-{NormaliseKey(collection)}-{rowIndex + 1}", naturalKey, JsonSerializer.SerializeToElement(payload), warnings));
        }
        return results;
    }

    private static List<ParsedEmailOrder> ParseAndoverAvonmouthSplit(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        DateTimeOffset receivedAt)
    {
        if (!Regex.IsMatch(body, @"\bAndover\b", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(body, @"\bAvonmouth\b", RegexOptions.IgnoreCase))
            return [];

        var date = ExtractDate(body, receivedAt) ?? ExtractDate(request.Subject ?? string.Empty, receivedAt);
        if (date is null) return [];
        var rows = Regex.Matches(body, @"(?im)(?:(?<qty>\d{1,3})\s*(?:pallets?|plts?)\s*(?:to\s+)?(?<site>Andover|Avonmouth)|(?<site2>Andover|Avonmouth)[^\r\n]{0,80}?\b(?<qty2>\d{1,3})\s*(?:pallets?|plts?)\b)")
            .Cast<Match>()
            .Select(m => (Site: CultureInfo.InvariantCulture.TextInfo.ToTitleCase((m.Groups["site"].Success ? m.Groups["site"].Value : m.Groups["site2"].Value).ToLowerInvariant()),
                          Pallets: int.Parse(m.Groups["qty"].Success ? m.Groups["qty"].Value : m.Groups["qty2"].Value, CultureInfo.InvariantCulture)))
            .Where(x => x.Pallets > 0)
            .GroupBy(x => x.Site, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (rows.Count < 2) return [];

        var customer = sourceTextCustomer(request, body);
        var warnings = new[] { "Split from one source email into separate depot orders; verify attachment evidence before approval." };
        return rows.Select((row, index) => BuildStructuredOrder(
            request, $"depot-split-{NormaliseKey(row.Site)}-{index + 1}", customer, rawPo,
            date.Value, date.Value, row.Pallets, InferCollectionSiteFromSender(request.SenderAddress),
            row.Site, "Depot pallet delivery", warnings)).ToList();

        static string sourceTextCustomer(MailboxEmailIntakeRequest request, string body)
        {
            if ($"{request.Subject}\n{body}".Contains("Waitrose", StringComparison.OrdinalIgnoreCase)) return "WAITROSE";
            if ($"{request.Subject}\n{body}".Contains("Barfoots", StringComparison.OrdinalIgnoreCase)) return "BARFOOTS";
            return CustomerCode(request.SenderName) == "UNKNOWN" ? "EMAIL" : CustomerCode(request.SenderName);
        }
    }

    private static string CanonicalWholesaleMarket(string value)
    {
        var key = NormaliseKey(value);
        if (key.Contains("coventgarden", StringComparison.OrdinalIgnoreCase)) return "New Covent Garden";
        if (key.Contains("spitalfields", StringComparison.OrdinalIgnoreCase) || key.Contains("spitalfieldsmatket", StringComparison.OrdinalIgnoreCase)) return "Spitalfields";
        if (key.Contains("westerninternational", StringComparison.OrdinalIgnoreCase)) return "Western International";
        if (key.Contains("brighton", StringComparison.OrdinalIgnoreCase)) return "Brighton Market";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(CleanSourceLine(value).ToLowerInvariant());
    }

    private static IReadOnlyList<WaitroseLegacyRow> ParseKnownWaitroseWorkbookRows(
        MailboxEmailIntakeRequest request,
        MailboxAttachmentRequest attachment,
        IReadOnlyList<object?[]> rows)
    {
        var name = attachment.Name ?? string.Empty;
        var source = $"{request.Subject}\n{request.SenderAddress}\n{name}";
        if (source.Contains("Vitacress", StringComparison.OrdinalIgnoreCase) || name.Contains("Collections WAITROSE", StringComparison.OrdinalIgnoreCase))
            return WaitroseLegacyWorkbookParser.ParseVitacress(rows);
        if (source.Contains("WR Week", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(name, @"^Week-\d+\.xls", RegexOptions.IgnoreCase))
            return WaitroseLegacyWorkbookParser.ParseApsWeekly(rows, LocalDate(request.ReceivedAtUtc ?? DateTimeOffset.UtcNow));
        return [];
    }

    private static string InferLegacyWaitroseCollectionSite(MailboxEmailIntakeRequest request, MailboxAttachmentRequest attachment)
    {
        var source = $"{request.Subject}\n{request.SenderAddress}\n{attachment.Name}";
        if (source.Contains("Vitacress", StringComparison.OrdinalIgnoreCase)) return "Vitacress Runcton";
        if (source.Contains("apsgroup.uk.com", StringComparison.OrdinalIgnoreCase) || source.Contains("WR Week", StringComparison.OrdinalIgnoreCase)) return "APS Chichester Packhouse";
        return "Collection site to confirm";
    }

    private static ParsedEmailOrder? ParseBodyOrder(
        MailboxEmailIntakeRequest request,
        DateOnly? sourceDate,
        string? rawPo,
        string body,
        string sourceText,
        IReadOnlyCollection<string> masterSiteNames,
        List<string> globalWarnings)
    {
        if (sourceDate is null)
        {
            globalWarnings.Add("No planning date could be read from the email subject/body.");
            return null;
        }

        var signal = DetectKnownSignal(sourceText, masterSiteNames);
        var customer = signal?.CustomerCode ?? InferCustomerCode(request.Subject, request.SenderAddress, sourceText);
        var jobType = InferJobType(request.Subject, body);
        var collection = InferCollectionSite(request.Subject, body, jobType);
        var destination = InferDestination(request.Subject, body, jobType) ?? signal?.SiteName;
        if (string.IsNullOrWhiteSpace(destination) && Regex.IsMatch(sourceText, @"\b(?:Barfoots|Sefter|Leythorne)\b", RegexOptions.IgnoreCase))
            destination = "Barfoots";
        var pallets = ExtractInt(TotalPalletsRegex, body, "qty")
            ?? ExtractInt(LabelledQuantityRegex, sourceText, "qty")
            ?? ExtractInt(PalletQuantityRegex, sourceText, "qty");
        var requestedTime = NormaliseTime(ExtractMatch(CollectionTimeRegex, body, "time"))
            ?? NormaliseTime(ExtractMatch(ReadyForCollectionTimeRegex, body, "time"));
        var recognisedCustomerOrSite = signal is not null || !string.Equals(customer, "EMAIL", StringComparison.OrdinalIgnoreCase);
        if (!HasEnoughBodyOrderEvidence(rawPo, collection, destination, pallets, requestedTime, jobType, recognisedCustomerOrSite))
        {
            globalWarnings.Add("Email body contained a date but not enough order detail to stage a transport order.");
            return null;
        }
        if (body.Contains("Barfoots Sefter", StringComparison.OrdinalIgnoreCase) && destination is null)
            destination = "Barfoots";
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(rawPo))
            warnings.Add("No customer PO/reference was found; a stable email reference was generated and should be checked before approval.");
        if (string.IsNullOrWhiteSpace(collection))
            warnings.Add("Collection site was not explicit in the email.");
        if (string.IsNullOrWhiteSpace(destination))
            warnings.Add("Delivery/return destination was not explicit in the email.");
        if (pallets is null && !jobType.Contains("Tray", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Pallet quantity was not explicit in the email.");

        var baseReference = rawPo ?? StableEmailReference(request.MessageId);
        var orderReference = BuildRowReference(baseReference, customer, destination ?? collection ?? jobType, sourceDate.Value, 1);
        var naturalKey = NaturalKey(request, customer, destination ?? collection ?? jobType, sourceDate.Value);
        var instructions = BuildInstructions(
            rawPo,
            requestedTime,
            null,
            ExtractTemperature(body),
            request,
            null,
            warnings,
            jobType);

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerCode"] = customer,
            ["collectionDate"] = sourceDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = sourceDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collection,
            ["marketName"] = customer,
            ["stallNumber"] = destination,
            ["driverInstructions"] = instructions,
            ["customerPo"] = rawPo,
            ["requestedTime"] = requestedTime,
            ["jobType"] = jobType,
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = warnings.Count == 0 ? "High" : warnings.Count <= 2 ? "Medium" : "Low",
            ["intakeWarnings"] = warnings
        };

        return new ParsedEmailOrder("body-1", naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static bool HasEnoughBodyOrderEvidence(string? rawPo, string? collection, string? destination, int? pallets, string? requestedTime, string jobType, bool recognisedCustomerOrSite)
    {
        var hasReference = !string.IsNullOrWhiteSpace(rawPo);
        var hasCollection = !string.IsNullOrWhiteSpace(collection);
        var hasDestination = !string.IsNullOrWhiteSpace(destination);
        var hasQuantity = pallets is > 0;
        var hasTime = !string.IsNullOrWhiteSpace(requestedTime);
        if (jobType.Contains("Tray", StringComparison.OrdinalIgnoreCase))
            return hasReference && (hasCollection || hasDestination);

        // A customer/supplier name plus a date is only evidence that the email
        // may need review; it is not enough to create a transport order. Body
        // fallback orders must carry an actual quantity and at least one route
        // side before they are staged.
        return hasQuantity &&
               (hasCollection || hasDestination) &&
               (hasReference || hasTime || recognisedCustomerOrSite);
    }

    private static string? ExtractCollectionSiteFromLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = DateRegex.Replace(value, " ");
        clean = MonthNameDateRegex.Replace(clean, " ");
        clean = Regex.Replace(clean, @"\b(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?\b", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\b(?:today|tomorrow)\b", " ", RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '–', '—', ':');
        return clean.Any(char.IsLetter) && clean.Length >= 3 ? CleanSourceLine(clean) : null;
    }

    private static string? DeliverySiteName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = CleanSourceLine(value).Trim(' ', '-', '–', '—');
        var separator = clean.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) clean = clean[..separator].Trim();
        else
        {
            var comma = clean.IndexOf(',');
            if (comma > 0) clean = clean[..comma].Trim();
        }
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string? FindMasterSiteMention(string body, IReadOnlyCollection<string> masterSiteNames) =>
        masterSiteNames
            .Where(site => !string.IsNullOrWhiteSpace(site) && site.Trim().Length >= 3)
            .OrderByDescending(site => site.Length)
            .FirstOrDefault(site => body.Contains(site.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.Trim();

    private static string? PayloadText(Dictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element)
            return element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : element.ToString();
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static bool IsTemplateSource(string sourceKey) =>
        !sourceKey.StartsWith("body-", StringComparison.OrdinalIgnoreCase) &&
        !sourceKey.StartsWith("labelled-body-", StringComparison.OrdinalIgnoreCase);

    private static string PrecedenceConfidence(IReadOnlyList<string> warnings)
    {
        var hardWarnings = warnings.Count(warning =>
            !warning.StartsWith("Collection site inferred as ", StringComparison.OrdinalIgnoreCase) &&
            !warning.StartsWith("No customer PO/reference", StringComparison.OrdinalIgnoreCase) &&
            !warning.StartsWith("Customer resolved as ", StringComparison.OrdinalIgnoreCase));
        return hardWarnings == 0 ? "High" : hardWarnings <= 2 ? "Medium" : "Low";
    }

    private static string BuildPrecedenceNaturalKey(
        ParsedEmailOrder order,
        MailboxEmailIntakeRequest request,
        Dictionary<string, object?> payload)
    {
        var customer = PayloadText(payload, "customerCode") ?? "EMAIL";
        var customerPo = PayloadText(payload, "customerPo") ?? order.SourceKey;
        var collectionDate = PayloadText(payload, "collectionDate") ?? string.Empty;
        var deliveryDate = PayloadText(payload, "deliveryDate") ?? collectionDate;
        return $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{NormaliseKey(customer)}|{NormaliseKey(customerPo)}|{collectionDate}|{deliveryDate}|{NormaliseKey(PayloadText(payload, "sellerName"))}|{NormaliseKey(PayloadText(payload, "stallNumber"))}";
    }

    private static List<ParsedEmailOrder> ParseHallHunterDirectDepot(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        string sourceText,
        DateTimeOffset receivedAt)
    {
        if (!sourceText.Contains("Hall Hunter", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.Contains("Primafruit", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!sourceText.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.Contains("direct depot delivery", StringComparison.OrdinalIgnoreCase))
            return [];

        var collectionDate = ExtractDateAfter(body, @"collect[^.\r\n]*?") ?? LocalDate(receivedAt);
        var deliveryDate = ExtractDateAfter(body, @"delivery\s+date[^.\r\n]*?") ?? ExtractDate(sourceText, receivedAt) ?? collectionDate;
        var collection = ExtractMatch(new Regex(@"\bfrom\s+(?<site>Hall\s+Hunter)\b", RegexOptions.IgnoreCase), body, "site") ?? "Hall Hunter";
        var po = rawPo ?? ExtractPo(sourceText);
        var depotRows = Regex.Matches(body, @"(?im)(?:^|\n|[*\s])(?<depot>Aylesford|Bracknell|Brinklow|Leyland)\s+(?<qty>\d{1,3})\s+pallets?\b")
            .Cast<Match>()
            .Select(match => (Depot: CleanSourceLine(match.Groups["depot"].Value), Pallets: int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture)))
            .GroupBy(row => row.Depot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        if (depotRows.Count == 0)
        {
            var pallets = ExtractInt(PalletQuantityRegex, body, "qty") ?? ExtractInt(LabelledQuantityRegex, body, "qty");
            if (pallets is > 0) depotRows.Add(("Waitrose", pallets.Value));
        }

        return depotRows
            .Select((row, index) => BuildStructuredOrder(
                request,
                $"hall-hunter-{NormaliseKey(row.Depot)}-{index + 1}",
                "WAITROSE",
                po,
                collectionDate,
                deliveryDate,
                row.Pallets,
                collection,
                row.Depot,
                "Hall Hunter direct depot delivery",
                []))
            .ToList();
    }

    private static List<ParsedEmailOrder> ParseWaitroseDepotTable(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        string sourceText,
        DateOnly? sourceDate,
        DateTimeOffset receivedAt)
    {
        if (!sourceText.Contains("Waitrose", StringComparison.OrdinalIgnoreCase) &&
            !sourceText.Contains("Weightrose", StringComparison.OrdinalIgnoreCase))
            return [];
        if (!sourceText.Contains("Depot", StringComparison.OrdinalIgnoreCase) ||
            !(sourceText.Contains("Pallet count", StringComparison.OrdinalIgnoreCase) || sourceText.Contains("Pallets", StringComparison.OrdinalIgnoreCase)))
            return [];

        var deliveryDate = ExtractDateAfter(sourceText, @"delivery\s+date[^A-Z0-9\r\n]*") ?? sourceDate ?? LocalDate(receivedAt);
        var collection = sourceText.Contains("Hill Brothers", StringComparison.OrdinalIgnoreCase) ||
                         sourceText.Contains("Hills", StringComparison.OrdinalIgnoreCase)
            ? "Hill Brothers"
            : null;
        var rows = Regex.Matches(sourceText, @"(?im)\b(?<depot>AYLESFORD|BRACKNELL|BRINKLOW|LEYLAND)\b\s*\|?\s*(?<po>[A-Z]\d{5}(?:\s*\+\s*[A-Z]\d{5})*)\s*\|?\s*(?<qty>\d{1,3})\b")
            .Cast<Match>()
            .Select(match => (
                Depot: CultureInfo.InvariantCulture.TextInfo.ToTitleCase(match.Groups["depot"].Value.ToLowerInvariant()),
                Po: Regex.Replace(match.Groups["po"].Value, @"\s+", string.Empty),
                Pallets: int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture)))
            .GroupBy(row => $"{row.Depot}|{row.Po}|{row.Pallets}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        return rows
            .Select((row, index) => BuildStructuredOrder(
                request,
                $"waitrose-{NormaliseKey(row.Depot)}-{index + 1}",
                "WAITROSE",
                row.Po ?? rawPo,
                deliveryDate,
                deliveryDate,
                row.Pallets,
                collection,
                row.Depot,
                "Waitrose depot pallet booking",
                string.IsNullOrWhiteSpace(collection) ? ["Collection site was not explicit in the email."] : []))
            .ToList();
    }

    private static List<ParsedEmailOrder> ParseSimpleSplitBody(
        MailboxEmailIntakeRequest request,
        string? rawPo,
        string body,
        DateTimeOffset receivedAt)
    {
        var source = $"{request.Subject} {request.SenderAddress} {body}";
        if (!source.Contains("C & J Hayward", StringComparison.OrdinalIgnoreCase) &&
            !source.Contains("debhayward@yahoo.com", StringComparison.OrdinalIgnoreCase))
            return [];

        var collectionDate = body.Contains("today", StringComparison.OrdinalIgnoreCase) ? LocalDate(receivedAt) : ExtractDate(body, receivedAt);
        var deliveryDate = body.Contains("tonight", StringComparison.OrdinalIgnoreCase)
            ? LocalDate(receivedAt)
            : body.Contains("tomorrow", StringComparison.OrdinalIgnoreCase)
                ? LocalDate(receivedAt).AddDays(1)
                : collectionDate;
        if (collectionDate is null || deliveryDate is null) return [];

        var rows = Regex.Matches(body, @"(?is)(?<qty>\d{1,3})\s+to\s+(?<collection>.+?)\s*-\s*(?<destination>.+?)(?=\s+\d{1,3}\s+to\s+|Many thanks|$)")
            .Cast<Match>()
            .Select(match => (
                Pallets: int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture),
                Collection: CleanSourceLine(match.Groups["collection"].Value),
                Destination: CleanSourceLine(match.Groups["destination"].Value)))
            .Where(row => row.Pallets > 0 && !string.IsNullOrWhiteSpace(row.Collection) && !string.IsNullOrWhiteSpace(row.Destination))
            .ToList();

        return rows
            .Select((row, index) => BuildStructuredOrder(
                request,
                $"simple-split-{index + 1}-{NormaliseKey(row.Collection)}-{NormaliseKey(row.Destination)}",
                "CJHAYWARD",
                rawPo,
                collectionDate.Value,
                deliveryDate.Value,
                row.Pallets,
                row.Collection,
                row.Destination,
                "C & J Hayward pallet collection",
                string.IsNullOrWhiteSpace(rawPo) ? ["No customer PO/reference was found; a stable email reference was generated and should be checked before approval."] : []))
            .ToList();
    }

    private static ParsedEmailOrder BuildStructuredOrder(
        MailboxEmailIntakeRequest request,
        string sourceKey,
        string customer,
        string? rawPo,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        int pallets,
        string? collection,
        string destination,
        string jobType,
        IReadOnlyList<string> warnings,
        string? collectionTime = null)
    {
        var baseReference = rawPo ?? StableEmailReference(request.MessageId);
        var orderReference = BuildRowReference(baseReference, customer, destination, deliveryDate, 1);
        var naturalKey = $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{customer}|{collectionDate:yyyy-MM-dd}|{deliveryDate:yyyy-MM-dd}|{NormaliseKey(collection)}|{NormaliseKey(destination)}|{pallets}";
        var instructions = BuildInstructions(rawPo, collectionTime, null, null, request, null, warnings, jobType);
        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerCode"] = customer,
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collection,
            ["marketName"] = customer,
            ["stallNumber"] = destination,
            ["driverInstructions"] = instructions,
            ["customerPo"] = rawPo,
            ["requestedTime"] = collectionTime,
            ["jobType"] = jobType,
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = naturalKey,
            ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
            ["intakeWarnings"] = warnings
        };

        return new ParsedEmailOrder(sourceKey, naturalKey, JsonSerializer.SerializeToElement(payload), warnings);
    }

    private static bool IsBookingHeader(object?[] row)
    {
        var keys = row.Select(value => NormaliseKey(CellText(value))).Where(value => value.Length > 0).ToHashSet();
        return keys.Contains("pallets") &&
               (keys.Contains("depotdescription") || keys.Contains("destination") || keys.Contains("depot"));
    }

    private static Dictionary<string, int> HeaderMap(object?[] row)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < row.Length; index++)
        {
            var key = NormaliseKey(CellText(row[index]));
            if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                result[key] = index;
        }
        return result;
    }

    private static int FindColumn(Dictionary<string, int> columns, params string[] names)
    {
        foreach (var name in names)
            if (columns.TryGetValue(name, out var index))
                return index;
        return -1;
    }

    private static string? CellText(object?[] row, int index) =>
        index < 0 || index >= row.Length ? null : CellText(row[index]);

    private static string? CellText(object? value)
    {
        if (value is null || value is DBNull) return null;
        return value switch
        {
            DateTime date => date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } text ? text : null
        };
    }

    private static int? CellInt(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is int intValue) return intValue;
        if (row[index] is double doubleValue) return (int)Math.Round(doubleValue, MidpointRounding.AwayFromZero);
        if (decimal.TryParse(CellText(row[index]), NumberStyles.Any, CultureInfo.InvariantCulture, out var decimalValue))
            return (int)Math.Round(decimalValue, MidpointRounding.AwayFromZero);
        return null;
    }

    private static DateOnly? CellDate(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return DateOnly.FromDateTime(dateTime);
        if (row[index] is double serial && serial > 1 && serial < 100000)
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        return ParseDateText(CellText(row[index]), DateTimeOffset.UtcNow);
    }

    private static string? CellTime(object?[] row, int index)
    {
        if (index < 0 || index >= row.Length || row[index] is null) return null;
        if (row[index] is DateTime dateTime) return dateTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (row[index] is TimeSpan span) return $"{(int)span.TotalHours:00}:{span.Minutes:00}";
        if (row[index] is double serial && serial >= 0 && serial < 1)
        {
            var spanFromSerial = TimeSpan.FromDays(serial);
            return $"{spanFromSerial.Hours:00}:{spanFromSerial.Minutes:00}";
        }
        var text = CellText(row[index]);
        if (TimeSpan.TryParse(text?.Replace('.', ':'), CultureInfo.InvariantCulture, out var parsed))
            return $"{(int)parsed.TotalHours:00}:{parsed.Minutes:00}";
        return text;
    }

    private static DateOnly? ExtractDate(string input, DateTimeOffset receivedAt)
    {
        var match = DateRegex.Match(input ?? string.Empty);
        if (match.Success)
            return BuildDate(match.Groups["day"].Value, match.Groups["month"].Value, match.Groups["year"].Value, receivedAt);

        var monthNameMatch = MonthNameDateRegex.Match(input ?? string.Empty);
        if (!monthNameMatch.Success) return null;
        return BuildDate(monthNameMatch.Groups["day"].Value, monthNameMatch.Groups["month"].Value, monthNameMatch.Groups["year"].Value, receivedAt);
    }

    private static DateOnly? ExtractDateAfter(string input, string prefixPattern)
    {
        var match = Regex.Match(input ?? string.Empty, $"{prefixPattern}(?<date>\\d{{1,2}}[./-]\\d{{1,2}}(?:[./-](?:20\\d{{2}}|\\d{{2}}))?)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? ExtractDate(match.Groups["date"].Value, DateTimeOffset.UtcNow) : null;
    }

    private static DateOnly LocalDate(DateTimeOffset receivedAt)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(receivedAt, zone).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(receivedAt.ToOffset(TimeSpan.FromHours(1)).DateTime);
        }
    }

    private static DateOnly? BuildDate(string dayText, string monthText, string yearText, DateTimeOffset receivedAt)
    {
        var day = int.Parse(dayText, CultureInfo.InvariantCulture);
        var month = int.TryParse(monthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedMonth)
            ? parsedMonth
            : MonthNumber(monthText);
        var year = string.IsNullOrWhiteSpace(yearText)
            ? receivedAt.Year
            : yearText.Length == 2
                ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture)
                : int.Parse(yearText, CultureInfo.InvariantCulture);
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int MonthNumber(string value) => value.Trim().ToUpperInvariant() switch
    {
        var month when month.StartsWith("JAN", StringComparison.Ordinal) => 1,
        var month when month.StartsWith("FEB", StringComparison.Ordinal) => 2,
        var month when month.StartsWith("MAR", StringComparison.Ordinal) => 3,
        var month when month.StartsWith("APR", StringComparison.Ordinal) => 4,
        "MAY" => 5,
        var month when month.StartsWith("JUN", StringComparison.Ordinal) => 6,
        var month when month.StartsWith("JUL", StringComparison.Ordinal) => 7,
        var month when month.StartsWith("AUG", StringComparison.Ordinal) => 8,
        var month when month.StartsWith("SEP", StringComparison.Ordinal) => 9,
        var month when month.StartsWith("OCT", StringComparison.Ordinal) => 10,
        var month when month.StartsWith("NOV", StringComparison.Ordinal) => 11,
        var month when month.StartsWith("DEC", StringComparison.Ordinal) => 12,
        _ => 0
    };

    private static DateOnly? ParseDateText(string? input, DateTimeOffset receivedAt) =>
        string.IsNullOrWhiteSpace(input) ? null : ExtractDate(input, receivedAt);

    private static string? ExtractPo(string input)
    {
        var match = ExplicitPoRegex.Match(input ?? string.Empty);
        if (!match.Success) return null;
        var value = match.Groups["po"].Success ? match.Groups["po"].Value : match.Value;
        return Regex.Replace(value.Trim(), @"\s+", string.Empty).ToUpperInvariant();
    }

    private static string? ExtractTemperature(string body) => ExtractMatch(TemperatureRegex, body, "temp") is { } temp ? $"{temp}°C" : null;
    private static int? ExtractInt(Regex regex, string input, string group) => int.TryParse(ExtractMatch(regex, input, group), out var value) ? value : null;
    private static string? ExtractMatch(Regex regex, string input, string group) => regex.Match(input ?? string.Empty) is { Success: true } match ? match.Groups[group].Value.Trim() : null;

    private static string? ExtractLabelValue(string body, params string[] labels)
    {
        var wanted = labels.Select(NormaliseKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return LabelRegex.Matches(body ?? string.Empty)
            .Cast<Match>()
            .Where(match => wanted.Contains(NormaliseKey(match.Groups["label"].Value)))
            .Select(match => CleanSourceLine(match.Groups["value"].Value))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? ExtractLabelBlock(string body, params string[] labels)
    {
        body ??= string.Empty;
        var wanted = labels.Select(NormaliseKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matches = LabelRegex.Matches(body).Cast<Match>().ToList();
        foreach (var match in matches)
        {
            if (!wanted.Contains(NormaliseKey(match.Groups["label"].Value))) continue;

            var start = match.Groups["value"].Index;
            var next = LabelStartRegex.Match(body, match.Index + match.Length);
            var end = next.Success ? next.Index : body.Length;
            var block = body[start..end];
            var cleaned = CleanMultilineBlock(block);
            return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
        }

        return null;
    }

    private static string? ExtractTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var matches = Regex.Matches(value, @"\b(?<time>(?:[01]?\d|2[0-3])(?:[:.]\d{2})?\s*(?:am|pm)?)\b", RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Where(match => !LooksLikeDateComponent(value, match))
            .ToList();
        return matches.LastOrDefault()?.Groups["time"].Value;
    }

    private static bool LooksLikeDateComponent(string value, Match match)
    {
        var before = match.Index > 0 ? value[match.Index - 1] : '\0';
        var afterIndex = match.Index + match.Length;
        var after = afterIndex < value.Length ? value[afterIndex] : '\0';
        return before is '.' or '/' or '-' || after is '.' or '/' or '-';
    }

    private static string? FindLine(string body, Func<string, bool> predicate) =>
        (body ?? string.Empty)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && predicate(line));

    private static List<string> SplitTableLine(string line) =>
        line.Split('|', StringSplitOptions.TrimEntries)
            .Where(value => !string.IsNullOrWhiteSpace(value) && value != "---")
            .ToList();

    private static string? InferCollectionSiteFromSender(string? senderAddress)
    {
        var domain = SenderDomain(senderAddress);
        if (domain is null) return null;
        foreach (var (rootDomain, site) in SenderDomainCollectionSites)
            if (DomainMatches(domain, rootDomain)) return site;
        return null;
    }

    internal static bool SenderAddressMatchesDomain(string? senderAddress, string rootDomain)
    {
        var domain = SenderDomain(senderAddress);
        return domain is not null && DomainMatches(domain, rootDomain);
    }

    private static bool DomainMatches(string domain, string rootDomain) =>
        domain.Equals(rootDomain, StringComparison.OrdinalIgnoreCase) ||
        domain.EndsWith("." + rootDomain, StringComparison.OrdinalIgnoreCase);

    private static string? SenderDomain(string? senderAddress)
    {
        if (string.IsNullOrWhiteSpace(senderAddress)) return null;
        var at = senderAddress.LastIndexOf('@');
        if (at < 0 || at == senderAddress.Length - 1) return null;
        return senderAddress[(at + 1)..].Trim().Trim('>', ')', ']').ToLowerInvariant();
    }

    private static string? CleanCustomerName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = Regex.Replace(value, @"\border\b", string.Empty, RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\s+", " ").Trim(' ', '-', '–', '—', ':');
        return string.IsNullOrWhiteSpace(clean) ? null : clean;
    }

    private static string InferCustomerCodeFromMasterOrSubject(string? subject, string sourceText, string? senderAddress, string? destination)
    {
        var signal = DetectKnownSignal(sourceText, []);
        return signal?.CustomerCode ?? InferCustomerCode(subject, senderAddress, destination ?? sourceText);
    }

    private static string CustomerCode(string value)
    {
        var clean = Regex.Replace(value.Trim().ToUpperInvariant(), @"\s+", string.Empty);
        return string.IsNullOrWhiteSpace(clean) ? "EMAIL" : clean[..Math.Min(clean.Length, 40)];
    }

    private static string? CleanDeliveryAddressForSite(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var firstLine = address.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(firstLine) ? CleanSourceLine(address) : CleanSourceLine(firstLine);
    }

    private static bool BodyMentionsCollectionSite(string body, string site) =>
        !string.IsNullOrWhiteSpace(site) && body.Contains(site, StringComparison.OrdinalIgnoreCase);

    private static string ConfidenceFor(IReadOnlyList<string> warnings)
    {
        var hardWarnings = warnings.Count(warning =>
            !warning.StartsWith("Collection site inferred as ", StringComparison.OrdinalIgnoreCase) &&
            !warning.StartsWith("No customer PO/reference", StringComparison.OrdinalIgnoreCase));
        return hardWarnings == 0 ? "High" : hardWarnings <= 2 ? "Medium" : "Low";
    }

    private static string InferCustomerCode(string? subject, string? senderAddress, string? depot)
    {
        if ((senderAddress ?? string.Empty).EndsWith("@langmeadherbs.co.uk", StringComparison.OrdinalIgnoreCase) ||
            (senderAddress ?? string.Empty).EndsWith("@langmeadfarms.co.uk", StringComparison.OrdinalIgnoreCase))
            return "LANGMEADS";
        var source = $"{subject} {depot}".ToUpperInvariant();
        foreach (var brand in new[] { "MORRISONS", "ALDI", "WAITROSE", "WEIGHTROSE", "COOP", "CO-OP", "OCADO", "SAINSBURYS", "SAINSBURY", "NATURES WAY", "NATURE'S WAY", "NWF", "NWAY", "BARFOOTS", "LANGMEADS", "LANGMEAD", "LANGMEAD HERBS" })
        {
            if (source.Contains(brand, StringComparison.OrdinalIgnoreCase))
                return brand
                    .Replace("CO-OP", "COOP", StringComparison.OrdinalIgnoreCase)
                    .Replace("WEIGHTROSE", "WAITROSE", StringComparison.OrdinalIgnoreCase)
                    .Replace("SAINSBURYS", "SAINSBURY", StringComparison.OrdinalIgnoreCase)
                    .Replace("NATURE'S WAY", "NWF", StringComparison.OrdinalIgnoreCase)
                    .Replace("NATURES WAY", "NWF", StringComparison.OrdinalIgnoreCase)
                    .Replace("NWAY", "NWF", StringComparison.OrdinalIgnoreCase)
                    .Replace("BARFOOTS", "BARFOOTS", StringComparison.OrdinalIgnoreCase)
                    .Replace("LANGMEAD HERBS", "LANGMEADS", StringComparison.OrdinalIgnoreCase)
                    .Replace("LANGMEAD", "LANGMEADS", StringComparison.OrdinalIgnoreCase);
        }
        if ((senderAddress ?? string.Empty).EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase)) return "NWF";
        if ((senderAddress ?? string.Empty).EndsWith("@summerberry.co.uk", StringComparison.OrdinalIgnoreCase)) return "TSBC";
        if ((senderAddress ?? string.Empty).EndsWith("@hillsplants.com", StringComparison.OrdinalIgnoreCase)) return "HILLBROTHERS";
        if ((senderAddress ?? string.Empty).EndsWith("@langmeadherbs.co.uk", StringComparison.OrdinalIgnoreCase) ||
            (senderAddress ?? string.Empty).EndsWith("@langmeadfarms.co.uk", StringComparison.OrdinalIgnoreCase)) return "LANGMEADS";
        if ((senderAddress ?? string.Empty).EndsWith("@barfoots.co.uk", StringComparison.OrdinalIgnoreCase)) return "BARFOOTS";
        var domain = (senderAddress ?? string.Empty).Split('@').LastOrDefault();
        var stem = domain?.Split('.').FirstOrDefault();
        var clean = new string((stem ?? "EMAIL").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "EMAIL" : clean[..Math.Min(clean.Length, 40)];
    }

    private static string CleanDestination(string depot, string customer)
    {
        var value = depot.Trim();
        if (value.StartsWith(customer, StringComparison.OrdinalIgnoreCase))
            value = value[customer.Length..].Trim(' ', '-', '–', '—');
        return string.IsNullOrWhiteSpace(value) ? depot.Trim() : value;
    }

    private static string InferJobType(string? subject, string body)
    {
        var value = $"{subject} {body}";
        if (value.Contains("tray collection", StringComparison.OrdinalIgnoreCase)) return "Tray collection";
        if (value.Contains("collection", StringComparison.OrdinalIgnoreCase) && !value.Contains("delivery", StringComparison.OrdinalIgnoreCase)) return "Collection";
        return "Delivery";
    }

    private static string? InferCollectionSite(string? subject, string body, string jobType)
    {
        var explicitSite = ExtractMatch(CollectFromRegex, body, "site");
        if (!string.IsNullOrWhiteSpace(explicitSite)) return CleanSourceLine(explicitSite);
        if (body.Contains("Hills collection", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Hill Brothers", StringComparison.OrdinalIgnoreCase))
            return "Hill Brothers";
        if (body.Contains("Langmead Herbs", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("Ham Farm", StringComparison.OrdinalIgnoreCase))
            return "Ham Farm";
        if (body.Contains("Walton Farm", StringComparison.OrdinalIgnoreCase))
            return "Walton Farm";
        if (jobType == "Tray collection")
        {
            var match = Regex.Match(subject ?? string.Empty, @"Tray\s+collection\s+(?<site>.+?)(?:\s+\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)?$", RegexOptions.IgnoreCase);
            if (match.Success) return match.Groups["site"].Value.Trim(' ', '-', '–', '—');
        }
        return null;
    }

    private static string? InferDestination(string? subject, string body, string jobType)
    {
        var clean = ReFwRegex.Replace(subject ?? string.Empty, string.Empty).Trim();
        if (jobType == "Tray collection") return null;
        if (clean.Contains("COOP", StringComparison.OrdinalIgnoreCase) || clean.Contains("CO-OP", StringComparison.OrdinalIgnoreCase)) return "COOP";
        var subjectDeliveryTo = Regex.Match(clean, @"^Delivery\s+to\s+(?<dest>.+?)(?:\s+\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)?$", RegexOptions.IgnoreCase);
        if (subjectDeliveryTo.Success) return CleanSourceLine(subjectDeliveryTo.Groups["dest"].Value.Trim(' ', '-', '–', '—'));
        var bodyDeliveryTo = ExtractMatch(DeliveryToRegex, body, "site");
        if (!string.IsNullOrWhiteSpace(bodyDeliveryTo))
            return CleanSourceLine(bodyDeliveryTo.Trim(' ', '-', '–', '—'));
        var delivery = Regex.Match(clean, @"^(?:\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?\s*)?(?<dest>.+?)\s+delivery$", RegexOptions.IgnoreCase);
        return delivery.Success ? delivery.Groups["dest"].Value.Trim() : null;
    }

    private static string? NormaliseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim().Replace('.', ':');
        if (DateTime.TryParseExact(clean, ["h tt", "htt", "h:mm tt", "hh:mm tt", "H:mm", "HH:mm", "%H"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed.ToString("HH:mm", CultureInfo.InvariantCulture);
        return clean;
    }

    private static KnownIntakeSignal? DetectKnownSignal(string sourceText, IReadOnlyCollection<string> masterSiteNames)
    {
        foreach (var site in masterSiteNames.Where(site => !string.IsNullOrWhiteSpace(site)).OrderByDescending(site => site.Length))
        {
            var cleanSite = CleanSourceLine(site);
            if (cleanSite.Length < 3) continue;
            if (sourceText.Contains(cleanSite, StringComparison.OrdinalIgnoreCase))
                return new KnownIntakeSignal(InferCustomerCode(sourceText, null, cleanSite), cleanSite, [cleanSite]);
        }

        foreach (var signal in KnownSignals)
        {
            if (signal.Aliases.Any(alias => sourceText.Contains(alias, StringComparison.OrdinalIgnoreCase)))
                return signal;
        }

        return null;
    }

    private static string NaturalKey(MailboxEmailIntakeRequest request, string customer, string destination, DateOnly date)
    {
        var canonicalSubject = ReFwRegex.Replace(request.Subject ?? string.Empty, string.Empty).Trim().ToUpperInvariant();
        return $"{(request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant()}|{canonicalSubject}|{date:yyyy-MM-dd}|{customer.ToUpperInvariant()}|{destination.Trim().ToUpperInvariant()}";
    }

    private static string BuildRowReference(string baseReference, string customer, string destination, DateOnly date, int row)
    {
        var baseClean = SafeToken(baseReference, 38);
        var destClean = SafeToken(destination, 24);
        var candidate = $"{baseClean}/{destClean}";
        if (candidate.Length <= 80) return candidate;
        candidate = $"{baseClean}/{SafeToken(customer, 12)}/{date:MMdd}/{row}";
        return candidate[..Math.Min(candidate.Length, 80)];
    }

    private static string StableEmailReference(string messageId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(messageId));
        return $"EMAIL-{Convert.ToHexString(bytes)[..12]}";
    }

    private static string SafeToken(string value, int max)
    {
        var clean = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9/-]+", "-").Trim('-');
        if (clean.Length == 0) clean = "ORDER";
        return clean[..Math.Min(clean.Length, max)];
    }

    private static string BuildInstructions(
        string? rawPo,
        string? requestedTime,
        string? availableTime,
        string? temperature,
        MailboxEmailIntakeRequest request,
        string? attachmentName,
        IReadOnlyCollection<string> warnings,
        string jobType,
        string? deliveryTime = null,
        string? deliveryTimeConstraint = null,
        string? deliveryAddress = null)
    {
        var items = new List<string?>
        {
            $"Order type: {jobType}",
            string.IsNullOrWhiteSpace(rawPo) ? null : $"PO ref: {rawPo}",
            string.IsNullOrWhiteSpace(requestedTime) ? null : $"Requested time: {requestedTime}",
            string.IsNullOrWhiteSpace(availableTime) ? null : $"Available time: {availableTime}",
            string.IsNullOrWhiteSpace(deliveryTime) ? null : $"Delivery time: {deliveryTime}{(string.IsNullOrWhiteSpace(deliveryTimeConstraint) ? string.Empty : $" {deliveryTimeConstraint}")}",
            string.IsNullOrWhiteSpace(deliveryAddress) ? null : $"Delivery address: {CleanSourceLine(deliveryAddress)}",
            string.IsNullOrWhiteSpace(temperature) ? null : $"Temperature: {temperature}",
            $"Source email: {request.Subject}",
            string.IsNullOrWhiteSpace(request.SenderAddress) ? null : $"Source sender: {request.SenderAddress}",
            string.IsNullOrWhiteSpace(attachmentName) ? null : $"Source attachment: {attachmentName}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        };
        var result = string.Join(" · ", items.Where(item => !string.IsNullOrWhiteSpace(item)));
        return result.Length <= 1000 ? result : result[..1000];
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
        => MailboxBodyNormalizer.Normalize(bodyText, bodyHtml);

    private static string CleanMultilineBlock(string value)
    {
        var lines = value
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim(' ', '-', '–', '—'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();
        var result = string.Join("\n", lines);
        return result.Length <= 500 ? result : result[..500];
    }

    private static string CleanSourceLine(string value)
    {
        var cleaned = Regex.Replace(value, @"\s+", " ").Trim();
        return cleaned.Length <= 200 ? cleaned : cleaned[..200];
    }

    private static byte[] DecodeBase64(string value)
    {
        var trimmed = value.Trim();
        var comma = trimmed.IndexOf(',');
        if (comma >= 0 && trimmed[..comma].Contains("base64", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[(comma + 1)..];
        return Convert.FromBase64String(trimmed);
    }

    private static bool LooksOperationalOnly(string subject, string body)
    {
        var value = $"{subject} {body}";
        return value.Contains("night shunting", StringComparison.OrdinalIgnoreCase)
            || value.Contains("available loads", StringComparison.OrdinalIgnoreCase)
            || value.Contains("loads available", StringComparison.OrdinalIgnoreCase)
            || value.Contains("load work available", StringComparison.OrdinalIgnoreCase)
            || value.Contains("loads tipping", StringComparison.OrdinalIgnoreCase)
            || value.Contains("rates negotiable", StringComparison.OrdinalIgnoreCase)
            || value.Contains("must be own vehicle", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Monarch Available Loads", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Can you cover the below loads", StringComparison.OrdinalIgnoreCase)
            || value.Contains("let us know if you are interested", StringComparison.OrdinalIgnoreCase)
            || value.Contains("let us know if you can assist", StringComparison.OrdinalIgnoreCase)
            || value.Contains("You are receiving this email because you opted in", StringComparison.OrdinalIgnoreCase)
            || value.Contains("current stock levels", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ETA for tonight", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Inbound ETA", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Please find attached ETA", StringComparison.OrdinalIgnoreCase)
            || value.Contains("is ready to be collected", StringComparison.OrdinalIgnoreCase)
            || value.Contains("is ready for collection", StringComparison.OrdinalIgnoreCase)
            || value.Contains("are ready for collection", StringComparison.OrdinalIgnoreCase)
            || value.Contains("ready for collection from", StringComparison.OrdinalIgnoreCase)
            || value.Contains("missing PO request log", StringComparison.OrdinalIgnoreCase)
            || value.Contains("fleetio.com", StringComparison.OrdinalIgnoreCase)
            || value.Contains("notifications@fleetio.com", StringComparison.OrdinalIgnoreCase)
            || value.Contains("failed inspection", StringComparison.OrdinalIgnoreCase)
            || value.Contains("walk round check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("walkround check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("drivers unit walk round", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksTmsLoopback(string subject, string sender, string body)
    {
        if (!sender.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase)) return false;
        var value = $"{subject} {body}";
        return value.Contains("SLH TMS Intake Queue", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TMS Intake Queue", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Live Trigger Check", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Order Capture", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormaliseKey(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

internal sealed record KnownIntakeSignal(string CustomerCode, string SiteName, IReadOnlyList<string> Aliases);

public sealed record MailboxAttachmentRequest(
    string? Name,
    string? ContentType,
    string? ContentBase64,
    bool? IsInline = false,
    string? ContentId = null,
    long? Size = null,
    string? ContentBytes = null)
{
    [JsonIgnore]
    public string? EffectiveContentBase64 => ContentBase64 ?? ContentBytes;
}

public sealed record MailboxEmailIntakeRequest(
    string MessageId,
    string? InternetMessageId,
    string? Mailbox,
    string? SenderAddress,
    string? SenderName,
    string? Subject,
    DateTimeOffset? ReceivedAtUtc,
    string? BodyText,
    string? BodyHtml,
    string? WebLink,
    List<MailboxAttachmentRequest>? Attachments,
    string? ConversationId = null,
    JsonElement? ToRecipients = null,
    JsonElement? CcRecipients = null,
    string? BodyFormat = null,
    string? Importance = null,
    string? CorrelationId = null);

public sealed record ParsedEmailOrder(
    string SourceKey,
    string NaturalKey,
    JsonElement Payload,
    IReadOnlyList<string> Warnings);

public sealed record EmailIntakeParseResult(
    IReadOnlyList<ParsedEmailOrder> Orders,
    IReadOnlyList<string> Warnings,
    string? IgnoredReason)
{
    public static EmailIntakeParseResult Ignored(string reason) => new([], [], reason);
}
