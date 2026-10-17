using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>
/// The part of an email its sender actually wrote in this message: the full body
/// (plain text or HTML) cut at the start of quoted reply history. Decisions such as
/// "does this email contain an order?" must use this, not the whole thread, or every
/// reply that quotes an earlier order looks like a new order.
/// </summary>
public static class MailboxOwnContent
{
    // Outlook ("From: … Sent: …", "-----Original Message-----", underscore rule),
    // Gmail/Apple ("On … wrote:") and forwarded-message markers.
    private static readonly Regex QuotedHistoryStart = new(
        @"^(?:from|sent)\s*:\s|^-{2,}\s*original\s+message|^_{8,}$|^on\s.{4,200}\bwrote:$|^begin\s+forwarded\s+message",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ReplyPrefix = new(@"^\s*(?:re|aw|sv)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ForwardPrefix = new(@"^\s*(?:fw|fwd)\s*:", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Own text of the message. Forwards are returned whole: the forwarded
    /// part is the content the sender is passing on.</summary>
    public static string Body(MailboxEmailIntakeRequest request)
    {
        var body = MailboxBodyNormalizer.Normalize(request.BodyText, request.BodyHtml);
        if (IsForward(request.Subject)) return body;

        var kept = new List<string>();
        foreach (var raw in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = Regex.Replace(raw, @"\s+", " ").Trim();
            if (QuotedHistoryStart.IsMatch(line)) break;
            kept.Add(line);
        }
        return string.Join("\n", kept).Trim();
    }

    /// <summary>Subject words that can count as order evidence. A reply's subject is
    /// inherited from the original order, so it is not evidence of a new order.</summary>
    public static string EvidenceSubject(string? subject) => IsReply(subject) ? string.Empty : subject ?? string.Empty;

    public static bool IsReply(string? subject) => ReplyPrefix.IsMatch(subject ?? string.Empty);

    public static bool IsForward(string? subject) => ForwardPrefix.IsMatch(subject ?? string.Empty);
}
