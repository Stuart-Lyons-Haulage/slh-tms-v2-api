using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/geofence-integrity")]
[Authorize]
public sealed class GeofenceIntegrityController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var planningDate = UkOperatingDate(now);
            List<Load> loads;
            try { loads = await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct); }
            catch { loads = []; db.ChangeTracker.Clear(); }

            var preparedLoads = GeofencePlanningMatch.PrepareLoads(loads);
            var fences = await EmbeddedGeofenceEngine.FenceStatusesAsync(db, ct);
            var overrides = await ActiveOverridesByName(ct);
            List<Site> sites;
            try { sites = await GeofenceSiteResolver.LoadActiveSitesAsync(db, ct); }
            catch { sites = []; db.ChangeTracker.Clear(); }

            var activeSiteIdsWithGeofence = fences.Where(x => x.SiteId is not null).Select(x => x.SiteId!.Value).ToHashSet();
            var duplicateReferenceGroups = sites
                .Where(x => !string.IsNullOrWhiteSpace(x.ExternalCode))
                .GroupBy(x => NormalizeCode(x.ExternalCode), StringComparer.OrdinalIgnoreCase)
                .Count(x => x.Count() > 1);

            var linkDiagnostics = fences.ToDictionary(
                item => item.Fence.Id,
                item => GeofenceLinkDiagnostics.Analyze(item.Fence, sites));

            // Use the same canonical planner-stop names as Run Progress, Run Timing and TV.
            // This keeps NWF Drayton/Merston/Selsey/Runcton evidence consistent across all
            // operational screens instead of comparing raw planner shorthand here.
            var snapshot = await EmbeddedGeofenceEngine.BuildAsync(db, planningDate, preparedLoads, ct);
            var latestTracking = await db.VehicleTrackingEvents.AsNoTracking()
                .OrderByDescending(x => x.EventTimeUtc)
                .Select(x => new { x.VehicleIdentifier, x.EventTimeUtc, x.Latitude, x.Longitude, x.ProviderName })
                .FirstOrDefaultAsync(ct);

            var trackingAgeMinutes = latestTracking is null ? (double?)null : Math.Max(0, (now - latestTracking.EventTimeUtc).TotalMinutes);
            var trackingFresh = trackingAgeMinutes is not null && trackingAgeMinutes <= 15;
            var linked = fences.Count(x => x.SiteId != null);
            var engineReady = fences.Count > 0;
            var planningLinkReady = linked > 0;
            var latestVisit = snapshot.Visits.OrderByDescending(x => x.EnteredAtUtc).FirstOrDefault();
            var latestConfirmed = snapshot.ConfirmedVisits.OrderByDescending(x => x.ConfirmedAtUtc).FirstOrDefault();
            var recentVisits = snapshot.Visits.OrderByDescending(x => x.EnteredAtUtc).Take(20).ToList();

            return Ok(new
            {
                checkedAtUtc = now,
                source = "EmbeddedSLHGeofences+RoadTechTracking",
                engineReady,
                planningLinkReady,
                liveRunProgressionReady = engineReady && trackingFresh,
                trackingFresh,
                trackingAgeMinutes,
                geofences = new
                {
                    total = fences.Count,
                    active = fences.Count,
                    valid = fences.Count,
                    linked,
                    unlinked = fences.Count - linked,
                    invalid = 0,
                    reconciliation = new
                    {
                        activeSites = sites.Count,
                        sitesMissingReference = sites.Count(x => string.IsNullOrWhiteSpace(x.ExternalCode)),
                        duplicateReferenceGroups,
                        sitesMissingGeofence = sites.Count(x => !activeSiteIdsWithGeofence.Contains(x.Id)),
                        safeCandidates = linkDiagnostics.Values.Count(item => item.SafeToAutoLink),
                        exactCode = linkDiagnostics.Values.Count(item => item.Reason == "ExactCode"),
                        exactNameOrAlias = linkDiagnostics.Values.Count(item => item.Reason == "ExactNameOrAlias"),
                        uniqueFuzzy = linkDiagnostics.Values.Count(item => item.Reason == "UniqueFuzzy"),
                        ambiguous = linkDiagnostics.Values.Count(item => item.Reason.StartsWith("Ambiguous", StringComparison.Ordinal)),
                        noCandidate = linkDiagnostics.Values.Count(item => item.Reason == "NoCandidate")
                    }
                },
                records = fences.Select(x =>
                {
                    var diagnostic = linkDiagnostics[x.Fence.Id];
                    overrides.TryGetValue(NormalizeName(x.Fence.Name), out var manual);
                    var locationOnly = string.Equals(manual?.SiteNumber, "LOCATION_ONLY", StringComparison.OrdinalIgnoreCase);
                    var siteNumber = x.SiteCode ?? (locationOnly ? null : manual?.SiteNumber ?? x.Fence.SiteNumber);
                    var codedUnlinked = x.SiteId is null && !locationOnly && !string.IsNullOrWhiteSpace(siteNumber);
                    return new
                    {
                        id = x.Fence.Id,
                        name = x.Fence.Name,
                        category = x.Fence.Category,
                        categoryMaxWaitMinutes = x.Fence.CategoryMaxWaitMinutes,
                        maxWaitMinutes = x.Fence.MaxWaitMinutes,
                        pendingEntryMinutes = x.Fence.PendingEntryMinutes,
                        pendingExitMinutes = x.Fence.PendingExitMinutes,
                        siteNumber,
                        siteId = x.SiteId,
                        siteName = x.SiteName,
                        siteCode = x.SiteCode,
                        manualOverride = x.ManualOverride,
                        locationOnly,
                        active = true,
                        polygonValid = true,
                        geofenceAvailable = true,
                        siteLinked = x.SiteId != null,
                        validationStatus = locationOnly ? "Location only" : x.SiteId != null ? "Valid" : codedUnlinked ? "Coded / needs Site promotion" : "Unlinked",
                        linkReason = diagnostic.Reason,
                        diagnostic.SafeToAutoLink,
                        diagnostic.SuggestedSiteId,
                        diagnostic.SuggestedSiteName,
                        diagnostic.CandidateCount,
                        candidates = diagnostic.Candidates
                    };
                }),
                latestTracking,
                latestGeofenceHit = Visit(latestVisit),
                latestConfirmedHit = Visit(latestConfirmed),
                recentHits = recentVisits.Select(Visit)
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return Ok(new
            {
                checkedAtUtc = DateTimeOffset.UtcNow,
                source = "EmbeddedSLHGeofencesSafeFallback",
                engineReady = EmbeddedGeofenceEngine.ApprovedFences.Count > 0,
                planningLinkReady = false,
                liveRunProgressionReady = false,
                trackingFresh = false,
                trackingAgeMinutes = (double?)null,
                geofences = new
                {
                    total = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    active = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    valid = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    linked = 0,
                    unlinked = EmbeddedGeofenceEngine.ApprovedFences.Count,
                    invalid = 0
                },
                warning = $"Approved geofences are available, but live tracking integrity could not be calculated: {exception.GetBaseException().Message}"
            });
        }
    }

    private async Task<Dictionary<string, SiteGeofence>> ActiveOverridesByName(CancellationToken ct)
    {
        try
        {
            var rows = await db.SiteGeofences.AsNoTracking().Where(x => x.Active).ToListAsync(ct);
            return rows.GroupBy(x => NormalizeName(x.Name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.UpdatedAtUtc).First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            return new Dictionary<string, SiteGeofence>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static object? Visit(DerivedVisit? visit)
    {
        if (visit is null) return null;
        return new
        {
            geofenceName = visit.Fence.Name,
            visit.VehicleIdentifier,
            visit.EnteredAtUtc,
            visit.ConfirmedAtUtc,
            visit.ExitedAtUtc,
            visit.LoadId,
            visit.LoadStopId,
            visit.DwellMinutes,
            status = visit.ExitedAtUtc is not null ? (visit.ConfirmedAtUtc is not null ? "Departed" : "PassThrough") : visit.ConfirmedAtUtc is not null ? "OnSiteConfirmed" : "Arrived",
            statusReason = visit.ExitedAtUtc is not null
                ? "Derived from RoadTech tracking crossing the approved SLH geofence boundary."
                : visit.ConfirmedAtUtc is not null
                    ? "Confirmed from RoadTech tracking after the minimum dwell period."
                    : "Vehicle is currently inside the approved SLH geofence."
        };
    }

    private static string NormalizeName(string value) => string.Join(' ', value.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    private static string NormalizeCode(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(value.UtcDateTime); }
    }
}
