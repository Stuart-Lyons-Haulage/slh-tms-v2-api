using System.Net;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

/// <summary>Reads the complete body rather than the Outlook bodyPreview field.</summary>
public static class MailboxBodyNormalizer
{
    public static string Normalize(string? bodyText, string? bodyHtml)
    {
        if (string.IsNullOrWhiteSpace(bodyHtml))
            return RemoveDuplicateStandaloneTownLines(bodyText?.Trim() ?? string.Empty);

        // Preserve rows and cell boundaries so HTML tables and multi-drop lists
        // remain parseable. Do not concatenate the preview: it duplicates rows.
        var text = Regex.Replace(bodyHtml, @"<(script|style)\b[^>]*>.*?</\1\s*>", "",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, @"</t[dh]>\s*<t[dh]\b[^>]*>", " | ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</(?:p|div|tr|li|h[1-6])\s*>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text).Replace('\u00a0', ' ');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"[ \t]*\r?\n[ \t]*", "\n").Trim();
        return string.IsNullOrWhiteSpace(text) ? RemoveDuplicateStandaloneTownLines(bodyText?.Trim() ?? string.Empty) : RemoveDuplicateStandaloneTownLines(text);
    }

    private static string RemoveDuplicateStandaloneTownLines(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var kept = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (IsStandaloneCountryLine(trimmed))
                continue;

            var isStandaloneTown = trimmed.Length >= 3
                && trimmed.Length <= 40
                && Regex.IsMatch(trimmed, @"^[A-Z][A-Z -]+$");

            if (isStandaloneTown && kept.TakeLast(6).Any(previous => previous.Contains(trimmed, StringComparison.OrdinalIgnoreCase)))
                continue;

            kept.Add(line);
        }

        return string.Join("\n", kept).Trim();
    }

    private static bool IsStandaloneCountryLine(string value) =>
        value.Equals("UNITED KINGDOM", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("UK", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("GREAT BRITAIN", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("ENGLAND", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("SCOTLAND", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("WALES", StringComparison.OrdinalIgnoreCase);
}
