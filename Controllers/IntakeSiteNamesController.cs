using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Site names in orders waiting for review that the Site master does not recognise, with
/// likely matches, and a one-click "add as alias" that re-checks the waiting orders.
/// </summary>
[ApiController]
[Route("api/v1/order-intake/site-names")]
[Authorize]
public sealed class IntakeSiteNamesController(TmsDbContext db) : ControllerBase
{
    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    { "ALDI", "LIDL", "NWF", "DISTRIBUTION", "CENTRE", "CENTER", "DEPOT", "REGIONAL", "LTD", "LIMITED", "WAITROSE", "TESCO", "SAINSBURY", "SAINSBURYS", "THE", "AND", "SITE", "FARM", "FARMS" };

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var index = await SiteNameIndex.LoadAsync(db, ct, forceReload: true);
        var sites = await db.Sites.AsNoTracking().Where(site => site.Active)
            .Select(site => new { site.Id, site.Name, site.ExternalCode, site.Aliases }).ToListAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-14);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(x => x.EntityType == "order" && x.Status == StagingStatus.PendingReview && x.ReceivedAtUtc >= since)
            .OrderByDescending(x => x.ReceivedAtUtc)
            .Take(1500)
            .Select(x => x.PayloadJson)
            .ToListAsync(ct);

        var found = new Dictionary<string, UnmatchedName>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var payload = document.RootElement;
                var customer = Text(payload, "customerCode");
                var unusual = payload.TryGetProperty("intakeWarnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array &&
                              warnings.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString()!.StartsWith("Unusual: new", StringComparison.Ordinal));
                Check("Delivery", Text(payload, "deliverySiteId"), Text(payload, "stallNumber") ?? Text(payload, "deliverySite") ?? Text(payload, "destination"));
                Check("Collection", Text(payload, "collectionSiteId"), Text(payload, "sellerName") ?? Text(payload, "collectionSite"));

                void Check(string kind, string? siteId, string? name)
                {
                    if (string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(siteId)) return;
                    if (index.Resolve(SiteNameIndex.Clean(name)) is not null) return;
                    var key = $"{kind}|{name.Trim()}";
                    if (!found.TryGetValue(key, out var entry))
                        found[key] = entry = new UnmatchedName(kind, name.Trim());
                    entry.Orders++;
                    if (unusual) entry.FlaggedUnusual++;
                    if (customer is not null) entry.Customers.Add(customer);
                }
            }
            catch (JsonException) { }
        }

        var siteTokens = sites.Select(site => (site, tokens: Tokens($"{site.Name} {site.Aliases}"))).ToList();
        return Ok(found.Values
            .OrderByDescending(item => item.Orders)
            .Select(item =>
            {
                var tokens = Tokens(item.Name);
                var suggestions = siteTokens
                    .Select(entry => (entry.site, score: entry.tokens.Intersect(tokens).Count()))
                    .Where(entry => entry.score > 0)
                    .OrderByDescending(entry => entry.score).ThenBy(entry => entry.site.Name)
                    .Take(3)
                    .Select(entry => new { entry.site.Id, entry.site.Name, entry.site.ExternalCode })
                    .ToList();
                return new { item.Kind, item.Name, item.Orders, item.FlaggedUnusual, customers = item.Customers.OrderBy(x => x).ToList(), suggestions };
            }));
    }

    /// <summary>Adds the name as an alias of a Site master record, then re-checks waiting orders.</summary>
    [HttpPost("alias"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> AddAlias([FromBody] AddSiteAliasRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Alias)) return BadRequest(new { message = "alias is required." });
        var site = await db.Sites.SingleOrDefaultAsync(item => item.Id == request.SiteId, ct);
        if (site is null) return NotFound(new { message = "Site not found." });
        var merged = SiteMasterIdentityResolver.MergeAliases(site.Aliases, request.Alias.Trim());
        if (merged.Length > 500) return BadRequest(new { message = $"{site.Name} already has too many aliases to add another." });
        site.Aliases = merged;
        await db.SaveChangesAsync(ct);
        var rechecked = await IntakeAutoApproval.RecheckPendingAsync(db, ct);
        return Ok(new { site = site.Name, aliases = site.Aliases, rechecked });
    }

    /// <summary>Re-checks the "Unusual" warnings on every order waiting for review.</summary>
    [HttpPost("recheck"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Recheck(CancellationToken ct) =>
        Ok(new { rechecked = await IntakeAutoApproval.RecheckPendingAsync(db, ct) });

    private static HashSet<string> Tokens(string? value) =>
        Regex.Split((value ?? string.Empty).ToUpperInvariant(), "[^A-Z0-9]+")
            .Where(token => token.Length >= 4 && !GenericWords.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

    private static string? Text(JsonElement payload, string name) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private sealed class UnmatchedName(string kind, string name)
    {
        public string Kind { get; } = kind;
        public string Name { get; } = name;
        public int Orders { get; set; }
        public int FlaggedUnusual { get; set; }
        public HashSet<string> Customers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record AddSiteAliasRequest(Guid SiteId, string Alias);
