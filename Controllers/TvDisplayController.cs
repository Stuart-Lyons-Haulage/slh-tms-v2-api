using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/tv-display")]
public sealed class TvDisplayController(TmsDbContext db, AzureMapsRouteClient maps, IConfiguration configuration) : ControllerBase
{
    private const int MaxLiveEtaCalculationsPerRefresh = 12;
    private static readonly TimeSpan MapEtaBudget = TimeSpan.FromSeconds(2);

    [HttpGet("key"), Authorize(Policy = "TmsAccess")]
    public async Task<IActionResult> Key(CancellationToken ct)
    {
        var access = await TvDisplayKeyStore.GetOrCreateAsync(db, User.Identity?.Name, ct);
        return Ok(new
        {
            access.Key,
            access.CreatedAtUtc,
            urlPath = $"/tv#key={Uri.EscapeDataString(access.Key)}"
        });
    }

    [HttpPost("key/rotate"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Rotate(CancellationToken ct)
    {
        var access = await TvDisplayKeyStore.RotateAsync(db, User.Identity?.Name, ct);
        return Ok(new
        {
            access.Key,
            access.CreatedAtUtc,
            urlPath = $"/tv#key={Uri.EscapeDataString(access.Key)}"
        });
    }

    [HttpGet("pairing-code"), Authorize(Policy = "TmsAccess")]
    public async Task<IActionResult> PairingCode(CancellationToken ct)
    {
        var pairing = await TvDisplayPairingStore.GetOrCreateAsync(db, User.Identity?.Name, ct);
        return Ok(new
        {
            pairing.Code,
            pairing.CreatedAtUtc,
            pairing.ExpiresAtUtc,
            tvPath = "/tv"
        });
    }

    [HttpPost("pairing-code/refresh"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> RefreshPairingCode(CancellationToken ct)
    {
        var pairing = await TvDisplayPairingStore.RefreshAsync(db, User.Identity?.Name, ct);
        return Ok(new
        {
            pairing.Code,
            pairing.CreatedAtUtc,
            pairing.ExpiresAtUtc,
            tvPath = "/tv"
        });
    }

    [HttpPost("pair"), AllowAnonymous]
    public async Task<IActionResult> Pair(TvDisplayPairRequest request, CancellationToken ct)
    {
        var client = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (!TvDisplayPairingRateLimiter.Allow(client))
            return StatusCode(StatusCodes.Status429TooManyRequests, new { message = "Too many pairing attempts. Wait one minute and try again." });

        var access = await TvDisplayPairingStore.ExchangeAsync(db, request.Code, ct);
        if (access is null)
            return Unauthorized(new { message = "That TV pairing code is invalid or has expired. Generate a fresh code from TV display in the signed-in TMS." });

        return Ok(new { access.Key, pairedAtUtc = DateTimeOffset.UtcNow });
    }

    [HttpGet("live-runs"), AllowAnonymous]
    public async Task<IActionResult> LiveRuns([FromHeader(Name = "X-TV-Display-Key")] string? displayKey, [FromQuery] DateOnly? date, CancellationToken ct)
    {
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This TV display is not paired. Open the TV display page in the signed-in TMS to get a new pairing code." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();
        var carried = (await PlanningResilience.ReadLoadsAsync(db, day.AddDays(-1), ct))
            .Where(load => load.Status != LoadStatus.Cancelled && load.Status != LoadStatus.Completed)
            .ToList();

        foreach (var carry in carried)
        {
            var key = PlanningResilience.LogicalRunKey(carry);
            if (loads.All(current => !string.Equals(PlanningResilience.LogicalRunKey(current), key, StringComparison.OrdinalIgnoreCase)))
                loads.Add(carry);
        }

        loads = PlanningResilience.CollapseLogicalDuplicates(loads);
        await RunOperationalStore.EnrichAsync(db, loads, ct);
        WallboardPhysicalStops.Apply(loads);

        var driverIds = loads.Where(x => x.DriverId is not null).Select(x => x.DriverId!.Value).Distinct().ToList();
        var vehicleIds = loads.Where(x => x.VehicleId is not null).Select(x => x.VehicleId!.Value).Distinct().ToList();
        var trailerIds = loads.Where(x => x.TrailerId is not null).Select(x => x.TrailerId!.Value).Distinct().ToList();

        var drivers = await SafeDictionary(db.Drivers.AsNoTracking().Where(x => driverIds.Contains(x.Id)), x => x.Id, ct);
        var vehicles = await SafeDictionary(db.Vehicles.AsNoTracking().Where(x => vehicleIds.Contains(x.Id)), x => x.Id, ct);
        var trailers = await SafeDictionary(db.Trailers.AsNoTracking().Where(x => trailerIds.Contains(x.Id)), x => x.Id, ct);
        var liveStatuses = await SafeList(db.VehicleLiveStatuses.AsNoTracking(), ct);

        loads = loads.Where(load => load.PlanningDate == day ||
            (load.VehicleId is Guid vehicleId && vehicles.TryGetValue(vehicleId, out var vehicle) && MatchLive(vehicle, liveStatuses) is { } live && now - TrackingFreshnessUtc(live) <= TimeSpan.FromMinutes(30)) ||
            load.Status is LoadStatus.Dispatched or LoadStatus.InProgress)
            .OrderBy(load => load.PlanningDate)
            .ThenBy(load => load.Reference)
            .ToList();

        var loadIds = loads.Select(load => load.Id).ToList();
        List<GeofenceVisit> durableVisits = loadIds.Count == 0
            ? []
            : await SafeList(db.GeofenceVisits.AsNoTracking().Where(visit => visit.LoadId != null && loadIds.Contains(visit.LoadId.Value)), ct);
        var fenceIds = durableVisits.Select(visit => visit.GeofenceId).Distinct().ToList();
        Dictionary<Guid, SiteGeofence> fences = fenceIds.Count == 0
            ? []
            : await SafeDictionary(db.SiteGeofences.AsNoTracking().Where(fence => fenceIds.Contains(fence.Id)), fence => fence.Id, ct);

        var rows = new List<TvRunDisplayRow>();
        foreach (var load in loads)
        {
            drivers.TryGetValue(load.DriverId ?? Guid.Empty, out var driver);
            vehicles.TryGetValue(load.VehicleId ?? Guid.Empty, out var vehicle);
            trailers.TryGetValue(load.TrailerId ?? Guid.Empty, out var trailer);

            var live = vehicle is null ? null : MatchLive(vehicle, liveStatuses);
            var stops = load.Stops.OrderBy(x => x.Sequence).ToList();
            var visits = durableVisits
                .Where(visit => visit.LoadId == load.Id)
                .OrderBy(visit => visit.EnteredAtUtc)
                .ToList();
            var completedStopIds = visits
                .Where(visit => visit.ExitedAtUtc is not null && visit.LoadStopId is not null)
                .Select(visit => visit.LoadStopId!.Value)
                .ToHashSet();

            if (ShouldHideCompletedRun(load, completedStopIds)) continue;

            var currentVisit = visits
                .Where(visit => visit.ExitedAtUtc is null)
                .OrderByDescending(visit => visit.EnteredAtUtc)
                .FirstOrDefault();
            var lastDeparted = visits
                .Where(visit => visit.ExitedAtUtc is not null)
                .OrderByDescending(visit => visit.ExitedAtUtc)
                .FirstOrDefault();
            fences.TryGetValue(currentVisit?.GeofenceId ?? Guid.Empty, out var currentFence);

            var nextStop = currentVisit?.LoadStopId is Guid currentStopId
                ? stops.FirstOrDefault(stop => stop.Id == currentStopId)
                : stops.FirstOrDefault(stop => !completedStopIds.Contains(stop.Id))
                    ?? PickNextStop(stops, now, load.Status);
            var firstStop = stops.FirstOrDefault();
            var finalStop = stops.LastOrDefault();
            var routeComplete = stops.Count > 0 && completedStopIds.Count >= stops.Count;
            var etaTarget = routeComplete ? null : finalStop;
            var eta = etaTarget?.PlannedArrivalUtc;
            var etaSource = eta is null ? "Unavailable" : "Planned";

            var trackingFreshnessUtc = live is null ? (DateTimeOffset?)null : TrackingFreshnessUtc(live);
            var trackingAgeMinutes = trackingFreshnessUtc is null ? (double?)null : Math.Max(0, (now - trackingFreshnessUtc.Value).TotalMinutes);
            var state = State(load, driver, vehicle, live, trackingAgeMinutes, eta, finalStop?.PlannedArrivalUtc);

            int? liveDwellSeconds = null;
            int? liveDwellMinutes = null;
            if (currentVisit is not null)
            {
                liveDwellSeconds = Math.Max(0, (int)Math.Floor((now - currentVisit.EnteredAtUtc).TotalSeconds));
                liveDwellMinutes = Math.Max(currentVisit.DwellMinutes, liveDwellSeconds.Value / 60);
                var delayed =
                    currentFence?.MaxWaitMinutes is int waitLimit && liveDwellMinutes.Value > waitLimit ||
                    currentFence?.CategoryMaxWaitMinutes is int categoryWaitLimit && liveDwellMinutes.Value > categoryWaitLimit;
                state = delayed
                    ? ("SITE DELAY", $"{currentFence?.Name ?? "Site"} · time on site {liveDwellMinutes} min", 98)
                    : ("ON SITE", $"{currentFence?.Name ?? "Site"} · time on site {liveDwellMinutes} min", 88);
            }

            rows.Add(new TvRunDisplayRow(
                load.Id,
                load.Reference,
                load.Status.ToString(),
                driver?.DisplayName ?? "Driver TBC",
                vehicle?.Registration ?? "Vehicle TBC",
                trailer?.TrailerNumber,
                firstStop?.PlannedArrivalUtc,
                finalStop?.PlannedArrivalUtc,
                nextStop?.Name,
                finalStop?.Name,
                etaTarget?.Name,
                eta,
                etaSource,
                live is null ? "No live tracking" : TrackingText(live, trackingAgeMinutes ?? 0),
                trackingFreshnessUtc,
                live?.SpeedKph,
                state.Label,
                state.Detail,
                state.Priority,
                currentVisit?.EnteredAtUtc,
                lastDeparted?.ExitedAtUtc,
                liveDwellMinutes,
                liveDwellSeconds,
                lastDeparted?.DwellMinutes,
                lastDeparted is null ? null : lastDeparted.DwellMinutes * 60,
                currentVisit is not null ? "OnSite" : lastDeparted is not null ? "Departed" : "EnRoute",
                null));
        }

        return Ok(new
        {
            planningDate = day,
            generatedAtUtc = now,
            refreshSeconds = 20,
            runCount = rows.Count,
            runs = rows
                .OrderByDescending(row => row.Priority)
                .ThenBy(row => row.FirstPlannedUtc ?? DateTimeOffset.MaxValue)
                .ToList()
        });
    }

    internal static bool ShouldHideCompletedRun(Load load, IReadOnlySet<Guid> completedStopIds)
    {
        if (load.Status == LoadStatus.Completed) return true;
        var stops = load.Stops ?? [];
        return stops.Count > 0 && stops.All(stop => completedStopIds.Contains(stop.Id));
    }

    private static LoadStop? PickNextStop(List<LoadStop> stops, DateTimeOffset now, LoadStatus status)
    {
        if (status == LoadStatus.Completed || stops.Count == 0) return null;
        var cutoff = now - TimeSpan.FromMinutes(15);
        return stops.FirstOrDefault(stop => stop.PlannedArrivalUtc is null || stop.PlannedArrivalUtc >= cutoff) ?? stops.Last();
    }

    private static (string Label, string Detail, int Priority) State(Load load, Driver? driver, Vehicle? vehicle, VehicleLiveStatus? live, double? ageMinutes, DateTimeOffset? eta, DateTimeOffset? planned)
    {
        if (load.Status == LoadStatus.Completed) return ("COMPLETED", "Run completed", 0);
        if (driver is null || vehicle is null) return ("NEEDS ALLOCATION", "Driver or vehicle not allocated", 100);
        if (live is null) return (load.Status == LoadStatus.InProgress ? "IN PROGRESS" : "UPCOMING", "Waiting for live vehicle tracking", 60);
        if (ageMinutes is > 30) return ("TRACKING STALE", $"Last tracking receipt {Math.Round(ageMinutes.Value)} min ago", 90);

        var moving = live.IsMoving == true || (live.SpeedKph ?? 0) > 2;
        if (moving)
        {
            if (eta is not null && planned is not null && eta > planned.Value.AddMinutes(15))
                return ("AT RISK", "Live ETA is more than 15 minutes behind plan", 95);
            return ("MOVING", eta is null ? "Live vehicle movement" : "Live ETA active", 80);
        }

        if (live.IgnitionOn == true) return ("STATIONARY", "Vehicle signed on / stationary", 70);
        return (load.Status == LoadStatus.InProgress ? "IN PROGRESS" : "UPCOMING", "Vehicle parked", 50);
    }

    private static string TrackingText(VehicleLiveStatus live, double ageMinutes)
    {
        var age = ageMinutes < 1 ? "now" : $"{Math.Round(ageMinutes)}m ago";
        if (live.IsMoving == true || (live.SpeedKph ?? 0) > 2) return $"Moving · {Math.Round(live.SpeedKph ?? 0)} km/h · {age}";
        if (live.IgnitionOn == true) return $"Stationary · {age}";
        return $"Parked · {age}";
    }

    private static DateTimeOffset TrackingFreshnessUtc(VehicleLiveStatus live) => live.LastReceivedAtUtc;

    private static VehicleLiveStatus? MatchLive(Vehicle vehicle, List<VehicleLiveStatus> statuses)
    {
        var aliases = new[] { vehicle.Registration, vehicle.FleetNumber, vehicle.Abbreviation }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => Normalise(value!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return statuses
            .Where(status => aliases.Contains(Normalise(status.VehicleIdentifier)))
            .OrderByDescending(status => status.LastReceivedAtUtc)
            .ThenByDescending(status => status.LastEventTimeUtc)
            .FirstOrDefault();
    }

    private static string Normalise(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }

    private static async Task<Dictionary<TKey, TValue>> SafeDictionary<TValue, TKey>(IQueryable<TValue> query, Func<TValue, TKey> key, CancellationToken ct) where TKey : notnull
    {
        try { return await query.ToDictionaryAsync(key, ct); }
        catch { return new Dictionary<TKey, TValue>(); }
    }

    private static async Task<List<TValue>> SafeList<TValue>(IQueryable<TValue> query, CancellationToken ct)
    {
        try { return await query.ToListAsync(ct); }
        catch { return []; }
    }
}

internal static class TvDisplayKeyStore
{
    private const string EntityType = "tvdisplaykey";
    private const string IdempotencyKey = "tvdisplay:key:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<TvDisplayAccess> GetOrCreateAsync(TmsDbContext db, string? user, CancellationToken ct)
    {
        var existing = await ExistingAsync(db, ct);
        if (existing is not null) return existing;
        return await SaveAsync(db, NewKey(), user, ct);
    }

    public static Task<TvDisplayAccess> RotateAsync(TmsDbContext db, string? user, CancellationToken ct) => SaveAsync(db, NewKey(), user, ct);

    public static async Task<bool> ValidateAsync(TmsDbContext db, string? supplied, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var existing = await ExistingAsync(db, ct);
        if (existing is null) return false;
        return SecureEquals(existing.Key, supplied.Trim());
    }

    private static async Task<TvDisplayAccess?> ExistingAsync(TmsDbContext db, CancellationToken ct)
    {
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.EntityType == EntityType && x.IdempotencyKey == IdempotencyKey && x.Status == StagingStatus.Promoted, ct);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<TvDisplayAccess>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static async Task<TvDisplayAccess> SaveAsync(TmsDbContext db, string key, string? user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var access = new TvDisplayAccess(key, now);
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == IdempotencyKey, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = EntityType,
                IdempotencyKey = IdempotencyKey,
                PayloadJson = "{}",
                Source = "SLH secure TV display"
            };
            db.StagedImports.Add(row);
        }
        row.EntityType = EntityType;
        row.PayloadJson = JsonSerializer.Serialize(access, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = now;
        row.ReviewedBy = user;
        row.ReviewNote = "Read-only Live Runs TV display key generated/rotated.";
        await db.SaveChangesAsync(ct);
        return access;
    }

    private static string NewKey()
    {
        var value = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return value.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool SecureEquals(string expected, string supplied)
    {
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}

internal static class TvDisplayPairingStore
{
    private const string EntityType = "tvdisplaypairing";
    private const string IdempotencyKey = "tvdisplay:pairing:v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<TvDisplayPairing> GetOrCreateAsync(TmsDbContext db, string? user, CancellationToken ct)
    {
        var current = await CurrentAsync(db, ct);
        if (current is not null && current.UsedAtUtc is null && current.ExpiresAtUtc > DateTimeOffset.UtcNow)
            return current;
        return await SaveNewAsync(db, user, ct);
    }

    public static Task<TvDisplayPairing> RefreshAsync(TmsDbContext db, string? user, CancellationToken ct) => SaveNewAsync(db, user, ct);

    public static async Task<TvDisplayAccess?> ExchangeAsync(TmsDbContext db, string? suppliedCode, CancellationToken ct)
    {
        var code = (suppliedCode ?? string.Empty).Trim();
        if (code.Length != 6 || code.Any(character => !char.IsDigit(character))) return null;

        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.EntityType == EntityType && x.IdempotencyKey == IdempotencyKey && x.Status == StagingStatus.Promoted, ct);
        if (row is null) return null;

        TvDisplayPairing? pairing;
        try { pairing = JsonSerializer.Deserialize<TvDisplayPairing>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
        if (pairing is null || pairing.UsedAtUtc is not null || pairing.ExpiresAtUtc <= DateTimeOffset.UtcNow || !TvDisplayKeyStore.SecureEquals(pairing.Code, code))
            return null;

        var now = DateTimeOffset.UtcNow;
        pairing = pairing with { UsedAtUtc = now };
        row.PayloadJson = JsonSerializer.Serialize(pairing, JsonOptions);
        row.ReviewedAtUtc = now;
        row.ReviewedBy = "TV pairing";
        row.ReviewNote = "TV paired successfully using the one-time display code.";
        await db.SaveChangesAsync(ct);

        return await TvDisplayKeyStore.GetOrCreateAsync(db, "TV pairing", ct);
    }

    private static async Task<TvDisplayPairing?> CurrentAsync(TmsDbContext db, CancellationToken ct)
    {
        var row = await db.StagedImports.AsNoTracking().SingleOrDefaultAsync(x => x.EntityType == EntityType && x.IdempotencyKey == IdempotencyKey && x.Status == StagingStatus.Promoted, ct);
        if (row is null) return null;
        try { return JsonSerializer.Deserialize<TvDisplayPairing>(row.PayloadJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static async Task<TvDisplayPairing> SaveNewAsync(TmsDbContext db, string? user, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pairing = new TvDisplayPairing(RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"), now, now.AddMinutes(10), null);
        var row = await db.StagedImports.SingleOrDefaultAsync(x => x.IdempotencyKey == IdempotencyKey, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = EntityType,
                IdempotencyKey = IdempotencyKey,
                PayloadJson = "{}",
                Source = "SLH TV pairing"
            };
            db.StagedImports.Add(row);
        }
        row.EntityType = EntityType;
        row.PayloadJson = JsonSerializer.Serialize(pairing, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = now;
        row.ReviewedBy = user;
        row.ReviewNote = "One-time TV pairing code generated; valid for 10 minutes.";
        await db.SaveChangesAsync(ct);
        return pairing;
    }
}

internal static class TvDisplayPairingRateLimiter
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, List<DateTimeOffset>> Attempts = new(StringComparer.OrdinalIgnoreCase);

    public static bool Allow(string client)
    {
        lock (Gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (!Attempts.TryGetValue(client, out var attempts))
            {
                attempts = [];
                Attempts[client] = attempts;
            }
            attempts.RemoveAll(attempt => now - attempt > TimeSpan.FromMinutes(1));
            if (attempts.Count >= 10) return false;
            attempts.Add(now);
            return true;
        }
    }
}

public sealed record TvDisplayPairRequest(string? Code);
internal sealed record TvDisplayAccess(string Key, DateTimeOffset CreatedAtUtc);
internal sealed record TvDisplayPairing(string Code, DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, DateTimeOffset? UsedAtUtc);
internal sealed record TvRunDisplayRow(Guid Id, string Reference, string Status, string Driver, string Vehicle, string? Trailer,
    DateTimeOffset? FirstPlannedUtc, DateTimeOffset? FinalPlannedUtc, string? NextStop, string? FinalStop, string? EtaTarget, DateTimeOffset? EtaUtc, string EtaSource,
    string Tracking, DateTimeOffset? TrackingUpdatedAtUtc, decimal? SpeedKph, string State, string StateDetail, int Priority,
    DateTimeOffset? SiteArrivalUtc, DateTimeOffset? SiteDepartureUtc, int? LiveDwellMinutes, int? LiveDwellSeconds,
    int? FinalDwellMinutes, int? FinalDwellSeconds, string DwellState, string? LinkageException);
