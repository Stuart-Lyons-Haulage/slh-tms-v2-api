using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Deterministic extraction for customer-facing ETA and load-plan mail.
/// It produces reviewable evidence; it does not create or amend live transport orders.
/// </summary>
public sealed class CustomerCommunicationExtractionService
{
    private static readonly Regex EtaWindow = new(@"\b(?:current\s+)?eta\s*(?<from>\d{1,2}:\d{2})(?:\s*(?:to|-|until)\s*(?<to>\d{1,2}:\d{2}))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VehicleEta = new(@"\b(?:vehicle|truck)\s*(?<vehicle>\d+)\s*:\s*(?:(?<detail>.*?)(?:eta\s*)?)?(?<eta>\d{1,2}:\d{2})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Load = new(@"\bload\s*(?:number\s*)?(?<load>[A-Z0-9][A-Z0-9-]{1,20})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Pallets = new(@"\b(?<pallets>\d+)\s*pallets?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NextUpdate = new(@"\b(?:more\s+accurate\s+eta|update|updated)\s*(?:at|by)\s*(?<time>\d{1,2}:\d{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Acceptance = new(@"\b(?:accept|acceptance)\s*(?:until|up to)\s*(?<time>\d{1,2}:\d{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Url = new(@"https?://[^\s<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public CustomerCommunication Extract(MailboxEmailIntakeRequest request)
    {
        var subject = request.Subject?.Trim() ?? string.Empty;
        var body = (request.BodyText ?? string.Empty).Trim();
        var attachments = request.Attachments ?? [];
        var attachmentNames = attachments.Select(x => x.Name?.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
        var search = $"{subject}\n{body}\n{string.Join('\n', attachmentNames)}";
        var isPlan = search.Contains("load plan", StringComparison.OrdinalIgnoreCase)
            || attachmentNames.Any(x => x.Contains("load plan", StringComparison.OrdinalIgnoreCase));
        var hasEta = search.Contains("eta", StringComparison.OrdinalIgnoreCase);
        var exceptionSignals = new[] { "breakdown", "traffic", "closure", "diversion", "delay", "late", "driver hours", "m40", "m6" }
            .Where(signal => search.Contains(signal, StringComparison.OrdinalIgnoreCase)).ToList();
        var purpose = isPlan ? "LoadPlan" : hasEta ? "EtaUpdate" : exceptionSignals.Count > 0 ? "Exception" : "Other";
        var planVersion = ContainsAny(search, "amended", "amendment", "updated", "revised") ? "Amended" : "Original";
        var customerHints = KnownCustomers.Where(customer => search.Contains(customer, StringComparison.OrdinalIgnoreCase)).ToList();
        var claims = new List<CustomerEtaClaim>();

        var loadMatch = Load.Match(body);
        var palletMatch = Pallets.Match(body);
        var defaultLoad = loadMatch.Success ? loadMatch.Groups["load"].Value : null;
        var defaultPallets = palletMatch.Success && int.TryParse(palletMatch.Groups["pallets"].Value, out var palletCount) ? palletCount : (int?)null;
        var window = EtaWindow.Match(body);
        if (window.Success)
            claims.Add(new CustomerEtaClaim(defaultLoad, null, defaultPallets, window.Groups["from"].Value, window.Groups["to"].Success ? window.Groups["to"].Value : null, "Body ETA window"));
        foreach (Match match in VehicleEta.Matches(body))
            claims.Add(new CustomerEtaClaim(defaultLoad, match.Groups["vehicle"].Value, defaultPallets, match.Groups["eta"].Value, null, "Body vehicle ETA"));

        var warnings = new List<string>();
        if (purpose is "EtaUpdate" or "Exception" && claims.Count == 0) warnings.Add("ETA intent detected but no machine-readable time was found.");
        if (isPlan && attachments.Count == 0) warnings.Add("Load-plan intent detected without an attachment.");
        if (request.Mailbox is null) warnings.Add("Source mailbox was not supplied.");

        return new CustomerCommunication(
            request.MessageId,
            $"communication:{request.MessageId}",
            "PendingReview",
            purpose,
            planVersion,
            request.Mailbox,
            request.SenderAddress,
            request.SenderName,
            subject,
            request.ReceivedAtUtc,
            request.ConversationId,
            request.InternetMessageId,
            request.WebLink,
            request.ToRecipients?.GetRawText(),
            request.CcRecipients?.GetRawText(),
            customerHints,
            claims,
            exceptionSignals,
            NextUpdate.Match(body) is { Success: true } next ? next.Groups["time"].Value : null,
            Acceptance.Match(body) is { Success: true } acceptance ? acceptance.Groups["time"].Value : null,
            Url.Matches(body).Select(x => x.Value.TrimEnd('.', ',', ')')).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            attachments.Select(x => new CustomerCommunicationAttachment(x.Name, x.ContentType, x.Size, x.IsInline == true)).ToList(),
            warnings);
    }

    private static readonly string[] KnownCustomers = ["Barfoots", "APS", "NWF", "ALDI", "Waitrose", "Morrisons", "Co-op", "Sainsbury", "Langmead", "Vitacress", "Wealmoor", "Farplants", "IceLink"];
    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}

public sealed record CustomerCommunication(
    string MessageId,
    string IdempotencyKey,
    string ReviewStatus,
    string Purpose,
    string PlanVersion,
    string? Mailbox,
    string? SenderAddress,
    string? SenderName,
    string Subject,
    DateTimeOffset? ReceivedAtUtc,
    string? ConversationId,
    string? InternetMessageId,
    string? WebLink,
    string? ToRecipientsJson,
    string? CcRecipientsJson,
    IReadOnlyList<string> CustomerHints,
    IReadOnlyList<CustomerEtaClaim> Claims,
    IReadOnlyList<string> ExceptionSignals,
    string? NextUpdateLocal,
    string? AcceptanceUntilLocal,
    IReadOnlyList<string> TrackingLinks,
    IReadOnlyList<CustomerCommunicationAttachment> Attachments,
    IReadOnlyList<string> Warnings);

public sealed record CustomerEtaClaim(string? LoadReference, string? VehicleNumber, int? Pallets, string EtaFromLocal, string? EtaToLocal, string Evidence);
public sealed record CustomerCommunicationAttachment(string? Name, string? ContentType, long? Size, bool IsInline);
