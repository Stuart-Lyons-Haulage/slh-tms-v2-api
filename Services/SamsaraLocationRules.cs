namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps internal numeric references out of Samsara address fields. Samsara
/// geocodes those fields, so a numeric transport/site key is not a usable place.
/// </summary>
public static class SamsaraLocationRules
{
    public static bool HasUsableCoordinates(double? latitude, double? longitude) =>
        latitude is >= -90 and <= 90 &&
        longitude is >= -180 and <= 180 &&
        !(latitude.Value == 0 && longitude.Value == 0);

    public static bool HasUsableCoordinates(decimal? latitude, decimal? longitude) =>
        latitude is >= -90 and <= 90 &&
        longitude is >= -180 and <= 180 &&
        !(latitude.Value == 0 && longitude.Value == 0);

    public static bool IsLongNumericReference(string? value) =>
        value is { Length: >= 10 } && value.All(char.IsDigit);

    public static bool IsUsablePhysicalAddress(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !IsLongNumericReference(value.Trim());

    public static string ResolvePhysicalAddress(string? stopAddress, string? siteAddress, string stopName)
    {
        var suppliedAddress = stopAddress?.Trim();
        if (IsUsablePhysicalAddress(suppliedAddress)) return suppliedAddress!;

        var masterAddress = siteAddress?.Trim();
        if (IsUsablePhysicalAddress(masterAddress)) return masterAddress!;

        return IsLongNumericReference(suppliedAddress) ? string.Empty : stopName;
    }
}
