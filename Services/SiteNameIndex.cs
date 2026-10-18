using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Resolves the many ways a site is written in emails to one Site master record, using the
/// site's name, driver text name, code and aliases. Punctuation and case are ignored; a
/// name that clearly contains (or is contained in) exactly one site's name also resolves.
/// </summary>
public sealed class SiteNameIndex
{
    private const int MinimumPartialLength = 6;
    private static readonly string[] NoiseWords = ["DISTRIBUTIONCENTRE", "DISTRIBUTIONCENTER", "REGIONALDISTRIBUTIONCENTRE", "DEPOT", "LIMITED", "LTD"];
    private static (DateTimeOffset At, SiteNameIndex Index)? cached;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    private readonly Dictionary<string, HashSet<Guid>> names = new(StringComparer.Ordinal);

    public int Count => names.Count;

    public static async Task<SiteNameIndex> LoadAsync(TmsDbContext db, CancellationToken ct, bool forceReload = false)
    {
        if (!forceReload && cached is { } hit && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(30)) return hit.Index;
        await Gate.WaitAsync(ct);
        try
        {
            if (!forceReload && cached is { } again && DateTimeOffset.UtcNow - again.At < TimeSpan.FromMinutes(30)) return again.Index;
            var index = new SiteNameIndex();
            try
            {
                var sites = await db.Sites.AsNoTracking()
                    .Where(site => site.Active)
                    .Select(site => new { site.Id, site.Name, site.DriverTextName, site.ExternalCode, site.Aliases })
                    .ToListAsync(ct);
                foreach (var site in sites)
                {
                    index.Add(site.Name, site.Id);
                    index.Add(site.DriverTextName, site.Id);
                    index.Add(site.ExternalCode, site.Id);
                    foreach (var alias in (site.Aliases ?? string.Empty).Split([',', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        index.Add(alias, site.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Without the site list, matching falls back to names alone.
                db.ChangeTracker.Clear();
            }
            cached = (DateTimeOffset.UtcNow, index);
            return index;
        }
        finally { Gate.Release(); }
    }

    public static SiteNameIndex FromNames(IEnumerable<(string Name, Guid Id)> entries)
    {
        var index = new SiteNameIndex();
        foreach (var (name, id) in entries) index.Add(name, id);
        return index;
    }

    public static string Clean(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string StripNoise(string clean)
    {
        foreach (var word in NoiseWords)
            if (clean.EndsWith(word, StringComparison.Ordinal) && clean.Length - word.Length >= MinimumPartialLength)
                return clean[..^word.Length];
        return clean;
    }

    private void Add(string? name, Guid id)
    {
        var clean = Clean(name);
        if (clean.Length < 3) return;
        foreach (var key in new[] { clean, StripNoise(clean) }.Distinct())
        {
            if (!names.TryGetValue(key, out var ids)) names[key] = ids = [];
            ids.Add(id);
        }
    }

    /// <summary>The site this (already cleaned) name refers to, or null if unknown or ambiguous.</summary>
    public Guid? Resolve(string identity)
    {
        var clean = Clean(identity);
        if (clean.Length < 3) return null;
        foreach (var key in new[] { clean, StripNoise(clean) })
            if (names.TryGetValue(key, out var exact))
                return exact.Count == 1 ? exact.First() : null;

        if (clean.Length < MinimumPartialLength) return null;
        var stripped = StripNoise(clean);
        var partial = names
            .Where(entry => entry.Key.Length >= MinimumPartialLength &&
                            (stripped.Contains(entry.Key, StringComparison.Ordinal) || entry.Key.Contains(stripped, StringComparison.Ordinal)))
            .SelectMany(entry => entry.Value)
            .Distinct()
            .Take(2)
            .ToList();
        return partial.Count == 1 ? partial[0] : null;
    }
}
