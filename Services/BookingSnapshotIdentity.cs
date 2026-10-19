using System.Text.Json;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Identity for rows of a "full booking" workbook: a customer file that lists every
/// depot for a delivery date (Greenhouse, Barfoots and Langmead Aldi bookings). When
/// the customer resends the file, the newer file is the whole booking, so a row is the
/// same order if customer, delivery date, sender company, collection site and depot
/// match - whoever at that company sent it and whatever the pallet count now is.
/// </summary>
public static class BookingSnapshotIdentity
{
    private static readonly HashSet<string> FullBookingProfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Greenhouse Aldi workbook",
        "BARFOOTS_ALDI_CONFIRMED_WORKBOOK",
        "LANGMEADS_ALDI_ORDERS_WORKBOOK"
    };

    /// <summary>The booking (file) a row belongs to, or null if the row is not from a full-booking profile.</summary>
    public static string? Booking(JsonElement payload)
    {
        var profile = new[] { Text(payload, "intakeProfile"), Text(payload, "intakeParser") }
            .FirstOrDefault(value => value is not null && FullBookingProfiles.Contains(value));
        var customer = Text(payload, "customerCode");
        var deliveryDate = Text(payload, "deliveryDate") ?? Text(payload, "collectionDate");
        var domain = SenderDomain(Text(payload, "sourceSender"));
        if (profile is null || customer is null || deliveryDate is null || domain is null) return null;
        return $"{Key(profile)}|{Key(customer)}|{deliveryDate}|{domain}";
    }

    /// <summary>The row's identity within its booking, or null if not a full-booking row.</summary>
    public static string? Row(JsonElement payload)
    {
        var booking = Booking(payload);
        if (booking is null) return null;
        var collection = Collection(payload);
        var destination = Text(payload, "stallNumber") ?? Text(payload, "destination") ?? Text(payload, "deliverySite");
        if (collection is null || destination is null) return null;
        return $"{booking}|{Key(collection)}|{Key(destination)}";
    }

    public static string? Collection(JsonElement payload) =>
        Text(payload, "sellerName") ?? Text(payload, "collectionSite") ?? Text(payload, "collectionPoint");

    /// <summary>Normalised collection site, for comparing rows across files.</summary>
    public static string? CollectionKey(JsonElement payload) => Collection(payload) is { } value ? Key(value) : null;

    private static string? SenderDomain(string? sender)
    {
        if (string.IsNullOrWhiteSpace(sender)) return null;
        var at = sender.LastIndexOf('@');
        return at < 0 ? null : sender[(at + 1)..].Trim().ToLowerInvariant();
    }

    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object &&
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
}
