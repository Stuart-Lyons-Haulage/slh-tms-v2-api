using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public enum OrderSourceDirection
{
    Inbound,
    ForwardedInbound,
    OutboundLyonsPlan,
    InternalOutbound,
    NonOrder
}

public sealed record OrderSourceClassification(
    OrderSourceDirection Direction,
    string? EffectiveSender,
    bool IsOrderCandidate,
    string Reason);

/// <summary>
/// Classifies source direction independently from the customer parser. This prevents
/// an outbound Lyons plan becoming demand, while preserving a customer instruction
/// that a Lyons colleague has forwarded into the intake mailbox.
/// </summary>
public static partial class OrderSourceDirectionClassifier
{
    [GeneratedRegex(@"(?im)^\s*(?:from|sender)\s*:\s*(?:[^<\r\n]*<)?(?<email>[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})(?:>)?\s*$")]
    private static partial Regex ForwardedFromRegex();

    [GeneratedRegex(@"(?i)^(?:FW|FWD)\s*:")]
    private static partial Regex ForwardedSubjectRegex();

    [GeneratedRegex(@"(?i)\b(order|booking|collect(?:ion)?|deliver(?:y)?|backhaul|return load|empty (?:tray|crate|pallet)|transfer|trans[ -]?ship|pallet|quantity|qty|cancel|amend|revis(?:e|ed|ion))\b")]
    private static partial Regex InstructionSignalRegex();

    public static OrderSourceClassification Classify(MailboxEmailIntakeRequest request)
    {
        var sender = request.SenderAddress?.Trim();
        var internalSender = IsLyons(sender);
        var subject = request.Subject?.Trim() ?? string.Empty;
        var body = string.Join("\n", request.BodyText, request.BodyHtml);
        var names = string.Join("\n", (request.Attachments ?? [])
            .Where(x => x.IsInline != true).Select(x => x.Name));
        var combined = string.Join("\n", subject, body, names);

        if (internalSender && IsPlannerSnapshot(subject, names))
            return new(OrderSourceDirection.OutboundLyonsPlan, null, false,
                "Lyons collection/load plan is comparison evidence only.");

        if (internalSender && TryExternalForwardedSender(subject, body, out var forwardedSender))
            return new(OrderSourceDirection.ForwardedInbound, forwardedSender,
                InstructionSignalRegex().IsMatch(combined) || HasOperationalAttachment(request),
                "External customer instruction forwarded internally.");

        if (internalSender)
            return new(OrderSourceDirection.InternalOutbound, null, false,
                "Internal/outbound Lyons message; retained as evidence only.");

        var candidate = InstructionSignalRegex().IsMatch(combined) || HasOperationalAttachment(request);
        return candidate
            ? new(OrderSourceDirection.Inbound, sender, true, "External inbound instruction candidate.")
            : new(OrderSourceDirection.NonOrder, sender, false, "No transport instruction signal found.");
    }

    public static bool IsLyons(string? address) =>
        !string.IsNullOrWhiteSpace(address) &&
        (address.EndsWith("@lyonshaulage.com", StringComparison.OrdinalIgnoreCase) ||
         address.EndsWith("@stuartlyonshaulage.co.uk", StringComparison.OrdinalIgnoreCase));

    public static string ExtractForwardedInstruction(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return body ?? string.Empty;
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var fromIndex = Array.FindIndex(lines, line => ForwardedFromRegex().IsMatch(line));
        if (fromIndex < 0) return body;

        var contentIndex = fromIndex + 1;
        while (contentIndex < lines.Length)
        {
            var trimmed = lines[contentIndex].Trim();
            if (trimmed.Length == 0)
            {
                contentIndex++;
                break;
            }
            if (!Regex.IsMatch(trimmed, @"^(?:sent|to|cc|subject|date)\s*:", RegexOptions.IgnoreCase))
                break;
            contentIndex++;
        }
        return string.Join("\n", lines.Skip(contentIndex)).Trim();
    }

    private static bool TryExternalForwardedSender(string subject, string body, out string? sender)
    {
        sender = null;
        if (!ForwardedSubjectRegex().IsMatch(subject) &&
            !body.Contains("original message", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("forwarded message", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (Match match in ForwardedFromRegex().Matches(body))
        {
            var candidate = match.Groups["email"].Value.Trim();
            if (!IsLyons(candidate))
            {
                sender = candidate;
                return true;
            }
        }
        return false;
    }

    private static bool IsPlannerSnapshot(string subject, string names)
    {
        if (subject.Contains("LYONS COLLECTIONS", StringComparison.OrdinalIgnoreCase) ||
            subject.StartsWith("SB ", StringComparison.OrdinalIgnoreCase) ||
            subject.StartsWith("SB-", StringComparison.OrdinalIgnoreCase) ||
            subject.StartsWith("SB_", StringComparison.OrdinalIgnoreCase) ||
            subject.StartsWith("SOUTHBOUND", StringComparison.OrdinalIgnoreCase))
            return true;

        return names.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(name =>
                (name.StartsWith("SB ", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("SB-", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("SB_", StringComparison.OrdinalIgnoreCase) ||
                 name.StartsWith("SOUTHBOUND", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("LYONS COLLECTION", StringComparison.OrdinalIgnoreCase)) &&
                (name.Contains("BOOKING", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("LOAD PLAN", StringComparison.OrdinalIgnoreCase) ||
                 name.Contains("COLLECTION", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool HasOperationalAttachment(MailboxEmailIntakeRequest request) =>
        (request.Attachments ?? []).Any(x => x.IsInline != true &&
            Path.GetExtension(x.Name ?? string.Empty).ToLowerInvariant() is ".xlsx" or ".xls" or ".xlsm" or ".csv" or ".pdf");
}
