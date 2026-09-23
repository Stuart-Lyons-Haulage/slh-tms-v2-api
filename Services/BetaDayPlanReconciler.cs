namespace Slh.Tms.Api.Services;

public sealed record BetaComparisonOrderLine(string Reference, string Collection, string Delivery, int Pallets);

public sealed record BetaDayPlanReconciliation(
    int BetaOrderLines,
    int LyonsOrderLines,
    int MatchedOrderLines,
    IReadOnlyList<string> MissingFromLyons,
    IReadOnlyList<string> OnlyInLyons,
    bool OrderCoverageComplete,
    bool ComparableRouting,
    int RunCountDelta,
    decimal? MilesDelta,
    int? DriveMinutesDelta,
    IReadOnlyList<string> Warnings);

public static class BetaDayPlanReconciler
{
    public static BetaDayPlanReconciliation Reconcile(
        IReadOnlyList<BetaComparisonOrderLine> beta,
        IReadOnlyList<BetaComparisonOrderLine> lyons,
        int betaRunCount,
        int lyonsRunCount,
        bool betaRoutingComplete,
        bool lyonsRoutingComplete,
        decimal? betaMiles,
        decimal? lyonsMiles,
        int? betaDriveMinutes,
        int? lyonsDriveMinutes)
    {
        var unusedLyons = lyons.Select((line, index) => new Candidate(index, line)).ToList();
        var missing = new List<string>();
        var matched = 0;

        foreach (var betaLine in beta)
        {
            var match = BestMatch(betaLine, unusedLyons);
            if (match is null)
            {
                missing.Add(Label(betaLine));
                continue;
            }
            unusedLyons.Remove(match);
            matched++;
        }

        var onlyInLyons = unusedLyons.Select(item => Label(item.Line)).ToList();
        var coverageComplete = missing.Count == 0 && onlyInLyons.Count == 0 && beta.Count == lyons.Count;
        var comparableRouting = coverageComplete && betaRoutingComplete && lyonsRoutingComplete &&
            betaMiles is not null && lyonsMiles is not null && betaDriveMinutes is not null && lyonsDriveMinutes is not null;
        var warnings = new List<string>();
        if (!coverageComplete)
            warnings.Add("The Lyons plan does not contain exactly the same movement lines as the Beta input, so mileage and drive-time deltas are suppressed until work coverage matches.");
        if (coverageComplete && !betaRoutingComplete)
            warnings.Add("At least one Beta run lacks live Azure Maps HGV evidence, so a whole-day route delta is not claimed.");
        if (coverageComplete && !lyonsRoutingComplete)
            warnings.Add("At least one uploaded Lyons run lacks live Azure Maps HGV evidence, so a whole-day route delta is not claimed.");
        if (beta.Any(line => line.Pallets <= 0) || lyons.Any(line => line.Pallets <= 0))
            warnings.Add("Quantity-unknown tray/crate/market movements are matched by reference and/or physical movement only; no pallet quantity is invented for reconciliation.");

        return new BetaDayPlanReconciliation(
            beta.Count,
            lyons.Count,
            matched,
            missing,
            onlyInLyons,
            coverageComplete,
            comparableRouting,
            lyonsRunCount - betaRunCount,
            comparableRouting ? Math.Round(lyonsMiles!.Value - betaMiles!.Value, 2) : null,
            comparableRouting ? lyonsDriveMinutes!.Value - betaDriveMinutes!.Value : null,
            warnings);
    }

    private static Candidate? BestMatch(BetaComparisonOrderLine beta, IReadOnlyList<Candidate> lyons)
    {
        var betaRef = Normalise(beta.Reference);
        var betaCollection = Normalise(beta.Collection);
        var betaDelivery = Normalise(beta.Delivery);

        var exact = lyons.FirstOrDefault(item =>
            betaRef.Length > 0 && betaRef == Normalise(item.Line.Reference) &&
            QuantityCompatible(beta.Pallets, item.Line.Pallets) &&
            betaCollection == Normalise(item.Line.Collection) &&
            betaDelivery == Normalise(item.Line.Delivery));
        if (exact is not null) return exact;

        if (betaRef.Length > 0)
        {
            var referenceMatches = lyons.Where(item =>
                betaRef == Normalise(item.Line.Reference) && QuantityCompatible(beta.Pallets, item.Line.Pallets)).ToList();
            if (referenceMatches.Count == 1) return referenceMatches[0];
        }

        var movementMatches = lyons.Where(item =>
            QuantityCompatible(beta.Pallets, item.Line.Pallets) &&
            betaCollection == Normalise(item.Line.Collection) &&
            betaDelivery == Normalise(item.Line.Delivery)).ToList();
        return movementMatches.Count == 1 ? movementMatches[0] : null;
    }

    private static bool QuantityCompatible(int left, int right) => left <= 0 || right <= 0 ? left <= 0 && right <= 0 : left == right;

    private static string Label(BetaComparisonOrderLine line)
    {
        var reference = string.IsNullOrWhiteSpace(line.Reference) ? "No reference" : line.Reference.Trim();
        var quantity = line.Pallets > 0 ? $"{line.Pallets} pallets" : "quantity not stated";
        return $"{reference} · {line.Collection} → {line.Delivery} · {quantity}";
    }

    private static string Normalise(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private sealed record Candidate(int Index, BetaComparisonOrderLine Line);
}
