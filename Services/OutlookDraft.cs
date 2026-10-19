using System.Text;

namespace Slh.Tms.Api.Services;

/// <summary>
/// A ready-to-send email as an .eml file. "X-Unsent: 1" makes Outlook open it as a new
/// message (not a received one), so the planner just checks it and presses Send from their
/// own mailbox, with the PDF already attached. No From is set: Outlook uses the planner's
/// account, as the team's manual emails do today.
/// </summary>
public static class OutlookDraft
{
    public static byte[] Build(IEnumerable<string> to, IEnumerable<string> cc, string subject, string htmlBody, string attachmentName, byte[] attachment)
    {
        var boundary = $"slh-{Guid.NewGuid():N}";
        var b = new StringBuilder();
        b.Append("X-Unsent: 1\r\n");
        b.Append($"To: {string.Join(", ", to.Select(Address))}\r\n");
        var ccList = cc.Select(Address).ToList();
        if (ccList.Count > 0) b.Append($"Cc: {string.Join(", ", ccList)}\r\n");
        b.Append($"Subject: {Encode(subject)}\r\n");
        b.Append("MIME-Version: 1.0\r\n");
        b.Append($"Content-Type: multipart/mixed; boundary=\"{boundary}\"\r\n\r\n");

        b.Append($"--{boundary}\r\n");
        b.Append("Content-Type: text/html; charset=utf-8\r\n");
        b.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        b.Append(Lines(Convert.ToBase64String(Encoding.UTF8.GetBytes($"<html><body style=\"font-family:Calibri,Arial,sans-serif;font-size:11pt\">{htmlBody}</body></html>"))));

        var name = attachmentName.Replace("\"", "'", StringComparison.Ordinal);
        b.Append($"--{boundary}\r\n");
        b.Append($"Content-Type: application/pdf; name=\"{name}\"\r\n");
        b.Append($"Content-Disposition: attachment; filename=\"{name}\"\r\n");
        b.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        b.Append(Lines(Convert.ToBase64String(attachment)));
        b.Append($"--{boundary}--\r\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }

    /// <summary>Plain-text paragraphs (with \n) as simple HTML.</summary>
    public static string TextToHtml(string text) =>
        string.Join("<br>", System.Net.WebUtility.HtmlEncode(text).Split('\n'));

    private static string Address(string value) => value.Trim().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);

    private static string Encode(string value) =>
        value.All(character => character is >= ' ' and <= '~')
            ? value
            : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";

    private static string Lines(string base64)
    {
        var b = new StringBuilder();
        for (var index = 0; index < base64.Length; index += 76)
            b.Append(base64, index, Math.Min(76, base64.Length - index)).Append("\r\n");
        return b.ToString();
    }
}
