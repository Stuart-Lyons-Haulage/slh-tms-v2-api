namespace Slh.Tms.Api.Services;

public static class PalletHandlingRules
{
    public static PalletHandlingResult Resolve(string? customer, string? collectionSite, string? destination, string? sourceUnitType)
    {
        var customerKey = Normalise(customer);
        var collectionKey = Normalise(collectionSite);
        var destinationKey = Normalise(destination);
        var sourceKey = Normalise(sourceUnitType);

        // Handling-unit classification always happens before pallet rules. This prevents trays/crates/trolleys
        // being forced into pallet capacity logic just because a source quantity uses the pallets field.
        if (ContainsAny(sourceKey, "TRAY", "TRAYS")) return NonPallet("Tray", "tray", "Source unit type");
        if (ContainsAny(sourceKey, "CRATE", "CRATES")) return NonPallet("Crate", "crate", "Source unit type");
        if (ContainsAny(sourceKey, "TROLLEY", "TROLLEYS", "DOLLY", "DOLLIES")) return NonPallet("Trolley", "trolley", "Source unit type");
        if (ContainsAny(sourceKey, "MIXED")) return NonPallet("Mixed", "mixed", "Source unit type");

        // Customer rules are authoritative where the business has defined an explicit pallet standard.
        if (ContainsAny(customerKey, "MORRISONS", "MORRISON") || ContainsAny(destinationKey, "MORRISONS", "MORRISON"))
            return Pallet("Standard", "standard", "Customer rule: Morrisons");

        if (ContainsAny(customerKey, "WAITROSE", "WEIGHTROSE") || ContainsAny(destinationKey, "WAITROSE", "WEIGHTROSE"))
            return Pallet("Standard", "standard", "Customer rule: Waitrose");

        var isAldi = ContainsAny(customerKey, "ALDI") || ContainsAny(destinationKey, "ALDI");
        var isBarefoots = ContainsAny(collectionKey, "BAREFOOT", "BAREFOOTS");
        var isNwf = ContainsAny(collectionKey, "NWF");
        var isLangmeads = ContainsAny(collectionKey, "LANGMEAD", "LANGMEADS", "HAMFARM");
        var isAtherstone = ContainsAny(destinationKey, "ATHERSTONE");

        if (isAldi && (isBarefoots || isNwf))
            return Pallet("Euro", "euro", isBarefoots ? "Site/customer rule: Barefoots + Aldi" : "Site/customer rule: NWF + Aldi");

        if (isLangmeads && isAldi && isAtherstone)
            return Pallet("Euro", "euro", "Destination override: Langmeads + Aldi Atherstone");

        if (isLangmeads)
            return Pallet("Standard", "standard", "Site default: Langmeads");

        // Preserve a valid explicit source pallet type when no business rule overrides it.
        if (ContainsAny(sourceKey, "EURO", "EUR")) return Pallet("Euro", "euro", "Source pallet type");
        if (ContainsAny(sourceKey, "STANDARD", "STD")) return Pallet("Standard", "standard", "Source pallet type");

        // If the source says pallet but not which format, retain it as a pallet with unknown format.
        if (ContainsAny(sourceKey, "PALLET", "PALLETS"))
            return new PalletHandlingResult("Pallet", null, "unknown", "Pallet format not mapped", true);

        return new PalletHandlingResult("Unknown", null, "unknown", "No handling-unit rule matched", false);
    }

    private static PalletHandlingResult Pallet(string palletType, string colourKey, string ruleSource) =>
        new("Pallet", palletType, colourKey, ruleSource, true);

    private static PalletHandlingResult NonPallet(string loadUnitType, string colourKey, string ruleSource) =>
        new(loadUnitType, null, colourKey, ruleSource, false);

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}

public sealed record PalletHandlingResult(
    string LoadUnitType,
    string? PalletType,
    string ColourKey,
    string RuleSource,
    bool IsPallet);
