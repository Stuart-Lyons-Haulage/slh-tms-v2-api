using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/assistant"), Authorize]
public sealed class AssistantController(
    TmsAssistantService assistant,
    TmsDbContext db,
    AzureMapsRouteClient maps,
    ILogger<AssistantSafeFixService> safeFixLogger) : ControllerBase
{
    [HttpGet("snapshot")]
    public async Task<IActionResult> Snapshot([FromQuery] DateOnly? date, CancellationToken ct)
    {
        var snapshot = await assistant.GetSnapshot(date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct);
        var suggestions = await AddMarketSuggestions(snapshot.Suggestions, ct);
        return Ok(snapshot with { Suggestions = EnableSafeFixes(suggestions) });
    }

    [HttpPost("advice")]
    public async Task<IActionResult> Advice(AssistantAdviceRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 1000)
            return BadRequest(new { message = "Ask a planning question between 1 and 1000 characters." });

        var plannerQuestion = request.Message.Trim();
        try
        {
            var sites = await db.Sites.AsNoTracking().Where(site => site.Active).Take(2500).ToListAsync(ct);
            await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
            var normalQuestion = Normalise(plannerQuestion);
            var matchedSites = sites
                .Select(site => new
                {
                    Site = site,
                    Keys = new[] { site.Name, site.ExternalCode, site.DriverTextName }
                        .Concat((site.Aliases ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(Normalise)
                        .Where(value => value.Length >= 3)
                        .ToArray()
                })
                .Where(item => item.Keys.Any(key => normalQuestion.Contains(key, StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Site)
                .Take(8)
                .ToList();

            if (matchedSites.Count > 0)
            {
                var context = string.Join(" | ", matchedSites.Select(site =>
                    $"{site.Name} ({site.ExternalCode}): address={site.CollectionAddress ?? "not stored"}; map={site.MapLink ?? "not stored"}; coordinates={(site.Latitude is not null && site.Longitude is not null ? $"{site.Latitude},{site.Longitude}" : "not stored")}"));
                plannerQuestion = $"{plannerQuestion}\nKnown SLH site context: {context}";
            }
        }
        catch
        {
            db.ChangeTracker.Clear();
        }

        var userKey = User.FindFirst("oid")?.Value ?? User.Identity?.Name ?? "slh-planner";
        var advice = await assistant.Advise(request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow), plannerQuestion, userKey, ct);
        var suggestions = await AddMarketSuggestions(advice.Suggestions, ct);
        return Ok(advice with { Suggestions = EnableSafeFixes(suggestions) });
    }

    [HttpPost("fix-safe-validations"), Authorize(Policy = "TmsApprove")]
    public async Task<IActionResult> FixSafeValidations(CancellationToken ct)
    {
        try
        {
            var before = await ReadValidationState(ct);
            var safeFixes = new AssistantSafeFixService(db, maps, safeFixLogger);
            var attemptedResult = await safeFixes.Apply(ct);
            db.ChangeTracker.Clear();
            var after = await ReadValidationState(ct);
            var verifiedAttempted = VerifyChanges(attemptedResult.Changes, before, after);
            var verificationFailures = attemptedResult.Changes.Except(verifiedAttempted, StringComparer.Ordinal).ToList();
            var verified = verifiedAttempted.Select(AccurateChangeWording).ToList();
            var skippedReasons = attemptedResult.SkippedReasons
                .Concat(verificationFailures.Select(change => $"Verification did not prove the live TMS changed for: {change}"))
                .ToList();

            return Ok(new
            {
                attempted = attemptedResult.Applied,
                applied = verified.Count,
                verified = verificationFailures.Count == 0,
                skipped = attemptedResult.Skipped + verificationFailures.Count,
                changes = verified,
                verificationFailures,
                skippedReasons,
                before,
                after
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            safeFixLogger.LogError(exception, "SLH Assistant master-data fixes failed; returning a controlled result instead of HTTP 500.");
            db.ChangeTracker.Clear();
            return Ok(new
            {
                attempted = 0,
                applied = 0,
                verified = false,
                skipped = 1,
                changes = Array.Empty<string>(),
                verificationFailures = new[] { "The Assistant could not verify a master-data change." },
                skippedReasons = new[] { $"Master data repair could not complete: {exception.GetBaseException().Message}" }
            });
        }
    }

    private async Task<AssistantValidationState> ReadValidationState(CancellationToken ct)
    {
        var sites = await db.Sites.AsNoTracking().Where(x => x.Active).Take(5000).ToListAsync(ct);
        await MasterDetailStore.EnrichSitesAsync(db, sites, ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(x => x.Active).Take(5000).ToListAsync(ct);
        var markets = await db.MarketContacts.AsNoTracking().Where(x => x.Active).Take(5000).ToListAsync(ct);
        var contacts = await db.CustomerContacts.AsNoTracking().Where(x => x.Active && x.Email != null).Take(5000).ToListAsync(ct);
        var geofenceReview = 0;
        try
        {
            geofenceReview = (await SiteGeofenceMasterSync.GetStatusAsync(db, ct)).Count(x => x.NeedsReview);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            safeFixLogger.LogWarning(exception, "Assistant verification could not read geofence status.");
            db.ChangeTracker.Clear();
        }

        return new AssistantValidationState(
            sites.Count,
            sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink)),
            sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && (x.Latitude is null || x.Longitude is null)),
            CountSafeDuplicateSiteRecords(sites),
            vehicles.Count(x => x.Registration != TmsAssistantService.NormaliseRegistration(x.Registration)),
            markets.Count(x => CanonicalMarket(x.Market) != (x.Market ?? string.Empty).Trim()),
            CountExactMarketDuplicateRecords(markets),
            markets.Count(x => x.Active && !string.Equals(CanonicalMarket(x.Market), "Sender", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(x.StandOrLocation) && !string.IsNullOrWhiteSpace(InferStand(x.Name))),
            contacts.Count(x => x.Email != x.Email!.Trim().ToLowerInvariant()),
            geofenceReview);
    }

    private static List<string> VerifyChanges(IReadOnlyList<string> attempted, AssistantValidationState before, AssistantValidationState after)
    {
        var allowances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["vehicle"] = Math.Max(0, before.UntidyVehicleRegistrations - after.UntidyVehicleRegistrations),
            ["map-link"] = Math.Max(0, before.MissingSiteMapLinks - after.MissingSiteMapLinks),
            ["map-point"] = Math.Max(0, before.MissingSiteMapPoints - after.MissingSiteMapPoints),
            ["site-duplicate"] = Math.Max(0, before.SafeDuplicateSiteRecords - after.SafeDuplicateSiteRecords),
            ["market-normalise"] = Math.Max(0, before.NonCanonicalMarkets - after.NonCanonicalMarkets),
            ["market-duplicate"] = Math.Max(0, before.ExactDuplicateMarketRecords - after.ExactDuplicateMarketRecords),
            ["market-stand"] = Math.Max(0, before.InferableMarketStandGaps - after.InferableMarketStandGaps),
            ["customer-email"] = Math.Max(0, before.UntidyCustomerEmails - after.UntidyCustomerEmails),
            ["geofence"] = Math.Max(0, before.GeofenceReviewItems - after.GeofenceReviewItems)
        };

        var verified = new List<string>();
        foreach (var change in attempted)
        {
            var category = ChangeCategory(change);
            if (category is null || !allowances.TryGetValue(category, out var remaining) || remaining <= 0) continue;
            verified.Add(change);
            allowances[category] = remaining - 1;
        }
        return verified;
    }

    private static string? ChangeCategory(string change)
    {
        if (change.StartsWith("Vehicle ", StringComparison.OrdinalIgnoreCase)) return "vehicle";
        if (change.StartsWith("Created a driver map link", StringComparison.OrdinalIgnoreCase)) return "map-link";
        if (change.StartsWith("Added map point", StringComparison.OrdinalIgnoreCase)) return "map-point";
        if (change.StartsWith("Consolidated", StringComparison.OrdinalIgnoreCase) && change.Contains("duplicate site", StringComparison.OrdinalIgnoreCase)) return "site-duplicate";
        if (change.StartsWith("Normalised market record", StringComparison.OrdinalIgnoreCase)) return "market-normalise";
        if (change.StartsWith("Consolidated", StringComparison.OrdinalIgnoreCase) && change.Contains("duplicate market", StringComparison.OrdinalIgnoreCase)) return "market-duplicate";
        if (change.Contains("stand", StringComparison.OrdinalIgnoreCase) && change.Contains("market", StringComparison.OrdinalIgnoreCase)) return "market-stand";
        if (change.StartsWith("Normalised the ETA email", StringComparison.OrdinalIgnoreCase)) return "customer-email";
        if (change.Contains("geofence", StringComparison.OrdinalIgnoreCase) || change.Contains("SITE", StringComparison.OrdinalIgnoreCase)) return "geofence";
        return null;
    }

    private static string AccurateChangeWording(string change)
    {
        if (change.StartsWith("Consolidated", StringComparison.OrdinalIgnoreCase) && change.Contains("duplicate site", StringComparison.OrdinalIgnoreCase))
            return change.Replace("Consolidated", "Archived duplicate Site record(s) into the canonical Site and verified", StringComparison.OrdinalIgnoreCase);
        if (change.StartsWith("Consolidated", StringComparison.OrdinalIgnoreCase) && change.Contains("duplicate market", StringComparison.OrdinalIgnoreCase))
            return change.Replace("Consolidated", "Archived duplicate Market record(s) into the canonical Market entry and verified", StringComparison.OrdinalIgnoreCase);
        return change;
    }

    private static int CountSafeDuplicateSiteRecords(IReadOnlyCollection<Site> sites)
    {
        var groups = new Dictionary<string, HashSet<Guid>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var name = Normalise(site.Name);
            var address = NormaliseAddress(site.CollectionAddress);
            var postcode = ExtractPostcode(site.CollectionAddress);
            var keys = new List<string>();
            if (name.Length >= 5 && address.Length >= 8) keys.Add($"nameaddress:{name}|{address}");
            if (name.Length >= 5 && !string.IsNullOrWhiteSpace(postcode)) keys.Add($"namepostcode:{name}|{postcode}");
            foreach (var key in keys)
            {
                if (!groups.TryGetValue(key, out var ids)) groups[key] = ids = [];
                ids.Add(site.Id);
            }
        }
        return groups.Values.Where(ids => ids.Count > 1).SelectMany(ids => ids.Skip(1)).Distinct().Count();
    }

    private static int CountExactMarketDuplicateRecords(IReadOnlyCollection<MarketContact> markets) => markets
        .Where(x => !string.IsNullOrWhiteSpace(x.Market) && !string.IsNullOrWhiteSpace(x.Name))
        .GroupBy(x => $"{Normalise(CanonicalMarket(x.Market))}|{Normalise(x.Name)}|{Normalise(x.StandOrLocation)}", StringComparer.OrdinalIgnoreCase)
        .Sum(group => Math.Max(0, group.Count() - 1));

    private async Task<IReadOnlyList<AssistantSuggestion>> AddMarketSuggestions(IReadOnlyList<AssistantSuggestion> suggestions, CancellationToken ct)
    {
        try
        {
            var rows = await db.MarketContacts.AsNoTracking().Where(x => x.Active).Take(5000).ToListAsync(ct);
            var result = suggestions.Where(x => x.Id != "markets-validation").ToList();
            var missingRequired = rows.Count(x => string.IsNullOrWhiteSpace(x.Market) || string.IsNullOrWhiteSpace(x.Name));
            var nonCanonical = rows.Count(x => CanonicalMarket(x.Market) != (x.Market ?? string.Empty).Trim());
            var duplicates = rows
                .Where(x => !string.IsNullOrWhiteSpace(x.Market) && !string.IsNullOrWhiteSpace(x.Name))
                .GroupBy(x => $"{Normalise(CanonicalMarket(x.Market))}|{Normalise(x.Name)}|{Normalise(x.StandOrLocation)}", StringComparer.OrdinalIgnoreCase)
                .Count(x => x.Count() > 1);
            var missingStand = rows.Count(x => !string.Equals(CanonicalMarket(x.Market), "Sender", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(x.StandOrLocation));

            if (missingRequired + nonCanonical + duplicates + missingStand > 0)
            {
                var parts = new[]
                {
                    missingRequired > 0 ? $"{missingRequired} missing market/name" : null,
                    duplicates > 0 ? $"{duplicates} duplicate group{(duplicates == 1 ? "" : "s")}" : null,
                    nonCanonical > 0 ? $"{nonCanonical} market name{(nonCanonical == 1 ? "" : "s")} to standardise" : null,
                    missingStand > 0 ? $"{missingStand} stand/location gap{(missingStand == 1 ? "" : "s")}" : null
                }.Where(x => x is not null);
                result.Add(new AssistantSuggestion(
                    "markets-validation",
                    missingRequired + duplicates > 0 ? "high" : "medium",
                    "Clean market master data",
                    $"Market validation found {string.Join(", ", parts)}. Safe fixes standardise market names, infer obvious stand numbers and archive only exact duplicate market + name + stand records; incomplete identities remain review-only.",
                    "Markets",
                    nonCanonical + duplicates > 0 || rows.Any(x => string.IsNullOrWhiteSpace(x.StandOrLocation) && !string.IsNullOrWhiteSpace(InferStand(x.Name)))));
            }
            return result;
        }
        catch
        {
            db.ChangeTracker.Clear();
            return suggestions;
        }
    }

    private static IReadOnlyList<AssistantSuggestion> EnableSafeFixes(IReadOnlyList<AssistantSuggestion> suggestions) =>
        suggestions.Select(item => item.Id == "sites-duplicates" ? item with
        {
            AutoFixAvailable = true,
            Detail = item.Detail + " The Assistant can archive records where the normalised name and address/postcode prove they are the same Site; ambiguous groups remain review-only, and a fix is only reported after a live re-read verifies it."
        } : item).ToList();

    private static string CanonicalMarket(string? value)
    {
        var clean = (value ?? string.Empty).Trim();
        var normal = Normalise(clean);
        if (normal.Contains("covent")) return "Covent";
        if (normal.Contains("spit")) return "Spitalfields";
        if (normal.Contains("western")) return "Western";
        if (normal.Contains("sender")) return "Sender";
        return clean;
    }

    private static string? InferStand(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bracketStart = name.LastIndexOf('(');
        if (bracketStart >= 0 && name.EndsWith(')') && bracketStart < name.Length - 2) return name[(bracketStart + 1)..^1].Trim();
        return null;
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string NormaliseAddress(string? value) => Normalise(Regex.Replace(value ?? string.Empty, @"\b(road|rd|street|st|avenue|ave|lane|ln|drive|dr)\b", string.Empty, RegexOptions.IgnoreCase));
    private static string? ExtractPostcode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value.ToUpperInvariant(), @"\b([A-Z]{1,2}\d[A-Z\d]?\s*\d[A-Z]{2})\b");
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", string.Empty) : null;
    }
}

public sealed record AssistantAdviceRequest(string Message, DateOnly? Date);
public sealed record AssistantValidationState(
    int ActiveSites,
    int MissingSiteMapLinks,
    int MissingSiteMapPoints,
    int SafeDuplicateSiteRecords,
    int UntidyVehicleRegistrations,
    int NonCanonicalMarkets,
    int ExactDuplicateMarketRecords,
    int InferableMarketStandGaps,
    int UntidyCustomerEmails,
    int GeofenceReviewItems);