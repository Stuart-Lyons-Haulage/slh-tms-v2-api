using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public sealed class NwfDailyTrackerParser
{
    private static readonly Regex RowRegex = new(
        @"(?m)^(?<date>\d{1,2}/\d{1,2}/\d{4})\|(?<transport>[^|]*)\|(?<loadref>[^|]*)\|(?<product>[^|]*)\|(?<loading>[^|]*)\|(?<drayton>[^|]*)\|(?<merston>[^|]*)\|(?<runcton>[^|]*)\|(?<selsey>[^|]*)\|(?<total>[^|]*)\|(?<used>[^|]*)\|(?<crate>[^|]*)\|(?<round>[^|]*)\|(?<comments>[^\r\n]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public EmailIntakeParseResult? TryParse(MailboxEmailIntakeRequest request)
    {
        var subject = request.Subject ?? string.Empty;
        if (!subject.Contains("SLH DAILY TRACKER", StringComparison.OrdinalIgnoreCase) &&
            !subject.Contains("NWF DAILY TRACKER", StringComparison.OrdinalIgnoreCase))
            return null;

        var body = NormaliseBody(request.BodyText, request.BodyHtml);
        var matches = RowRegex.Matches(body).Cast<Match>().ToList();
        if (matches.Count == 0)
        {
            return new EmailIntakeParseResult(
                [],
                ["NWF Daily Tracker email detected but no structured tracker rows were found in the email body."],
                "NWF tracker needs manual review because the structured rows were not available in the email body.");
        }

        var orders = new List<ParsedEmailOrder>();
        foreach (var match in matches)
        {
            if (!DateOnly.TryParseExact(match.Groups["date"].Value.Trim(), "d/M/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                !DateOnly.TryParseExact(match.Groups["date"].Value.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                continue;

            var transportPo = Clean(match.Groups["transport"].Value);
            var loadRef = Clean(match.Groups["loadref"].Value);
            var productPo = Clean(match.Groups["product"].Value);
            var loadingPlace = Clean(match.Groups["loading"].Value);
            var totalSpaces = Clean(match.Groups["total"].Value);
            var usedSpaces = Clean(match.Groups["used"].Value);
            var crateSite = Clean(match.Groups["crate"].Value);
            var roundTrip = Clean(match.Groups["round"].Value);
            var comments = Clean(match.Groups["comments"].Value);

            if (string.IsNullOrWhiteSpace(loadingPlace)) continue;

            var destinations = new[]
            {
                (Name: "Drayton", Value: match.Groups["drayton"].Value),
                (Name: "Merston", Value: match.Groups["merston"].Value),
                (Name: "Runcton", Value: match.Groups["runcton"].Value),
                (Name: "Selsey", Value: match.Groups["selsey"].Value),
            };

            foreach (var destination in destinations)
            {
                if (!int.TryParse(Clean(destination.Value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pallets) || pallets <= 0)
                    continue;

                var warnings = new List<string>();
                if (string.IsNullOrWhiteSpace(transportPo))
                    warnings.Add("Transport PO is blank on the NWF tracker row.");
                if (comments.Contains("crate", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("Crate-return instruction is present. Confirm whether the row represents produce, crate return, or both before acceptance.");

                var sourceRef = !string.IsNullOrWhiteSpace(loadRef)
                    ? loadRef
                    : !string.IsNullOrWhiteSpace(transportPo)
                        ? transportPo
                        : productPo;
                if (string.IsNullOrWhiteSpace(sourceRef))
                    sourceRef = $"NWF-{date:yyyyMMdd}-{Normalise(loadingPlace)}";

                var orderReference = BuildReference(sourceRef, destination.Name);
                var naturalKey = $"nwf|{date:yyyy-MM-dd}|{Normalise(loadRef)}|{Normalise(transportPo)}|{Normalise(productPo)}|{Normalise(destination.Name)}";
                var instructionParts = new[]
                {
                    "Order type: NWF inbound",
                    string.IsNullOrWhiteSpace(transportPo) ? null : $"Transport PO: {transportPo}",
                    string.IsNullOrWhiteSpace(loadRef) ? null : $"Load ref: {loadRef}",
                    string.IsNullOrWhiteSpace(productPo) ? null : $"Product PO: {productPo}",
                    string.IsNullOrWhiteSpace(totalSpaces) ? null : $"Total pallet spaces: {totalSpaces}",
                    string.IsNullOrWhiteSpace(usedSpaces) ? null : $"Pallet spaces used: {usedSpaces}",
                    string.IsNullOrWhiteSpace(crateSite) ? null : $"Crate collection site: {crateSite}",
                    string.IsNullOrWhiteSpace(roundTrip) ? null : $"Round trip: {roundTrip}",
                    string.IsNullOrWhiteSpace(comments) ? null : $"NWF comments: {comments}",
                    $"Source email: {request.Subject}",
                    warnings.Count == 0 ? null : $"Intake warning: {string.Join("; ", warnings)}"
                };
                var instructions = string.Join(" · ", instructionParts.Where(value => !string.IsNullOrWhiteSpace(value)));

                var payload = new Dictionary<string, object?>
                {
                    ["poNumber"] = orderReference,
                    ["customerPo"] = transportPo,
                    ["customerCode"] = "NWF",
                    ["collectionDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["deliveryDate"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["pallets"] = pallets,
                    ["sellerName"] = loadingPlace,
                    ["marketName"] = "NWF",
                    ["stallNumber"] = destination.Name,
                    ["jobType"] = "NWF inbound",
                    ["driverInstructions"] = instructions.Length <= 1000 ? instructions : instructions[..1000],
                    ["sourceMessageId"] = request.MessageId,
                    ["sourceInternetMessageId"] = request.InternetMessageId,
                    ["sourceSender"] = request.SenderAddress,
                    ["sourceSenderName"] = request.SenderName,
                    ["sourceSubject"] = request.Subject,
                    ["sourceReceivedAtUtc"] = request.ReceivedAtUtc,
                    ["sourceWebLink"] = request.WebLink,
                    ["sourceAttachmentName"] = (request.Attachments ?? []).FirstOrDefault(item => item.IsInline != true)?.Name,
                    ["intakeNaturalKey"] = naturalKey,
                    ["intakeConfidence"] = warnings.Count == 0 ? "High" : "Medium",
                    ["intakeWarnings"] = warnings,
                    ["intakeParser"] = "NWF Daily Tracker"
                };

                orders.Add(new ParsedEmailOrder(
                    $"nwf-{Normalise(sourceRef)}-{Normalise(destination.Name)}",
                    naturalKey,
                    JsonSerializer.SerializeToElement(payload),
                    warnings));
            }
        }

        return orders.Count > 0
            ? new EmailIntakeParseResult(orders, [], null)
            : new EmailIntakeParseResult([], ["NWF tracker rows were found but no positive depot pallet allocations were identified."], "NWF tracker requires manual review because no depot allocations could be staged safely.");
    }

    private static string NormaliseBody(string? bodyText, string? bodyHtml)
    {
        var input = !string.IsNullOrWhiteSpace(bodyText) ? bodyText! : bodyHtml ?? string.Empty;
        input = Regex.Replace(input, @"(?i)<br\s*/?>|</p>|</div>|</tr>|</li>", "\n");
        input = Regex.Replace(input, @"<[^>]+>", " ");
        input = WebUtility.HtmlDecode(input).Replace("**", string.Empty, StringComparison.Ordinal);
        input = Regex.Replace(input, @"[ \t]+", " ");
        input = Regex.Replace(input, @"\r?\n[ \t]*", "\n");
        return input.Trim();
    }

    private static string Clean(string? value) => Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

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
}
