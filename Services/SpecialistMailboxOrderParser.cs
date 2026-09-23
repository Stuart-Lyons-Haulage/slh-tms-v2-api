using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Handles high-value Info mailbox formats whose structure cannot be represented
/// safely by the generic single-date body parser. Returning null delegates to
/// EmailOrderIntakeService.
/// </summary>
public sealed class SpecialistMailboxOrderParser
{
    private static readonly Regex CancellationRegex = new(
        @"\b(cancelled|canceled|cancellation)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BookingReferenceRegex = new(
        @"\bBooking\s+Ref(?:erence)?\s*:\s*(?<ref>[A-Z0-9/-]{5,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex GenericPalletRegex = new(
        @"\b(?<qty>\d{1,3})\s*(?:pallets?|plts?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumericDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])(?:[./-](?<year>20\d{2}|\d{2}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NamedDateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?\s+(?<month>January|February|March|April|May|June|July|August|September|October|November|December)(?:\s+(?<year>20\d{2}))?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CollectionTimeRegex = new(
        @"\bCollection\s*:[^\r\n]*?\bfrom\s+(?<time>[0-2]?\d:[0-5]\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TransferSubjectRegex = new(
        @"\bCollection\s+from\s+(?<from>.+?)\s+to\s+(?<to>.+?)\s+(?<date>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)(?:\s*,\s*(?<ref>\d{5,}))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RouteTransferSubjectRegex = new(
        @"^(?<from>[A-Z0-9 .&'()/-]{2,100}?)\s+to\s+(?<to>[A-Z0-9 .&'()/-]{2,100}?)\s+transfers?\s+for\s+collections?\s*[-–—:]?\s*(?<date>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)(?:\s*,\s*(?<ref>\d{5,}))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NwfTransferSubjectRegex = new(
        @"^NWF\s+transfer\s*[-–—:]\s*(?<from>[A-Z0-9 .&'()/-]{2,100}?)\s+to\s+(?<to>[A-Z0-9 .&'()/-]{2,100}?)\s+(?:(?:MON|TUE|WED|THU|FRI|SAT|SUN)(?:DAY)?\s+)?(?<date>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CoventDropRegex = new(
        @"^(?<name>[^\r\n-][^\r\n]{1,100}?)\s*-\s*(?<qty>\d{1,3})\s+pallets?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex IfcoRowRegex = new(
        @"(?m)^IFCO\s*\|(?<fields>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NwfConfirmedCustomerRowRegex = new(
        @"(?m)^\s*(?<customer>ALDI|MORRISONS|WAITROSE|COSTCO)\s*\|(?<fields>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NwfCollectionSplitRegex = new(
        @"(?<site>Barnham|Merston|Runcton|Selsey|Drayton)\s+(?<qty>\d{1,3})\s*/?\s*(?:plts?|pallets?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WaitroseDirectDepotRegex = new(
        @"please\s+collect\s+(?<qty>\d{1,3})\s+pallets?\s+from\s+(?<collection>[^\r\n.]+?)\s+(?:today\s+)?(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)?\s*(?<collectionDate>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?).*?^[\s*•-]*(?<destination>[A-Za-z][A-Za-z0-9 .&'()/-]{2,80}?)\s+(?<destQty>\d{1,3})\s+pallets?.*?delivery\s+date\s+(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)?\s*(?<deliveryDate>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?).*?PO\s+number\s*[:#.-]?\s*(?<po>[A-Z0-9/-]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.Multiline);

    private static readonly Regex ApsDoleSubwayRegex = new(
        @"address\s+for\s+Dole\s+Subway\s*:\s*(?<address>.*?)(?:\bFor\s+D\.?D\.?\s*(?<deliveryDate>\d{1,2}[./-]\d{1,2}(?:[./-]\d{2,4})?)\s+will\s+be\s+(?<qty>\d{1,3})\s+pallets?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var subject = (request.Subject ?? string.Empty).Trim();
        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var combined = $"{subject}\n{body}";

        if (CancellationRegex.IsMatch(combined))
        {
            return new EmailIntakeParseResult(
                [],
                ["Cancellation email detected. It was deliberately not created as a new transport order."],
                "Cancellation/amendment detected. Review against the existing order rather than creating a duplicate.");
        }

        var waitroseDirect = ParseWaitroseDirectDepot(request, subject, body);
        if (waitroseDirect is not null) return waitroseDirect;

        var nwfConfirmedCustomer = ParseNwfConfirmedCustomerCollections(request, subject, body);
        if (nwfConfirmedCustomer is not null) return nwfConfirmedCustomer;

        var apsDoleSubway = ParseApsDoleSubway(request, subject, body);
        if (apsDoleSubway is not null) return apsDoleSubway;

        var attachmentOnly = ParseAttachmentOnlyBookingNotice(request, subject, body);
        if (attachmentOnly is not null) return attachmentOnly;

        var marketLines = ParsePmTransportMarketLines(request, subject, body);
        if (marketLines is not null) return marketLines;

        if (subject.Contains("Covent Garden", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("APS Produce", StringComparison.OrdinalIgnoreCase))
        {
            return ParseCoventGarden(request, subject, body);
        }

        if (subject.Contains("Amazon", StringComparison.OrdinalIgnoreCase) &&
            body.Contains("APS Produce", StringComparison.OrdinalIgnoreCase))
        {
            return ParseAmazon(request, subject, body);
        }

        if (combined.Contains("IFCO", StringComparison.OrdinalIgnoreCase) &&
            combined.Contains("|", StringComparison.Ordinal))
        {
            var ifco = ParseIfcoCollections(request, body);
            if (ifco is not null) return ifco;
        }

        var transfer = TransferSubjectRegex.Match(subject);
        if (transfer.Success)
            return ParseTransfer(request, transfer, body);

        var routeTransfer = RouteTransferSubjectRegex.Match(subject);
        if (routeTransfer.Success)
            return ParseTransfer(request, routeTransfer, body);

        var nwfTransfer = NwfTransferSubjectRegex.Match(subject);
        if (nwfTransfer.Success)
            return ParseTransfer(request, nwfTransfer, body);

        return null;
    }

    private static EmailIntakeParseResult? ParseNwfConfirmedCustomerCollections(MailboxEmailIntakeRequest request, string subject, string body)
    {
        if (!(request.SenderAddress ?? string.Empty).EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("confirm", StringComparison.OrdinalIgnoreCase))
            return null;

        var rows = NwfConfirmedCustomerRowRegex.Matches(body);
        if (rows.Count == 0) return null;

        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var orders = new List<ParsedEmailOrder>();
        var rowNumber = 0;

        foreach (Match row in rows)
        {
            rowNumber++;
            var customer = row.Groups["customer"].Value.Trim().ToUpperInvariant();
            var fields = row.Groups["fields"].Value.Split('|').Select(CleanField).ToArray();
            if (fields.Length < 10) continue;

            var transportPo = CleanReference(fields[0]);
            var collectionDate = ParseFlexibleNumericDate(fields[2], received.Year);
            var deliveryDate = ParseFlexibleNumericDate(fields[3], received.Year);
            var destination = fields[5];
            var collection = fields[8];
            var totalPallets = int.TryParse(fields[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTotal) ? parsedTotal : (int?)null;
            var splitNotes = fields.ElementAtOrDefault(10) ?? string.Empty;
            if (collectionDate is null || deliveryDate is null || string.IsNullOrWhiteSpace(destination) || string.IsNullOrWhiteSpace(collection) || totalPallets is null)
                continue;

            var sites = collection.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (sites.Length == 1)
            {
                AddNwfConfirmedCustomerOrder(orders, request, rowNumber, customer, transportPo, collectionDate.Value,
                    deliveryDate.Value, destination, sites[0], totalPallets.Value, []);
                continue;
            }

            var splits = NwfCollectionSplitRegex.Matches(splitNotes)
                .Select(match => (Site: CleanField(match.Groups["site"].Value), Pallets: int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture)))
                .ToList();
            var reconciled = splits.Count == sites.Length &&
                             splits.Sum(item => item.Pallets) == totalPallets.Value &&
                             sites.All(site => splits.Any(item => item.Site.Equals(site, StringComparison.OrdinalIgnoreCase)));
            if (!reconciled) continue;

            foreach (var split in splits)
                AddNwfConfirmedCustomerOrder(orders, request, rowNumber, customer, transportPo, collectionDate.Value,
                    deliveryDate.Value, destination, split.Site, split.Pallets, []);
        }

        return orders.Count == 0 ? null : new EmailIntakeParseResult(orders, [], null);
    }

    private static void AddNwfConfirmedCustomerOrder(
        ICollection<ParsedEmailOrder> orders,
        MailboxEmailIntakeRequest request,
        int rowNumber,
        string customer,
        string transportPo,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        string destination,
        string collection,
        int pallets,
        IReadOnlyList<string> warnings)
    {
        var customerPo = $"{transportPo}/{collection}";
        var reference = BuildReference(transportPo, $"{destination}-{collection}");
        var naturalKey = NaturalKey(request, customer, destination, collectionDate, customerPo);
        var payload = BasePayload(request, reference, customerPo, customer, collectionDate, deliveryDate, pallets,
            collection, destination, $"NWF confirmed {customer} collection", null, warnings, "NWF confirmed customer collection table");
        orders.Add(new ParsedEmailOrder($"nwf-confirmed-{rowNumber}-{SafeToken(collection, 20)}", naturalKey, payload, warnings));
    }

    private static EmailIntakeParseResult? ParseWaitroseDirectDepot(MailboxEmailIntakeRequest request, string subject, string body)
    {
        if (!subject.Contains("WAITROSE", StringComparison.OrdinalIgnoreCase) && !body.Contains("PO number", StringComparison.OrdinalIgnoreCase))
            return null;

        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var match = WaitroseDirectDepotRegex.Match(body);
        if (!match.Success) return null;

        var pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
        var destinationPallets = int.Parse(match.Groups["destQty"].Value, CultureInfo.InvariantCulture);
        var collectionDate = ParseFlexibleNumericDate(match.Groups["collectionDate"].Value, received.Year);
        var deliveryDate = ParseFlexibleNumericDate(match.Groups["deliveryDate"].Value, received.Year);
        var collection = CleanDropName(match.Groups["collection"].Value);
        var destination = CleanDropName(match.Groups["destination"].Value);
        var po = CleanReference(match.Groups["po"].Value);

        if (collectionDate is null || deliveryDate is null || pallets <= 0 || string.IsNullOrWhiteSpace(destination))
            return null;

        var warnings = new List<string>();
        if (destinationPallets != pallets)
            warnings.Add("Collection and destination pallet quantities differ in the source email; check before approval.");

        var reference = BuildReference(po, destination);
        var naturalKey = NaturalKey(request, "WAITROSE", destination, collectionDate.Value, po);
        var payload = BasePayload(request, reference, po, "WAITROSE", collectionDate.Value, deliveryDate.Value, pallets,
            collection, destination, "Hall Hunter direct depot delivery", null, warnings, "HHP Waitrose direct depot body email");
        return new EmailIntakeParseResult([new ParsedEmailOrder("waitrose-direct-depot-1", naturalKey, payload, warnings)], [], null);
    }

    private static EmailIntakeParseResult? ParseApsDoleSubway(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var combined = $"{subject}\n{body}";
        if (!combined.Contains("Dole Subway", StringComparison.OrdinalIgnoreCase) ||
            !combined.Contains("D.D", StringComparison.OrdinalIgnoreCase))
            return null;

        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var match = ApsDoleSubwayRegex.Match(body);
        if (!match.Success) return null;

        var deliveryDate = ParseFlexibleNumericDate(match.Groups["deliveryDate"].Value, received.Year);
        if (deliveryDate is null) return null;
        var pallets = int.Parse(match.Groups["qty"].Value, CultureInfo.InvariantCulture);
        var addressBlock = CleanAddressBlock(match.Groups["address"].Value);
        var destination = DestinationFromAddress(addressBlock) ?? "Oliver Kay Hoddesdon";
        var warnings = new List<string>();

        var collectionDate = LocalDate(received);
        var customer = "APS";
        var reference = BuildReference(StableEmailReference(request.MessageId), destination);
        var naturalKey = NaturalKey(request, customer, destination, collectionDate, null);
        var payload = BasePayload(request, reference, null, customer, collectionDate, deliveryDate.Value, pallets,
            "APS Produce", destination, "Dole Subway depot delivery", null, warnings, "APS Dole Subway body email");
        var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText())!;
        fields["deliveryAddress"] = addressBlock;
        fields["emailContextCandidates"] = new[] { "APS Produce", "Dole Subway", destination, addressBlock };
        return new EmailIntakeParseResult([new ParsedEmailOrder("aps-dole-subway-1", naturalKey, JsonSerializer.SerializeToElement(fields), warnings)], [], null);
    }

    private static EmailIntakeParseResult? ParseAttachmentOnlyBookingNotice(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var hasRealAttachments = (request.Attachments ?? []).Any(attachment => attachment.IsInline != true);
        if (!hasRealAttachments) return null;

        var combined = $"{subject}\n{body}";
        var looksCoopBooking = combined.Contains("COOP", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("CO-OP", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("CO OP", StringComparison.OrdinalIgnoreCase);
        var saysAttached = combined.Contains("attached", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("attachment", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("booking", StringComparison.OrdinalIgnoreCase);
        var hasBodyOrder = GenericPalletRegex.IsMatch(body) && NumericDateRegex.IsMatch(body);

        if (!looksCoopBooking || !saysAttached || hasBodyOrder) return null;

        return new EmailIntakeParseResult(
            [],
            ["CO-OP booking email depends on attachment content. No body-only transport order was staged, preventing a zero-pallet placeholder."],
            "CO-OP booking details are attachment-only. Parse the attachment content or review the source email before staging an order.");
    }

    private static EmailIntakeParseResult? ParsePmTransportMarketLines(
        MailboxEmailIntakeRequest request, string subject, string body)
    {
        if (!(request.SenderAddress ?? string.Empty).EndsWith("@pmtransport.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("market", StringComparison.OrdinalIgnoreCase)) return null;

        if (Regex.IsMatch(subject, @"^(RE|FW|FWD)\s*:", RegexOptions.IgnoreCase)) return null;

        var collection = Regex.Match(body, @"(?im)^\s*Please\s+collect\w*\s+(?<total>\d+)\s*(?:pt|p|pallets?)\s+from\s+(?<site>.+?)\s+today\b",
            RegexOptions.IgnoreCase);
        if (!collection.Success) return null;
        var rows = Regex.Matches(body, @"(?im)^\s*(?<qty>\d{1,3})\s*(?:pt|p|pallets?)\s+(?<stall>[^\r\n]+?)\s+(?<market>spit(?:alfields)?|(?:new\s+)?covent(?:\s+garden)?|western(?:\s+international)?)\s*$")
            .Cast<Match>().ToList();
        if (rows.Count == 0) return null;

        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var date = LocalDate(received);
        var total = int.Parse(collection.Groups["total"].Value, CultureInfo.InvariantCulture);
        var parsedTotal = rows.Sum(row => int.Parse(row.Groups["qty"].Value, CultureInfo.InvariantCulture));
        if (total != parsedTotal)
            return new EmailIntakeParseResult([], ["Market line quantities do not match the stated total."],
                "Market order requires manual review because some delivery rows may be missing.");

        var orders = new List<ParsedEmailOrder>();
        foreach (var row in rows)
        {
            var stall = CleanDropName(row.Groups["stall"].Value);
            var marketText = row.Groups["market"].Value;
            var market = marketText.StartsWith("spit", StringComparison.OrdinalIgnoreCase) ? "Spit"
                : marketText.StartsWith("western", StringComparison.OrdinalIgnoreCase) ? "Western" : "Covent";
            var pallets = int.Parse(row.Groups["qty"].Value, CultureInfo.InvariantCulture);
            var warnings = new[] { "Exact collection and delivery times were not stated. Confirm market instructions before approval." };
            var destinationKey = $"{market}/{stall}";
            var payload = BasePayload(request, BuildReference(StableEmailReference(request.MessageId), destinationKey),
                null, "PMTRANSPORT", date, date, pallets, CleanDropName(collection.Groups["site"].Value), stall,
                "Market delivery", null, warnings, "PM Transport market body lines");
            var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(payload.GetRawText())!;
            fields["marketName"] = market;
            fields["plannerReady"] = false;
            fields["intakeStatus"] = "PendingReview";
            var key = NaturalKey(request, "PMTRANSPORT", destinationKey, date, null);
            fields["intakeNaturalKey"] = key;
            orders.Add(new ParsedEmailOrder($"market-body-{orders.Count + 1}", key, JsonSerializer.SerializeToElement(fields), warnings));
        }
        return new EmailIntakeParseResult(orders, [], null);
    }

    private static EmailIntakeParseResult ParseAmazon(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var bookingRef = Match(BookingReferenceRegex, body, "ref") ?? StableEmailReference(request.MessageId);
        var collectionDate = DateAfterKeyword(body, "Collection", received.Year) ?? EarliestDate(body, received.Year);
        var deliveryDate = FirstNumericDate(body) ?? DateAfterKeyword(body, "Delivery", received.Year) ?? LatestDate($"{subject}\n{body}", received.Year) ?? collectionDate;
        var availableTime = Match(CollectionTimeRegex, body, "time");
        var pallets = FirstPalletQuantity(body);
        var collectionSite = FirstLineAfterHeader(body, "Collection") ?? "APS Produce";
        if (collectionSite.Contains("Tuesday", StringComparison.OrdinalIgnoreCase) || collectionSite.Contains("Wednesday", StringComparison.OrdinalIgnoreCase))
            collectionSite = "APS Produce";
        var destination = FirstLineAfterHeader(body, "Delivery") ?? "Amazon delivery";
        var warnings = new List<string>();
        if (collectionDate is null) warnings.Add("Collection date was not identified.");
        if (deliveryDate is null) warnings.Add("Delivery date was not identified.");
        if (pallets is null) warnings.Add("Pallet quantity was not identified.");
        if (destination.Equals("Amazon delivery", StringComparison.OrdinalIgnoreCase)) warnings.Add("Amazon destination requires confirmation.");

        var workingDate = collectionDate ?? deliveryDate ?? LocalDate(received);
        var reference = BuildReference(bookingRef, destination);
        var naturalKey = NaturalKey(request, "AMAZON", destination, workingDate, bookingRef);
        var payload = BasePayload(request, reference, bookingRef, "AMAZON", collectionDate ?? workingDate, deliveryDate ?? workingDate,
            pallets, collectionSite, destination, "Delivery", availableTime, warnings, "APS/Amazon body email");

        return new EmailIntakeParseResult([new ParsedEmailOrder("amazon-body-1", naturalKey, payload, warnings)], [], null);
    }

    private static EmailIntakeParseResult ParseCoventGarden(MailboxEmailIntakeRequest request, string subject, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var dates = AllDates($"{subject}\n{body}", received.Year).Distinct().OrderBy(date => date).ToList();
        var collectionDate = DateAfterKeyword(body, "Collection", received.Year) ?? dates.FirstOrDefault();
        if (collectionDate == default) collectionDate = LocalDate(received);
        var deliveryDate = dates.Count > 1 ? dates.Last() : collectionDate;
        var availableTime = Match(CollectionTimeRegex, body, "time");
        var drops = CoventDropRegex.Matches(body)
            .Cast<Match>()
            .Select(match => new { Name = CleanDropName(match.Groups["name"].Value), Pallets = int.TryParse(match.Groups["qty"].Value, out var qty) ? qty : 0 })
            .Where(drop => drop.Pallets > 0 && !string.IsNullOrWhiteSpace(drop.Name))
            .ToList();

        if (drops.Count == 0)
            return new EmailIntakeParseResult([], ["Covent Garden email detected but no individual pallet lines could be parsed."],
                "Covent Garden format needs manual review because no delivery rows were identified.");

        var baseReference = StableEmailReference(request.MessageId);
        var orders = new List<ParsedEmailOrder>();
        for (var index = 0; index < drops.Count; index++)
        {
            var drop = drops[index];
            var warnings = new List<string> { "Delivery instruction spans the evening/overnight period; exact delivery time was not stated in the email." };
            var reference = BuildReference(baseReference, drop.Name);
            var naturalKey = NaturalKey(request, "COVENTGARDEN", drop.Name, collectionDate, null);
            var payload = BasePayload(request, reference, null, "COVENTGARDEN", collectionDate, deliveryDate, drop.Pallets,
                "APS Produce", drop.Name, "Market delivery", availableTime, warnings, "APS/Covent Garden multi-drop body email");
            orders.Add(new ParsedEmailOrder($"covent-drop-{index + 1}", naturalKey, payload, warnings));
        }

        return new EmailIntakeParseResult(orders, [], null);
    }

    private static EmailIntakeParseResult? ParseIfcoCollections(MailboxEmailIntakeRequest request, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var orders = new List<ParsedEmailOrder>();
        var warnings = new List<string>();
        var rowNumber = 0;

        foreach (Match match in IfcoRowRegex.Matches(body))
        {
            var fields = match.Groups["fields"].Value.Split('|').Select(CleanField).ToList();
            if (fields.Count < 10) continue;

            rowNumber++;
            var transportPo = NullIfTbc(fields.ElementAtOrDefault(0));
            var collectionDate = ParseFlexibleNumericDate(fields.ElementAtOrDefault(2) ?? string.Empty, received.Year);
            var deliveryDate = ParseFlexibleNumericDate(fields.ElementAtOrDefault(3) ?? string.Empty, received.Year)
                ?? (fields.ElementAtOrDefault(3)?.Contains("same day", StringComparison.OrdinalIgnoreCase) == true ? collectionDate : null)
                ?? collectionDate;
            var collectionDepot = NullIfTbc(fields.ElementAtOrDefault(5));
            var loadReference = NullIfTbc(fields.ElementAtOrDefault(6));
            var cratePo = NullIfTbc(fields.ElementAtOrDefault(7));
            var returningTo = NullIfTbc(fields.ElementAtOrDefault(8));
            var quantity = int.TryParse(fields.ElementAtOrDefault(9), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedQty) ? parsedQty : (int?)null;
            var notes = fields.ElementAtOrDefault(10);

            if (collectionDate is null || deliveryDate is null)
            {
                warnings.Add("IFCO row was skipped because the collection date could not be read.");
                continue;
            }

            var rowWarnings = new List<string>();
            if (transportPo is null) rowWarnings.Add("Transport PO is TBC.");
            if (collectionDepot is null) rowWarnings.Add("IFCO collection depot is TBC.");
            if (loadReference is null) rowWarnings.Add("IFCO load reference is missing.");
            if (returningTo is null) rowWarnings.Add("Return destination is missing.");
            if (quantity is null or <= 0) rowWarnings.Add("Crate/tray quantity is missing.");

            var sourceRef = transportPo ?? cratePo ?? loadReference ?? $"IFCO-{collectionDate:yyyyMMdd}-{rowNumber}";
            var destination = returningTo ?? "Destination TBC";
            var collection = collectionDepot ?? "Collection depot TBC";
            var reference = BuildReference(sourceRef, destination);
            var naturalKey = NaturalKey(request, "IFCO", destination, collectionDate.Value, transportPo ?? cratePo ?? loadReference);
            var matchKeys = BuildIfcoMatchKeys(collectionDate.Value, transportPo, cratePo, loadReference, collectionDepot, returningTo);
            var payload = BuildIfcoPayload(request, reference, transportPo, cratePo, loadReference, collectionDate.Value,
                deliveryDate.Value, quantity, collection, destination, notes, rowWarnings, matchKeys);

            orders.Add(new ParsedEmailOrder($"ifco-row-{rowNumber}", naturalKey, payload, rowWarnings));
        }

        if (orders.Count == 0) return null;
        return new EmailIntakeParseResult(orders, warnings, null);
    }

    private static EmailIntakeParseResult ParseTransfer(MailboxEmailIntakeRequest request, Match transfer, string body)
    {
        var received = request.ReceivedAtUtc ?? DateTimeOffset.UtcNow;
        var collection = transfer.Groups["from"].Value.Trim();
        var destination = transfer.Groups["to"].Value.Trim();
        var date = ParseFlexibleNumericDate(transfer.Groups["date"].Value, received.Year) ?? LocalDate(received);
        var bodyTransferRef = Regex.Match(body, @"\bINTO\d{5,}\b", RegexOptions.IgnoreCase);
        var transportRef = transfer.Groups["ref"].Success
            ? transfer.Groups["ref"].Value.Trim()
            : bodyTransferRef.Success ? bodyTransferRef.Value.ToUpperInvariant() : StableEmailReference(request.MessageId);
        var pallets = FirstPalletQuantity(body);
        var combined = $"{request.Subject}\n{body}";
        var customer = InferTransferCustomer(request, combined, collection, destination);
        var warnings = new List<string>();
        if (pallets is null) warnings.Add("Pallet quantity was not identified.");
        var reference = BuildReference(transportRef, destination);
        var naturalKey = NaturalKey(request, customer, destination, date, transportRef);
        var payload = BasePayload(request, reference, transportRef, customer, date, date, pallets,
            collection, destination, "Collection transfer", null, warnings, "Route stated in email subject");

        return new EmailIntakeParseResult([new ParsedEmailOrder("transfer-body-1", naturalKey, payload, warnings)], [], null);
    }

    private static JsonElement BuildIfcoPayload(
        MailboxEmailIntakeRequest request,
        string reference,
        string? transportPo,
        string? cratePo,
        string? loadReference,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        int? quantity,
        string collection,
        string destination,
        string? notes,
        IReadOnlyList<string> warnings,
        IReadOnlyList<string> matchKeys)
    {
        var ready = warnings.Count == 0;
        var instructions = string.Join(" · ", new[]
        {
            "Order type: IFCO crate/tray collection",
            string.IsNullOrWhiteSpace(transportPo) ? null : $"Transport PO: {transportPo}",
            string.IsNullOrWhiteSpace(cratePo) ? null : $"Crate PO: {cratePo}",
            string.IsNullOrWhiteSpace(loadReference) ? null : $"IFCO load ref: {loadReference}",
            string.IsNullOrWhiteSpace(notes) ? null : notes,
            $"Source email: {request.Subject}",
            "Parser: NWF/IFCO body table",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = reference,
            ["customerPo"] = transportPo ?? cratePo,
            ["transportPo"] = transportPo,
            ["cratePo"] = cratePo,
            ["collectionReference"] = loadReference,
            ["customerCode"] = "IFCO",
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = quantity,
            ["sellerName"] = collection,
            ["marketName"] = "IFCO",
            ["stallNumber"] = destination,
            ["jobType"] = "IFCO crate/tray collection",
            ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
            ["plannerReady"] = ready,
            ["intakeStatus"] = ready ? null : "PendingReview",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = NaturalKey(request, "IFCO", destination, collectionDate, transportPo ?? cratePo ?? loadReference),
            ["intakeMatchKeys"] = matchKeys,
            ["intakeConfidence"] = ready ? "High" : "Medium",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = "NWF/IFCO body table"
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static JsonElement BasePayload(
        MailboxEmailIntakeRequest request,
        string orderReference,
        string? customerPo,
        string customer,
        DateOnly collectionDate,
        DateOnly deliveryDate,
        int? pallets,
        string collection,
        string destination,
        string jobType,
        string? availableTime,
        IReadOnlyList<string> warnings,
        string parser)
    {
        var coreReady = pallets is > 0 && !string.IsNullOrWhiteSpace(collection) && !string.IsNullOrWhiteSpace(destination);
        var instructions = string.Join(" · ", new[]
        {
            $"Order type: {jobType}",
            string.IsNullOrWhiteSpace(customerPo) ? null : $"PO ref: {customerPo}",
            string.IsNullOrWhiteSpace(availableTime) ? null : $"Available time: {availableTime}",
            $"Source email: {request.Subject}",
            $"Parser: {parser}",
            warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var payload = new Dictionary<string, object?>
        {
            ["poNumber"] = orderReference,
            ["customerPo"] = customerPo,
            ["customerCode"] = customer,
            ["collectionDate"] = collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["pallets"] = pallets,
            ["sellerName"] = collection,
            ["marketName"] = customer,
            ["stallNumber"] = destination,
            ["jobType"] = jobType,
            ["availableTime"] = availableTime,
            ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
            ["plannerReady"] = coreReady,
            ["intakeStatus"] = coreReady ? null : "PendingReview",
            ["sourceMessageId"] = request.MessageId,
            ["sourceInternetMessageId"] = request.InternetMessageId,
            ["sourceSender"] = request.SenderAddress,
            ["sourceSenderName"] = request.SenderName,
            ["sourceSubject"] = request.Subject,
            ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
            ["sourceWebLink"] = request.WebLink,
            ["intakeNaturalKey"] = NaturalKey(request, customer, destination, collectionDate, customerPo),
            ["intakeConfidence"] = coreReady && warnings.Count == 0 ? "High" : "Medium",
            ["intakeWarnings"] = warnings,
            ["intakeParser"] = parser,
            ["emailContextCandidates"] = new[] { collection, destination, customer }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray()
        };
        return JsonSerializer.SerializeToElement(payload);
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
    {
        var input = MailboxBodyNormalizer.Normalize(bodyText, bodyHtml);
        input = input.Replace("**", string.Empty, StringComparison.Ordinal);
        input = Regex.Replace(input, @"[ \t]+", " ");
        input = Regex.Replace(input, @"\r?\n[ \t]*", "\n");
        return input.Trim();
    }

    private static string? FirstLineAfterHeader(string body, string keyword)
    {
        var lines = body.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) continue;
            for (var next = index + 1; next < lines.Count; next++)
            {
                var candidate = lines[next].Trim(' ', '*');
                if (candidate.Length == 0) continue;
                return candidate;
            }
        }
        return null;
    }

    private static DateOnly? DateAfterKeyword(string body, string keyword, int defaultYear)
    {
        var index = body.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var slice = body[index..Math.Min(body.Length, index + 180)];
        return FirstNumericDate(slice) ?? FirstNamedDate(slice, defaultYear);
    }

    private static DateOnly? FirstNumericDate(string value)
    {
        var match = NumericDateRegex.Match(value);
        if (!match.Success) return null;
        return SafeDate(match.Groups["day"].Value, match.Groups["month"].Value, match.Groups["year"].Value);
    }

    private static DateOnly? FirstNamedDate(string value, int defaultYear)
    {
        var match = NamedDateRegex.Match(value);
        return match.Success ? NamedMatchToDate(match, defaultYear) : null;
    }

    private static DateOnly? EarliestDate(string value, int defaultYear) =>
        AllDates(value, defaultYear).OrderBy(date => date).Cast<DateOnly?>().FirstOrDefault();

    private static DateOnly? LatestDate(string value, int defaultYear) =>
        AllDates(value, defaultYear).OrderByDescending(date => date).Cast<DateOnly?>().FirstOrDefault();

    private static IEnumerable<DateOnly> AllDates(string value, int defaultYear)
    {
        foreach (Match match in NumericDateRegex.Matches(value))
        {
            var date = SafeDate(match.Groups["day"].Value, match.Groups["month"].Value, match.Groups["year"].Value);
            if (date is not null) yield return date.Value;
        }
        foreach (Match match in NamedDateRegex.Matches(value))
        {
            var date = NamedMatchToDate(match, defaultYear);
            if (date is not null) yield return date.Value;
        }
    }

    private static DateOnly? NamedMatchToDate(Match match, int defaultYear)
    {
        if (!int.TryParse(match.Groups["day"].Value, out var day)) return null;
        if (!DateTime.TryParseExact(match.Groups["month"].Value, "MMMM", CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var monthDate)) return null;
        var year = int.TryParse(match.Groups["year"].Value, out var explicitYear) ? explicitYear : defaultYear;
        try { return new DateOnly(year, monthDate.Month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateOnly? SafeDate(string dayText, string monthText, string yearText)
    {
        if (!int.TryParse(dayText, out var day) || !int.TryParse(monthText, out var month)) return null;
        var year = int.TryParse(yearText, out var parsedYear) ? parsedYear : DateTime.UtcNow.Year;
        if (year < 100) year += 2000;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static DateOnly? ParseFlexibleNumericDate(string value, int defaultYear)
    {
        var parts = value.Split('.', '/', '-');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var day) || !int.TryParse(parts[1], out var month)) return null;
        var year = defaultYear;
        if (parts.Length > 2 && int.TryParse(parts[2], out var parsedYear)) year = parsedYear < 100 ? 2000 + parsedYear : parsedYear;
        try { return new DateOnly(year, month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static int? FirstPalletQuantity(string body)
    {
        var match = GenericPalletRegex.Match(body);
        return match.Success && int.TryParse(match.Groups["qty"].Value, out var value) ? value : null;
    }

    private static string? Match(Regex regex, string input, string group)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups[group].Value.Trim() : null;
    }

    private static string CleanDropName(string value) => Regex.Replace(value.Trim(' ', '*'), @"\s+", " ");

    private static string CleanField(string? value) => Regex.Replace((value ?? string.Empty).Trim(' ', '*'), @"\s+", " ");

    private static string CleanReference(string value) => Regex.Replace(value.Trim(), @"\s+", string.Empty).ToUpperInvariant();

    private static string CleanAddressBlock(string value)
    {
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => CleanDropName(line))
            .Where(line => line.Length > 0)
            .Where(line => !line.StartsWith("Kind regards", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return string.Join("\n", lines);
    }

    private static string? DestinationFromAddress(string addressBlock)
    {
        var lines = addressBlock.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0) return null;
        var name = lines[0];
        var town = lines.FirstOrDefault(line => line.Equals("HODDESDON", StringComparison.OrdinalIgnoreCase))
            ?? lines.Skip(1).FirstOrDefault(line => Regex.IsMatch(line, @"^[A-Z][A-Z -]{2,}$"));
        if (!string.IsNullOrWhiteSpace(town) && name.Contains(town, StringComparison.OrdinalIgnoreCase)) town = null;
        return CleanDropName(string.Join(" ", new[] { name, town }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private static string? NullIfTbc(string? value)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : CleanField(value);
        return clean is null || clean.Equals("TBC", StringComparison.OrdinalIgnoreCase) ? null : clean;
    }

    private static IReadOnlyList<string> BuildIfcoMatchKeys(DateOnly collectionDate, string? transportPo, string? cratePo, string? loadReference, string? collectionDepot, string? returningTo)
    {
        var keys = new List<string>();
        AddKey(keys, collectionDate, "TRANSPORT", transportPo);
        AddKey(keys, collectionDate, "CRATEPO", cratePo);
        AddKey(keys, collectionDate, "LOAD", loadReference);
        if (!string.IsNullOrWhiteSpace(collectionDepot) || !string.IsNullOrWhiteSpace(returningTo))
            keys.Add($"IFCO|{collectionDate:yyyy-MM-dd}|ROUTE:{SafeToken(collectionDepot ?? "TBC", 40)}>{SafeToken(returningTo ?? "TBC", 40)}");
        return keys;
    }

    private static void AddKey(List<string> keys, DateOnly collectionDate, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            keys.Add($"IFCO|{collectionDate:yyyy-MM-dd}|{label}:{SafeToken(value, 60)}");
    }

    private static string BuildReference(string baseReference, string destination)
    {
        var left = SafeToken(baseReference, 38);
        var right = SafeToken(destination, 36);
        var result = $"{left}/{right}";
        return result[..Math.Min(80, result.Length)];
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
        return clean[..Math.Min(max, clean.Length)];
    }

    private static string NaturalKey(MailboxEmailIntakeRequest request, string customer, string destination, DateOnly collectionDate, string? customerPo)
    {
        var subject = Regex.Replace(request.Subject ?? string.Empty, @"^(?:(?:RE|FW|FWD)\s*:\s*)+", string.Empty, RegexOptions.IgnoreCase).Trim().ToUpperInvariant();
        return string.Join("|", new[]
        {
            (request.SenderAddress ?? string.Empty).Trim().ToLowerInvariant(),
            subject,
            collectionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            customer.ToUpperInvariant(),
            destination.Trim().ToUpperInvariant(),
            (customerPo ?? string.Empty).Trim().ToUpperInvariant()
        });
    }

    private static DateOnly LocalDate(DateTimeOffset receivedAt)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(receivedAt, "Europe/London").DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(receivedAt.ToOffset(TimeSpan.FromHours(1)).DateTime);
        }
    }

    private static string SenderCustomer(string? sender)
    {
        var domain = (sender ?? string.Empty).Split('@').LastOrDefault() ?? "EMAIL";
        var stem = domain.Split('.').FirstOrDefault() ?? "EMAIL";
        var clean = new string(stem.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "EMAIL" : clean[..Math.Min(40, clean.Length)];
    }

    private static string InferTransferCustomer(MailboxEmailIntakeRequest request, string combined, string collection, string destination)
    {
        if (combined.Contains("IFCO", StringComparison.OrdinalIgnoreCase)) return "IFCO";

        var route = $"{combined} {collection} {destination}";
        if (route.Contains("NWF", StringComparison.OrdinalIgnoreCase) ||
            route.Contains("Natures Way", StringComparison.OrdinalIgnoreCase) ||
            route.Contains("Merston", StringComparison.OrdinalIgnoreCase) ||
            route.Contains("Drayton", StringComparison.OrdinalIgnoreCase) ||
            (request.SenderAddress ?? string.Empty).EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase))
            return "NWF";

        return SenderCustomer(request.SenderAddress);
    }
}
