using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public sealed class NwfQuantityChangeParser
{
    private static readonly Regex DateRegex = new(
        @"\b(?<day>0?[1-9]|[12]\d|3[01])[./-](?<month>0?[1-9]|1[0-2])[./-](?<year>20\d{2}|\d{2})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OrderRefRegex = new(
        @"\border\s+ref\.?\s*(?<site>[A-Z][A-Z\s-]{2,40})\s+(?<ref>\d{5,})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PalletsToDepotRegex = new(
        @"\b(?<qty>\d{1,3})\s+pallets?\s+to\s+(?<depot>Drayton|Merston|Runcton|Selsey)\b|\b(?<qty2>\d{1,3})\s+(?<depot2>Drayton|Merston|Runcton|Selsey)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PoRegex = new(@"\bPO\d{5,}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress ?? string.Empty;
        var subject = request.Subject ?? string.Empty;
        if (!sender.EndsWith("@nwfltd.co.uk", StringComparison.OrdinalIgnoreCase) ||
            !subject.Contains("Change in Delivery Quantities", StringComparison.OrdinalIgnoreCase))
            return null;

        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var source = $"{subject}\n{body}";
        var dates = DateRegex.Matches(source).Cast<Match>().Select(ParseDate).Where(date => date is not null).Select(date => date!.Value).Distinct().ToList();
        var deliveryDate = dates.Count >= 2 ? dates[1] : dates.FirstOrDefault();
        if (deliveryDate == default)
        {
            return new EmailIntakeParseResult(
                [],
                ["NWF quantity-change email was recognised but no delivery date could be read."],
                "NWF quantity-change email needs manual review because the date was missing.");
        }

        var orderRef = OrderRefRegex.Match(source);
        var loadingPlace = orderRef.Success ? Clean(orderRef.Groups["site"].Value) : null;
        var customerRef = orderRef.Success ? orderRef.Groups["ref"].Value.Trim() : null;
        var purchaseOrders = PoRegex.Matches(source).Cast<Match>().Select(match => match.Value.ToUpperInvariant()).Distinct().ToList();
        var transportPo = purchaseOrders.FirstOrDefault();
        var productPo = purchaseOrders.Skip(1).FirstOrDefault();

        var depotSplits = PalletsToDepotRegex.Matches(source)
            .Cast<Match>()
            .Select(match => (
                Depot: Clean(match.Groups["depot"].Success ? match.Groups["depot"].Value : match.Groups["depot2"].Value),
                Pallets: int.Parse(match.Groups["qty"].Success ? match.Groups["qty"].Value : match.Groups["qty2"].Value, CultureInfo.InvariantCulture)))
            .Where(split => split.Pallets > 0 && !string.IsNullOrWhiteSpace(split.Depot))
            .GroupBy(split => split.Depot, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (depotSplits.Count == 0)
        {
            return new EmailIntakeParseResult(
                [],
                ["NWF quantity-change email was recognised but no depot pallet split could be read."],
                "NWF quantity-change email needs manual review because the depot split was missing.");
        }

        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(loadingPlace)) warnings.Add("Loading place/order-ref site was not explicit.");
        if (string.IsNullOrWhiteSpace(customerRef)) warnings.Add("Customer order reference was not explicit.");
        if (purchaseOrders.Count == 0) warnings.Add("No NWF PO reference was read from the quantity-change email.");

        var orders = new List<ParsedEmailOrder>();
        foreach (var split in depotSplits)
        {
            var matchKeys = BuildMatchKeys(deliveryDate, transportPo, productPo, loadingPlace, customerRef);
            var depotToken = Normalise(split.Depot);
            var loadingToken = Normalise(loadingPlace);
            var referenceRoot = productPo ?? transportPo ?? customerRef ?? $"NWF-{deliveryDate:yyyyMMdd}-{loadingToken}";
            var reference = $"{SafeToken(referenceRoot, 42)}/{SafeToken(loadingPlace ?? "NWF", 16)}/{SafeToken(split.Depot, 16)}";
            var naturalKey = $"NWFCHANGE|{deliveryDate:yyyy-MM-dd}|{Normalise(customerRef)}|{loadingToken}|{depotToken}";
            var instructions = string.Join(" · ", new[]
            {
                "Order type: NWF delivery quantity change",
                string.IsNullOrWhiteSpace(transportPo) ? null : $"Transport PO: {transportPo}",
                string.IsNullOrWhiteSpace(productPo) ? null : $"Product PO: {productPo}",
                string.IsNullOrWhiteSpace(customerRef) ? null : $"Order ref: {customerRef}",
                string.IsNullOrWhiteSpace(loadingPlace) ? null : $"Loading place: {loadingPlace}",
                $"Corrected depot split: {split.Pallets} pallets to {split.Depot}",
                $"Source email: {request.Subject}",
                warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            var payload = new Dictionary<string, object?>
            {
                ["poNumber"] = reference[..Math.Min(reference.Length, 80)],
                ["customerPo"] = productPo ?? transportPo,
                ["customerCode"] = "NWF",
                ["collectionDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["deliveryDate"] = deliveryDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["pallets"] = split.Pallets,
                ["sellerName"] = loadingPlace,
                ["marketName"] = "NWF",
                ["stallNumber"] = split.Depot,
                ["jobType"] = "NWF quantity change",
                ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
                ["transportPo"] = transportPo,
                ["productPo"] = productPo,
                ["customerRef"] = customerRef,
                ["plannerReady"] = true,
                ["intakeStatus"] = "ReadyForReview",
                ["intakeNaturalKey"] = naturalKey,
                ["intakeMatchKeys"] = matchKeys,
                ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
                ["intakeWarnings"] = warnings,
                ["intakeParser"] = "NWF Quantity Change",
                ["sourceMessageId"] = request.MessageId,
                ["sourceInternetMessageId"] = request.InternetMessageId,
                ["sourceSender"] = request.SenderAddress,
                ["sourceSenderName"] = request.SenderName,
                ["sourceSubject"] = request.Subject,
                ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                ["sourceWebLink"] = request.WebLink
            };

            orders.Add(new ParsedEmailOrder(
                $"nwf-quantity-change-{Normalise(customerRef)}-{depotToken}",
                naturalKey,
                JsonSerializer.SerializeToElement(payload),
                warnings));
        }

        return new EmailIntakeParseResult(orders, warnings, null);
    }

    private static IReadOnlyList<string> BuildMatchKeys(DateOnly date, string? transportPo, string? productPo, string? loadingPlace, string? customerRef)
    {
        var keys = new List<string>();
        AddKey(keys, date, "PRODUCT", productPo);
        AddKey(keys, date, "TRANSPORT", transportPo);
        AddKey(keys, date, "LOADING", loadingPlace);
        AddKey(keys, date, "ORDERREF", customerRef);
        return keys.Count == 0 ? [$"NWF|QUANTITYCHANGE|{date:yyyy-MM-dd}|UNKNOWN"] : keys;
    }

    private static void AddKey(List<string> keys, DateOnly date, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var key = $"NWF|{date:yyyy-MM-dd}|{type}:{Normalise(value)}";
        if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase)) keys.Add(key);
    }

    private static DateOnly? ParseDate(Match match)
    {
        var yearText = match.Groups["year"].Value;
        var year = yearText.Length == 2 ? 2000 + int.Parse(yearText, CultureInfo.InvariantCulture) : int.Parse(yearText, CultureInfo.InvariantCulture);
        try { return new DateOnly(year, int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Clean(string? value) => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
    private static string SafeToken(string value, int max)
    {
        var clean = Regex.Replace(value.ToUpperInvariant(), @"[^A-Z0-9/-]+", "-", RegexOptions.None).Trim('-');
        if (clean.Length == 0) clean = "ORDER";
        return clean[..Math.Min(clean.Length, max)];
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
    {
        var input = !string.IsNullOrWhiteSpace(bodyText) ? bodyText! : bodyHtml ?? string.Empty;
        input = Regex.Replace(input, @"(?i)<br\s*/?>|</p>|</div>|</tr>|</li>", "\n");
        input = Regex.Replace(input, "<[^>]+>", " ");
        input = WebUtility.HtmlDecode(input).Replace("**", string.Empty, StringComparison.Ordinal);
        input = Regex.Replace(input, @"[ \t]+", " ");
        input = Regex.Replace(input, @"\r?\n[ \t]*", "\n");
        return input.Trim();
    }
}
