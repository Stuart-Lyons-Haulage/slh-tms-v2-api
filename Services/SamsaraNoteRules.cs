using System.Text;

namespace Slh.Tms.Api.Services;

public static class SamsaraNoteRules
{
    // Samsara caps job/route notes at 2,000 - counted as UTF-8 bytes, not .NET
    // characters. Notes use "·" separators (2 bytes each in UTF-8) and other
    // non-ASCII text, so a 2,000-character note can be ~2,040 bytes and Samsara
    // rejects the whole route. Limit by byte length, not character length.
    public const int MaximumJobNoteBytes = 2000;

    public static string LimitJobNotes(string notes)
    {
        if (Encoding.UTF8.GetByteCount(notes) <= MaximumJobNoteBytes)
            return notes;

        const string suffix = "\n[Additional job notes omitted by SLH TMS: Samsara limit is 2,000 characters]";
        var budget = MaximumJobNoteBytes - Encoding.UTF8.GetByteCount(suffix);
        return TruncateToUtf8Bytes(notes, budget).TrimEnd() + suffix;
    }

    private static string TruncateToUtf8Bytes(string value, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length <= maxBytes) return value;
        var length = maxBytes;
        // Never cut through a multi-byte sequence: UTF-8 continuation bytes are 10xxxxxx.
        while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
