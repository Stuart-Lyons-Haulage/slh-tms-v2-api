namespace Slh.Tms.Api.Services;

public static class PalletCapacityCalculator
{
    public const decimal DefaultStandardCapacity = 26m;
    public const decimal DefaultEuroCapacity = 33m;
    public const decimal DefaultTrolleyCapacity = 41m;

    public static PalletCapacityResult Calculate(
        decimal? standardPallets,
        decimal? euroPallets,
        decimal? unknownPallets = null,
        decimal standardCapacity = DefaultStandardCapacity,
        decimal euroCapacity = DefaultEuroCapacity,
        decimal? trolleys = null,
        decimal trolleyCapacity = DefaultTrolleyCapacity,
        decimal? stackablePallets = null,
        bool stackingApproved = false,
        decimal stackingLevels = 2m)
    {
        if (standardCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(standardCapacity));
        if (euroCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(euroCapacity));
        if (trolleyCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(trolleyCapacity));
        if (stackingLevels < 1) throw new ArgumentOutOfRangeException(nameof(stackingLevels));
        if (standardPallets < 0 || euroPallets < 0 || unknownPallets < 0 || trolleys < 0)
            throw new ArgumentOutOfRangeException(nameof(standardPallets), "Load-unit quantities cannot be negative.");

        var standard = standardPallets ?? 0m;
        var euro = euroPallets ?? 0m;
        var unknown = unknownPallets ?? 0m;
        var trolley = trolleys ?? 0m;

        // SLH planning rule: Euro and Standard pallets each consume one standard pallet
        // position for utilisation. Therefore 1 Euro leaves 25 Standard positions,
        // 2 Euros leaves 24, and so on against the normal 26-position trailer capacity.
        // Keep euroCapacity in the contract for compatibility with trailer master data,
        // but do not use it to create a separate 33-Euro capacity for mixed loads.
        var physicalPallets = standard + euro;
        var approvedStackable = stackingApproved ? Math.Min(Math.Max(stackablePallets ?? 0m, 0m), physicalPallets) : 0m;
        var standardEquivalentPallets = physicalPallets - approvedStackable + approvedStackable / stackingLevels;
        var palletUtilisation = standardEquivalentPallets / standardCapacity;

        // SLH trolley rule: an otherwise empty trailer carries 41 trolleys. Each pallet position
        // used reduces that trolley allowance by one: 1 pallet => 40 trolleys, 2 => 39, etc.
        // The rule applies to both Standard and Euro pallets and is evaluated alongside the
        // pallet-footprint calculation. Whichever constraint is tighter becomes authoritative.
        var trolleyPositionsUsed = trolley + standardEquivalentPallets;
        var trolleyUtilisation = trolleyPositionsUsed / trolleyCapacity;
        var utilisation = Math.Max(palletUtilisation, trolleyUtilisation);
        var utilisationPercent = Math.Round(utilisation * 100m, 1, MidpointRounding.AwayFromZero);
        var standardEquivalentUsed = Math.Round(standardEquivalentPallets, 2, MidpointRounding.AwayFromZero);
        var trolleyRemaining = Math.Max(trolleyCapacity - standard - euro - trolley, 0m);

        var status = unknown > 0
            ? "Amber"
            : utilisation > 1m
                ? "Red"
                : "Green";

        return new PalletCapacityResult(
            standard,
            euro,
            unknown,
            standardCapacity,
            euroCapacity,
            utilisationPercent,
            standardEquivalentUsed,
            standardCapacity,
            status,
            unknown > 0
                ? "Pallet type is missing for part of this load, so capacity cannot be fully confirmed."
                : utilisation > 1m
                    ? "Calculated trailer footprint or trolley/pallet ratio exceeds available capacity."
                    : "Calculated pallet footprint and trolley/pallet ratio are within capacity.",
            trolley,
            trolleyCapacity,
            trolleyPositionsUsed,
            trolleyRemaining,
            Math.Round(trolleyUtilisation * 100m, 1, MidpointRounding.AwayFromZero),
            physicalPallets,
            approvedStackable,
            stackingLevels,
            stackingApproved);
    }
}

public sealed record PalletCapacityResult(
    decimal StandardPallets,
    decimal EuroPallets,
    decimal UnknownPallets,
    decimal StandardCapacity,
    decimal EuroCapacity,
    decimal UtilisationPercent,
    decimal StandardEquivalentUsed,
    decimal StandardEquivalentCapacity,
    string Status,
    string Message,
    decimal Trolleys = 0m,
    decimal TrolleyCapacity = PalletCapacityCalculator.DefaultTrolleyCapacity,
    decimal TrolleyPositionsUsed = 0m,
    decimal TrolleyPositionsRemaining = PalletCapacityCalculator.DefaultTrolleyCapacity,
    decimal TrolleyUtilisationPercent = 0m,
    decimal PhysicalPallets = 0m,
    decimal ApprovedStackablePallets = 0m,
    decimal StackingLevels = 1m,
    bool StackingApproved = false);
