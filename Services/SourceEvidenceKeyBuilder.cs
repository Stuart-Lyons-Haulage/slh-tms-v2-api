using System.Security.Cryptography;
using System.Text;

namespace Slh.Tms.Api.Services;

/// <summary>Builds a stable identity for retained mailbox evidence.</summary>
public static class SourceEvidenceKeyBuilder
{
    public static string For(string messageId, string? internetMessageId)
    {
        if (string.IsNullOrWhiteSpace(internetMessageId)) return Legacy(messageId);
        var identity = $"{messageId}\n{internetMessageId}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        return $"email-evidence:{hash}";
    }

    public static string Legacy(string messageId)
    {
        var key = $"email-evidence:{Compact(messageId)}";
        return key.Length <= 200 ? key : key[..200];
    }

    private static string Compact(string value)
    {
        var compact = new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray());
        return compact.Length > 96 ? compact[^96..] : compact;
    }
}
