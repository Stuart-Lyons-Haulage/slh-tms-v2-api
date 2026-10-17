namespace Slh.Tms.Api.Services;

internal static class CustomerCodeMatcher
{
    public static bool Equivalent(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        Normalize(left) == Normalize(right);

    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
