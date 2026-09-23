using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Assistant;

namespace Slh.Tms.Api.Services;

public sealed class TmsAssistantService(
    HttpClient httpClient,
    TmsDbContext db,
    AzureMapsRouteClient maps,
    DispatchService dispatch,
    BetaOptimiserService optimiser,
    AssistantOptions options,
    ILogger<TmsAssistantService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ── Snapshot ─────────────────────────────────────────────────────────────

    public async Task<AssistantSnapshot> GetSnapshot(DateOnly planningDate, CancellationToken ct)
    {
        var orders   = await ReadOrders(planningDate, ct);
        var loads    = await ReadLoads(planningDate, ct);
        var drivers  = await SafeRead(db.Drivers.AsNoTracking().Where(x => x.Active).Take(1000), "drivers", ct);
        var vehicles = await SafeRead(db.Vehicles.AsNoTracking().Where(x => x.Active).Take(1000), "vehicles", ct);
        var sites    = await SafeRead(db.Sites.AsNoTracking().Where(x => x.Active).Take(2000), "sites", ct);
        await SafeEnrichSites(sites, ct);

        var plannedOrderIds = loads
            .SelectMany(x => x.Stops ?? [])
            .Where(x => x.OrderId is not null)
            .Select(x => x.OrderId!.Value)
            .ToHashSet();

        var unplanned   = orders.Where(x => !plannedOrderIds.Contains(x.Id)).ToList();
        var unallocated = loads.Where(x => x.DriverId is null || x.VehicleId is null).ToList();
        var now         = DateTimeOffset.UtcNow;
        var today       = DateOnly.FromDateTime(DateTime.UtcNow);
        var suggestions = new List<AssistantSuggestion>();

        // ── Orders & runs ──────────────────────────────────────────────────
        if (unplanned.Count > 0)
            suggestions.Add(new("orders-unplanned", "high", "Plan approved orders",
                $"{unplanned.Count} approved order{(unplanned.Count == 1 ? " is" : "s are")} not yet on a run for {planningDate:dd MMM}. Add them to a run before dispatch locks.",
                "Planner", false));

        if (unallocated.Count > 0)
            suggestions.Add(new("loads-unallocated", "high", "Complete run allocations",
                $"{unallocated.Count} run{(unallocated.Count == 1 ? " is" : "s are")} missing a driver or vehicle for {planningDate:dd MMM}. Ask the assistant which driver best fits each run.",
                "Planner", false));

        var noMapPoints = loads.Where(x => x.Stops.Count == 0 || x.Stops.Any(s => s.Latitude is null || s.Longitude is null)).ToList();
        if (noMapPoints.Count > 0)
            suggestions.Add(new("loads-unmapped", "medium", "Finish route stop coordinates",
                $"{noMapPoints.Count} run{(noMapPoints.Count == 1 ? " has" : "s have")} missing stop coordinates — ETA calculations and the optimiser cannot work without them. Link a geofence or add the site address/postcode first.",
                "Sites", false));

        var emptyMiles = loads.Sum(x => x.EmptyMiles ?? 0);
        var distance   = loads.Sum(x => x.EstimatedDistanceMiles ?? 0);
        if (distance > 0 && emptyMiles / distance >= 0.2m)
            suggestions.Add(new("loads-empty-miles", "medium", "Reduce empty running",
                $"Empty mileage is {Math.Round(emptyMiles / distance * 100, 1)}% of recorded miles today. Ask the assistant to suggest backload pairings or southbound opportunities.",
                "Planner", false));

        // ── Optimiser intelligence ─────────────────────────────────────────
        try
        {
            var optDay = await optimiser.AnalyseDayAsync(planningDate, ct);
            if (optDay is not null && optDay.SavingMiles > 0)
                suggestions.Add(new("optimiser-savings", "medium", "Route optimiser found savings",
                    $"The route optimiser has identified {optDay.SavingMiles:F0} miles ({optDay.SavingDriveMinutes / 60:F0}h {optDay.SavingDriveMinutes % 60}m drive) of potential savings across {optDay.RoutedRunCount} runs. Ask the assistant to show the best opportunities.",
                    "Planner", false));
            if (optDay is not null && optDay.UnroutedRunCount > 0)
                suggestions.Add(new("optimiser-unrouted", "medium", "Runs not yet routed by optimiser",
                    $"{optDay.UnroutedRunCount} run{(optDay.UnroutedRunCount == 1 ? " has" : "s have")} no optimiser route yet — stop coordinates or site links may be missing.",
                    "Sites", false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not read optimiser day analysis.");
        }

        // ── Drivers ────────────────────────────────────────────────────────
        var staleThreshold = now.AddHours(-12);
        var staleTacho = drivers.Count(x =>
            !string.IsNullOrWhiteSpace(x.TachoMasterDriverId) &&
            (x.LastTachoSyncUtc is null || x.LastTachoSyncUtc < staleThreshold));
        if (staleTacho > 0)
            suggestions.Add(new("drivers-tacho-stale", "high", "TachoMaster hours data is stale",
                $"{staleTacho} driver{(staleTacho == 1 ? "'s" : "s'")} hours have not synced from TachoMaster in 12+ hours. Dispatch suggestions may use out-of-date hours — run a manual sync before allocating.",
                "Drivers", false));

        int newTachoDriversPending = 0;
        try
        {
            newTachoDriversPending = await db.StagedImports.AsNoTracking()
                .CountAsync(r => r.EntityType == "driverreview" && r.Status == StagingStatus.PendingReview, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not count pending driver review items.");
            db.ChangeTracker.Clear();
        }
        if (newTachoDriversPending > 0)
            suggestions.Add(new("drivers-tacho-new", "high", "Review new TachoMaster drivers",
                $"{newTachoDriversPending} driver{(newTachoDriversPending == 1 ? "" : "s")} appeared in TachoMaster with no matching Driver Master record. Review and promote each one before they can be allocated to a run.",
                "Drivers", false));

        var blockedDrivers = 0;
        try
        {
            var dispatchDrivers = await dispatch.GetDriversAsync(planningDate, ct);
            blockedDrivers = dispatchDrivers.Count(x => x.IsBlocked);
            var needsReturn  = dispatchDrivers.Count(x => x.NeedsReturn && !x.IsBlocked);
            if (blockedDrivers > 0)
                suggestions.Add(new("drivers-blocked", "high", "Drivers blocked from dispatch",
                    $"{blockedDrivers} driver{(blockedDrivers == 1 ? " is" : "s are")} blocked from dispatch today (compliance, hours, or tacho issue). Ask the assistant who and why.",
                    "Drivers", false));
            if (needsReturn > 0)
                suggestions.Add(new("drivers-return", "medium", "Drivers need a return run",
                    $"{needsReturn} driver{(needsReturn == 1 ? " needs" : "s need")} a return run today. Ask the assistant for backload suggestions.",
                    "Planner", false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not read dispatch driver list.");
            db.ChangeTracker.Clear();
        }

        var licenceDue = drivers.Count(x => x.LicenceExpiry is null || x.LicenceExpiry <= today.AddDays(30));
        if (licenceDue > 0)
            suggestions.Add(new("drivers-licence", "high", "Driver licences need attention",
                $"{licenceDue} active driver licence record{(licenceDue == 1 ? " needs" : "s need")} checking or expires within 30 days.",
                "Drivers", false));

        var cpcDue = drivers.Count(x => x.CPCExpiry is not null && x.CPCExpiry <= today.AddDays(60));
        if (cpcDue > 0)
            suggestions.Add(new("drivers-cpc", "high", "CPC qualifications expiring",
                $"{cpcDue} driver{(cpcDue == 1 ? " has a" : "s have")} CPC expir{(cpcDue == 1 ? "y" : "ies")} within 60 days. Expired CPC means the driver cannot legally drive commercially.",
                "Drivers", false));

        var cardDue = drivers.Count(x => x.DigitalTachoCardExpiry is not null && x.DigitalTachoCardExpiry <= today.AddDays(30));
        if (cardDue > 0)
            suggestions.Add(new("drivers-tacho-card", "high", "Digital tachograph cards expiring",
                $"{cardDue} driver{(cardDue == 1 ? "'s" : "s'")} digital tacho card{(cardDue == 1 ? " expires" : "s expire")} within 30 days. A driver cannot operate without a valid card.",
                "Drivers", false));

        var missingDriverMobiles = drivers.Count(x => string.IsNullOrWhiteSpace(x.MobileNumber));
        if (missingDriverMobiles > 0)
            suggestions.Add(new("drivers-mobile", "medium", "Driver contact numbers missing",
                $"{missingDriverMobiles} active driver{(missingDriverMobiles == 1 ? " has" : "s have")} no dispatch mobile number — ETA texts and driver comms will fail.",
                "Drivers", false));

        var missingTachoNames = drivers.Count(x => string.IsNullOrWhiteSpace(x.TachoName));
        if (missingTachoNames > 0)
            suggestions.Add(new("drivers-tacho-name", "medium", "Drivers not matched to TachoMaster name",
                $"{missingTachoNames} active driver{(missingTachoNames == 1 ? " has" : "s have")} no TachoMaster name — they cannot be matched to Tacho records during sync.",
                "Drivers", false));

        // ── Fleet compliance ───────────────────────────────────────────────
        var vehicleRisk = vehicles.Count(x =>
            x.FleetioVor == true ||
            x.FleetioMotDueUtc <= now.AddDays(30) ||
            x.FleetioPmiDueUtc <= now.AddDays(30) ||
            (x.FleetioStatus ?? string.Empty).Contains("out of service", StringComparison.OrdinalIgnoreCase));
        if (vehicleRisk > 0)
            suggestions.Add(new("fleet-compliance", "high", "Fleet compliance risk",
                $"{vehicleRisk} vehicle{(vehicleRisk == 1 ? " has a" : "s have")} compliance flag (MOT/PMI due or VOR). Check Fleetio before allocating these vehicles.",
                "Vehicles", false));

        var untidyRegistrations = vehicles.Count(x => x.Registration != NormaliseRegistration(x.Registration));
        if (untidyRegistrations > 0)
            suggestions.Add(new("vehicles-registration", "low", "Normalise vehicle registrations",
                $"{untidyRegistrations} registration{(untidyRegistrations == 1 ? " needs" : "s need")} safe spacing/case normalisation.",
                "Vehicles", true));

        // ── Sites & geofences ──────────────────────────────────────────────
        var missingPhysicalAddresses = sites.Count(x =>
            string.IsNullOrWhiteSpace(x.CollectionAddress) && (x.Latitude is null || x.Longitude is null));
        if (missingPhysicalAddresses > 0)
            suggestions.Add(new("sites-physical-address", "high", "Sites missing address and coordinates",
                $"{missingPhysicalAddresses} active site{(missingPhysicalAddresses == 1 ? " has" : "s have")} neither a physical address nor coordinates. Link a geofence or add the address/postcode in Site Master so Azure Maps can geocode it.",
                "Sites", false));

        var missingMapLinks = sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && string.IsNullOrWhiteSpace(x.MapLink));
        if (missingMapLinks > 0)
            suggestions.Add(new("sites-map-link", "low", "Missing driver map links",
                $"{missingMapLinks} site{(missingMapLinks == 1 ? " has" : "s have")} an address but no driver map link. The assistant can create these safely.",
                "Sites", true));

        var missingMapPoints = sites.Count(x => !string.IsNullOrWhiteSpace(x.CollectionAddress) && (x.Latitude is null || x.Longitude is null));
        if (missingMapPoints > 0)
            suggestions.Add(new("sites-map-point", "medium", "Sites need geocoding",
                $"{missingMapPoints} site{(missingMapPoints == 1 ? " has" : "s have")} a physical address but no coordinates. The assistant can geocode these via Azure Maps.",
                "Sites", true));

        var geofenceLinkGaps = await GeofenceLinkGaps(ct);
        if (geofenceLinkGaps.SitesMissingGeofence > 0)
            suggestions.Add(new("geofences-sites-sync",
                geofenceLinkGaps.AutoFixAvailable ? "high" : "medium",
                "Sites missing confirmed geofence link",
                $"{geofenceLinkGaps.SitesMissingGeofence} active site{(geofenceLinkGaps.SitesMissingGeofence == 1 ? " is" : "s are")} missing a confirmed geofence link. A linked geofence is the preferred ETA/routing location — the assistant can apply unique confirmed matches.",
                "Sites", geofenceLinkGaps.AutoFixAvailable));

        var duplicateSiteGroups = FindDuplicateSiteGroups(sites);
        if (duplicateSiteGroups.Count > 0)
        {
            var examples = string.Join("; ", duplicateSiteGroups.Take(3)
                .Select(g => string.Join(" / ", g.Select(s => $"{s.Name} ({s.ExternalCode})"))));
            suggestions.Add(new("sites-duplicates", "high", "Likely duplicate sites",
                $"{duplicateSiteGroups.Count} likely duplicate site group{(duplicateSiteGroups.Count == 1 ? "" : "s")} found: {examples}. The assistant can safely archive confirmed duplicates.",
                "Sites", true));
        }

        if (suggestions.Count == 0)
            suggestions.Add(new("ready", "info", "Plan looks ready",
                "No blocking planning or master-data issue was found. Confirm late changes and lock dispatch.",
                "Planner", false));

        return new AssistantSnapshot(
            planningDate,
            DateTimeOffset.UtcNow,
            options.IsConfigured ? "OpenAI gpt-4o + SLH safety rules" : "SLH safety rules",
            options.IsConfigured,
            new AssistantMetrics(
                orders.Count,
                unplanned.Count,
                loads.Count,
                unallocated.Count,
                drivers.Count,
                vehicles.Count,
                vehicleRisk,
                0, 0,
                emptyMiles,
                missingMapPoints,
                duplicateSiteGroups.Count),
            suggestions);
    }

    // ── Advice (AI) ───────────────────────────────────────────────────────────

    public async Task<AssistantAdvice> Advise(DateOnly planningDate, string message, string userKey, CancellationToken ct)
    {
        var snapshot = await GetSnapshot(planningDate, ct);
        var fallback = RuleBasedAnswer(message, snapshot);
        if (!options.IsConfigured) return new AssistantAdvice(fallback, snapshot.Source, snapshot.Suggestions);

        try
        {
            var liveContext = await BuildLiveContextAsync(planningDate, ct);

            var systemPrompt = $"""
                You are the SLH TMS operations assistant for Stuart Lyons Haulage, a UK road haulage company based in West Sussex.
                Today is {planningDate:dddd dd MMM yyyy}. You have access to live operational data injected below.

                WHO YOU HELP:
                Transport planners and operations managers who need fast, specific, actionable answers.
                They know the business — do not explain basic concepts. Be direct and concise.

                WHAT YOU KNOW (from live data provided):
                - Every active driver: Tacho hours remaining today/week, compliance status, previous vehicle, day-cycle position, live location, dispatch suggestions, blocked reasons
                - Every unallocated run for today: collection point, skills required, backload flag
                - Route optimiser analysis: which runs have savings opportunities and how much
                - Fleet compliance: vehicles with MOT/PMI due or VOR status
                - Site master: missing coordinates, missing geofences, duplicate sites
                - TachoMaster: pending new driver reviews, stale sync warnings

                RULES — ALWAYS:
                1. Legal driver hours come first. Never suggest a driver whose Tacho hours are insufficient for the run.
                2. Never suggest allocating a blocked driver — state why they are blocked.
                3. New Tacho drivers in the review queue cannot be dispatched until promoted.
                4. Prefer the driver's live DOT/Falcon vehicle. Show previous vehicle as fallback.
                5. For routing, geofence coordinates beat site address coordinates beat order coordinates.
                6. If a driver has stale Tacho data (>12h), say so and caveat any hours suggestion.
                7. CPC or digital tacho card expired = hard block. State this explicitly.
                8. Do not discuss rates, margins, or costs.
                9. If you don't have enough live data to answer confidently, say what's missing and why.

                FORMAT:
                - Lead with the direct answer. One sentence.
                - Then specifics: driver name, registration, hours remaining, reason.
                - Use short bullet points only when listing multiple options.
                - Maximum 300 words. Planners are busy.
                """;

            var userContent = $"""
                Planning date: {planningDate:dd MMM yyyy}

                QUESTION: {message.Trim()}

                === LIVE OPERATIONAL SNAPSHOT ===
                {JsonSerializer.Serialize(snapshot, JsonOptions)}

                === LIVE DRIVER, DISPATCH & OPTIMISER CONTEXT ===
                {JsonSerializer.Serialize(liveContext, JsonOptions)}
                """;

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.BaseUrl.TrimEnd('/')}/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model     = options.Model,
                temperature = 0.15,
                max_tokens  = 500,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userContent  }
                }
            });

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.TimeoutSeconds, 5, 60)));
            using var response = await httpClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var answer = document.RootElement.TryGetProperty("choices", out var choices) &&
                         choices.GetArrayLength() > 0 &&
                         choices[0].TryGetProperty("message", out var msg) &&
                         msg.TryGetProperty("content", out var content) &&
                         content.ValueKind == JsonValueKind.String
                ? content.GetString() ?? string.Empty
                : string.Empty;

            return new AssistantAdvice(
                string.IsNullOrWhiteSpace(answer) ? fallback : answer,
                snapshot.Source,
                snapshot.Suggestions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "OpenAI advice unavailable; returning deterministic SLH guidance.");
            return new AssistantAdvice(fallback, "SLH safety rules (AI temporarily unavailable)", snapshot.Suggestions);
        }
    }

    // ── Live context builder ──────────────────────────────────────────────────

    private async Task<object> BuildLiveContextAsync(DateOnly planningDate, CancellationToken ct)
    {
        // Drivers + dispatch suggestions (the richest source — computed by DispatchService)
        List<object> driverContext = [];
        try
        {
            var dispatchDrivers = await dispatch.GetDriversAsync(planningDate, ct);
            // Pull LastTachoSyncUtc from DB to show data freshness (not on DispatchTachoDataDto)
            var driverIds = dispatchDrivers.Select(d => d.DriverId).ToList();
            var syncTimes = await db.Drivers.AsNoTracking()
                .Where(d => driverIds.Contains(d.Id))
                .Select(d => new { d.Id, d.LastTachoSyncUtc })
                .ToDictionaryAsync(d => d.Id, d => d.LastTachoSyncUtc, ct);
            driverContext = dispatchDrivers.Select(d =>
            {
                var lastSync = syncTimes.GetValueOrDefault(d.DriverId);
                return (object)new
            {
                driverId         = d.DriverId,
                name             = d.Name,
                driverCode       = d.DriverCode,
                employmentType   = d.EmploymentType,
                isBlocked        = d.IsBlocked,
                blockedReason    = d.BlockedReason,
                needsReturn      = d.NeedsReturn,
                availableFrom    = d.AvailableFrom,
                // Tacho hours from dispatch service (already calculated against legal limits)
                tachoDriveAvailableMinutesToday = d.TachoData.DriveAvailablePlanningDayMinutes,
                tachoDriveAvailableHoursToday   = d.TachoData.DriveAvailablePlanningDayMinutes.HasValue
                    ? $"{d.TachoData.DriveAvailablePlanningDayMinutes / 60:F0}h {d.TachoData.DriveAvailablePlanningDayMinutes % 60}m"
                    : "unknown",
                // Live position from DOT/Falcon
                liveLocation     = d.TrackingData.LastStopName,
                liveVehicle      = d.TachoData.LastVehicleRegistration,
                // Dispatch suggestion computed by DispatchService
                suggestedRunId        = d.SuggestedRunId,
                suggestedRunReference = d.SuggestedRunReference,
                distanceToCollectionMiles = d.DistanceToSuggestedCollectionMiles,
                backloadCandidate    = d.BackloadCandidate,
                deadheadReductionMiles = d.DeadheadReductionMiles,
                dispatchSuggestion   = d.Suggestion,
                skills               = d.Skills.ToString(),
                tachoDataFresh = lastSync.HasValue && lastSync.Value >= DateTimeOffset.UtcNow.AddHours(-12),
                tachoSyncAge   = lastSync.HasValue ? $"{(DateTimeOffset.UtcNow - lastSync.Value).TotalHours:F1}h ago" : "never synced"
                };
            }).ToList<object>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not load dispatch driver list.");
            db.ChangeTracker.Clear();
        }

        // Unallocated runs for today
        List<object> runContext = [];
        try
        {
            var runs = await dispatch.GetRunsAsync(planningDate, ct);
            runContext = runs.Select(r => (object)new
            {
                runId           = r.RunId,
                reference       = r.Reference,
                collection      = r.CollectionPoint.Name,
                requiredSkills  = r.RequiredSkills.ToString(),
                requiresDoubleDeck    = r.RequiresDoubleDeck,
                requiresRefrigerated  = r.RequiresRefrigerated,
                isBackload      = r.IsBackload,
                isOvernightMarket = r.IsOvernightMarket,
                isSouthbound    = r.IsSouthbound
            }).ToList<object>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not load dispatch run list.");
            db.ChangeTracker.Clear();
        }

        // Optimiser analysis
        object? optimiserContext = null;
        try
        {
            var optDay = await optimiser.AnalyseDayAsync(planningDate, ct);
            if (optDay is not null)
            {
                optimiserContext = new
                {
                    totalRuns          = optDay.RunCount,
                    routedRuns         = optDay.RoutedRunCount,
                    unroutedRuns       = optDay.UnroutedRunCount,
                    currentMiles       = optDay.CurrentMiles,
                    projectedMiles     = optDay.ProjectedMiles,
                    savingMiles        = optDay.SavingMiles,
                    savingDriveMinutes = optDay.SavingDriveMinutes,
                    topSavings         = optDay.Routes
                        .Where(r => r.SavingMiles > 0)
                        .OrderByDescending(r => r.SavingMiles)
                        .Take(5)
                        .Select(r => new
                        {
                            r.Reference,
                            r.Driver,
                            r.Vehicle,
                            r.SavingMiles,
                            r.SavingDriveMinutes,
                            r.Rationale
                        }).ToList()
                };
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not load optimiser analysis.");
        }

        // Pending Tacho driver reviews
        List<object> pendingReviews = [];
        try
        {
            pendingReviews = (await db.StagedImports.AsNoTracking()
                .Where(r => r.EntityType == "driverreview" && r.Status == StagingStatus.PendingReview)
                .Select(r => new { r.IdempotencyKey, r.ReviewNote, r.ReceivedAtUtc })
                .Take(20)
                .ToListAsync(ct)).Cast<object>().ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant could not load pending driver reviews.");
            db.ChangeTracker.Clear();
        }

        return new
        {
            drivers            = driverContext,
            unallocatedRuns    = runContext,
            optimiserAnalysis  = optimiserContext,
            pendingDriverReviews = pendingReviews,
            contextGeneratedUtc  = DateTimeOffset.UtcNow
        };
    }

    // ── Deterministic fallback ────────────────────────────────────────────────

    private static string RuleBasedAnswer(string message, AssistantSnapshot snapshot)
    {
        var msg = message.ToLowerInvariant();
        var high = snapshot.Suggestions.Where(x => x.Severity == "high").ToList();
        var med  = snapshot.Suggestions.Where(x => x.Severity == "medium").ToList();

        if (msg.Contains("ready") || msg.Contains("can we go") || msg.Contains("start dispatch"))
        {
            if (high.Count == 0)
                return $"No high-priority issues for {snapshot.PlanningDate:dd MMM}. {snapshot.Metrics.UnallocatedLoads} run{(snapshot.Metrics.UnallocatedLoads == 1 ? "" : "s")} still need allocating. Confirm late changes and lock dispatch.";
            return $"Not yet. {high.Count} high-priority issue{(high.Count == 1 ? "" : "s")} must be resolved first: {string.Join("; ", high.Select(x => x.Title))}.";
        }

        if (msg.Contains("driver") || msg.Contains("who"))
        {
            var driverIssues = high.Concat(med).Where(x => x.Area == "Drivers").Take(3).ToList();
            return driverIssues.Count > 0
                ? $"Driver issues for {snapshot.PlanningDate:dd MMM}: {string.Join(" | ", driverIssues.Select(x => $"{x.Title}: {x.Detail}"))}"
                : "No driver issues currently flagged. Enable the AI assistant for live Tacho hours and dispatch suggestions.";
        }

        if (msg.Contains("optim") || msg.Contains("saving") || msg.Contains("miles"))
        {
            var opt = snapshot.Suggestions.FirstOrDefault(x => x.Id == "optimiser-savings");
            return opt is not null ? opt.Detail : "No optimiser savings flagged. Enable the AI assistant for live route analysis.";
        }

        var priorities = high.Concat(med).Take(4).ToList();
        return priorities.Count == 0
            ? $"No blocking issues for {snapshot.PlanningDate:dd MMM}. Refresh live tracking and confirm late changes before dispatch."
            : $"Top priorities for {snapshot.PlanningDate:dd MMM}: {string.Join(" | ", priorities.Select((x, i) => $"{i + 1}) {x.Title}"))}. Ask the AI assistant for specifics.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static string NormaliseRegistration(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private async Task<List<TransportOrder>> ReadOrders(DateOnly date, CancellationToken ct)
    {
        var primary = await SafeRead(db.TransportOrders.AsNoTracking()
            .Where(x => x.CollectionDate == date && x.Status != OrderStatus.Cancelled)
            .OrderBy(x => x.Reference).Take(500), "orders", ct);
        if (primary.Count > 0) return primary;
        try { return await PlanningRegisterStore.ReadOrdersAsync(db, date, date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant planning-register order fallback was unavailable.");
            return [];
        }
    }

    private async Task<List<Load>> ReadLoads(DateOnly date, CancellationToken ct)
    {
        var primary = await SafeRead(db.Loads.AsNoTracking().Include(x => x.Stops)
            .Where(x => x.PlanningDate == date && x.Status != LoadStatus.Cancelled)
            .OrderBy(x => x.Reference).Take(500), "loads", ct);
        if (primary.Count > 0) return primary;
        try { return await PlanningRegisterStore.ReadLoadsAsync(db, date, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant planning-register load fallback was unavailable.");
            return [];
        }
    }

    private async Task SafeEnrichSites(List<Site> sites, CancellationToken ct)
    {
        if (sites.Count == 0) return;
        try { await MasterDetailStore.EnrichSitesAsync(db, sites, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant site-detail enrichment was unavailable.");
        }
    }

    private async Task<(int SitesMissingGeofence, bool AutoFixAvailable)> GeofenceLinkGaps(CancellationToken ct)
    {
        try
        {
            var statuses = await SiteGeofenceMasterSync.GetStatusAsync(db, ct);
            var missing  = statuses.Count(s => s.NeedsReview);
            return (missing, missing > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant geofence link scan was unavailable.");
            db.ChangeTracker.Clear();
            return (0, false);
        }
    }

    private async Task<List<T>> SafeRead<T>(IQueryable<T> query, string area, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Assistant skipped unavailable {Area} data.", area);
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static List<List<Site>> FindDuplicateSiteGroups(IReadOnlyCollection<Site> sites)
    {
        var pairs = new Dictionary<string, HashSet<Site>>(StringComparer.OrdinalIgnoreCase);
        foreach (var site in sites)
        {
            var name    = NormaliseSiteValue(site.Name);
            var address = NormaliseSiteValue(site.CollectionAddress);
            foreach (var key in new[]
            {
                name.Length    >= 5 ? $"name:{name}"       : "",
                address.Length >= 8 ? $"address:{address}" : ""
            }.Where(k => k.Length > 0))
            {
                if (!pairs.TryGetValue(key, out var g)) pairs[key] = g = [];
                g.Add(site);
            }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return pairs.Values
            .Where(g => g.Count > 1)
            .Select(g => g.OrderBy(s => s.ExternalCode).ToList())
            .Where(g => seen.Add(string.Join('|', g.Select(s => s.Id).OrderBy(id => id))))
            .ToList();
    }

    private static string NormaliseSiteValue(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string SafetyIdentifier(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
}

public sealed record AssistantSuggestion(string Id, string Severity, string Title, string Detail, string Area, bool AutoFixAvailable);
public sealed record AssistantMetrics(int Orders, int UnplannedOrders, int Loads, int UnallocatedLoads, int ActiveDrivers, int ActiveVehicles, int VehicleComplianceRisks, int UnpricedLoads, int NegativeMarginLoads, decimal EmptyMiles, int MissingSiteMapPoints, int DuplicateSiteGroups);
public sealed record AssistantSnapshot(DateOnly PlanningDate, DateTimeOffset GeneratedAtUtc, string Source, bool AiConfigured, AssistantMetrics Metrics, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record AssistantAdvice(string Answer, string Source, IReadOnlyList<AssistantSuggestion> Suggestions);
public sealed record SafeFixResult(int Applied, int Skipped, IReadOnlyList<string> Changes, IReadOnlyList<string> SkippedReasons);
