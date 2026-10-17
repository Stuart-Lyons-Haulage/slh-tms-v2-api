using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public static class SafeSiteDuplicateAutoMergeService
{
    public static async Task<MasterDataDuplicateMergeResult> AutoMergeAsync(
        Slh.Tms.Api.Data.TmsDbContext db,
        string actor,
        CancellationToken ct)
    {
        var candidates = await MasterDataDuplicateReviewService.FindCandidatesAsync(db, "sites", ct);
        var safeCandidates = candidates
            .Where(candidate => candidate.CanAutoMerge || IsSafeSiteCandidate(candidate))
            .OrderByDescending(candidate => candidate.Confidence)
            .ThenBy(candidate => candidate.Canonical.Name, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToList();

        var merged = 0;
        var reviewed = candidates.Count;
        var messages = new List<string>();
        var skipped = candidates.Count - safeCandidates.Count;

        foreach (var candidate in safeCandidates)
        {
            try
            {
                var mergeNote = candidate.CanAutoMerge
                    ? "Automatic high-confidence site duplicate merge."
                    : "Automatic safe same-name/site-code duplicate merge; no conflicting postcode, address or customer evidence.";

                var result = await MasterDataDuplicateReviewService.MergeAsync(
                    db,
                    "sites",
                    new MasterDataDuplicateMergeRequest(
                        candidate.Canonical.Id,
                        candidate.Duplicates.Select(row => row.Id).ToList(),
                        mergeNote),
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
            messages.Add($"Left {skipped} site duplicate candidate(s) for manual review because they had conflicting address, postcode, customer or identity evidence.");

        return new MasterDataDuplicateMergeResult(merged, reviewed, messages);
    }

    private static bool IsSafeSiteCandidate(MasterDataDuplicateCandidate candidate)
    {
        if (!candidate.EntityType.Equals("sites", StringComparison.OrdinalIgnoreCase)) return false;
        if (candidate.Duplicates.Count == 0) return false;

        var rows = new[] { candidate.Canonical }.Concat(candidate.Duplicates).ToList();
        var names = rows.Select(row => Normalize(row.Name)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var codes = rows.Select(row => Normalize(row.Code)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var postcodes = rows.Select(row => NormalizePostcode(row.Postcode ?? ExtractPostcode(row.Address))).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var addresses = rows.Select(row => NormalizeAddress(row.Address)).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var customerCodes = rows.Select(row => Normalize(Field(row, "customerCode"))).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (customerCodes.Count > 1) return false;
        if (postcodes.Count > 1) return false;
        if (addresses.Count > 1 && postcodes.Count == 0) return false;

        // Same SiteID/ExternalCode is a safe identity even when one row has a fuller display name.
        if (codes.Count == 1) return true;

        if (names.Count != 1) return false;

        // Exact duplicate names with no extra identity conflict are safe when there is no conflicting evidence.
        // This handles the common broken-import case where the same site was inserted many times with blank details.
        if (codes.Count <= rows.Count && addresses.Count <= 1 && postcodes.Count <= 1) return true;

        return candidate.Confidence >= 86 && addresses.Count <= 1 && postcodes.Count <= 1;
    }

    private static string? Field(MasterDataDuplicateRecord row, string name)
    {
        foreach (var field in row.Fields)
        {
            if (!field.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            return field.Value?.ToString();
        }
        return null;
    }

    private static string Normalize(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var compact = Regex.Replace(value, @"\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr|unit|industrial|estate)\b", string.Empty, RegexOptions.IgnoreCase);
        return Normalize(compact);
    }

    private static string NormalizePostcode(string? value) => Normalize(value).ToUpperInvariant();

    private static string? ExtractPostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value.ToUpperInvariant(), @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b");
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty) : null;
    }
}
