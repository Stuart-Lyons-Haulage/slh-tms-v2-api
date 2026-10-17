namespace Slh.Tms.Api.Services;

public static class SafeMarketDuplicateAutoMergeService
{
    public static async Task<MasterDataDuplicateMergeResult> AutoMergeAsync(
        Slh.Tms.Api.Data.TmsDbContext db,
        string actor,
        CancellationToken ct)
    {
        var candidates = await MasterDataDuplicateReviewService.FindCandidatesAsync(db, "markets", ct);
        var safeCandidates = candidates
            .Where(candidate => candidate.CanAutoMerge || IsSafeSameMarketStandCandidate(candidate))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Canonical.Name, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToList();

        var merged = 0;
        var messages = new List<string>();
        var skipped = candidates.Count - safeCandidates.Count;

        foreach (var candidate in safeCandidates)
        {
            try
            {
                var result = await MasterDataDuplicateReviewService.MergeAsync(
                    db,
                    "markets",
                    new MasterDataDuplicateMergeRequest(
                        candidate.Canonical.Id,
                        candidate.Duplicates.Select(row => row.Id).ToList(),
                        candidate.CanAutoMerge
                            ? "Automatic high-confidence market duplicate merge."
                            : "Automatic safe same-market/same-stand duplicate merge; no conflicting sender evidence."),
                    actor,
                    ct);

                merged += result.Merged;
                messages.AddRange(result.Messages);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                messages.Add($"Skipped automatic merge for {candidate.Canonical.Name}: {ex.GetBaseException().Message}");
                db.ChangeTracker.Clear();
            }
        }

        if (skipped > 0)
            messages.Add($"Left {skipped} market duplicate candidate(s) for manual review because they had conflicting market, stand or sender evidence.");

        return new MasterDataDuplicateMergeResult(merged, candidates.Count, messages);
    }

    private static bool IsSafeSameMarketStandCandidate(MasterDataDuplicateCandidate candidate)
    {
        if (!candidate.EntityType.Equals("markets", StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.Duplicates.Count == 0) return false;

        var rows = new[] { candidate.Canonical }.Concat(candidate.Duplicates).ToList();
        var markets = rows.Select(row => Field(row, "market")).Select(Normalise).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var names = rows.Select(row => row.Name).Select(Normalise).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var stands = rows.Select(row => Field(row, "standOrLocation")).Select(Normalise).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var senders = rows.Select(row => Field(row, "sender")).Select(Normalise).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return markets.Count <= 1 && names.Count == 1 && stands.Count == 1 && senders.Count <= 1;
    }

    private static string? Field(MasterDataDuplicateRecord row, string name)
    {
        foreach (var field in row.Fields)
        {
            if (field.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return field.Value?.ToString();
        }

        return null;
    }

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
