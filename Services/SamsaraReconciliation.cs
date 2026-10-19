using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps the TMS and Samsara (our two main platforms) in step: drivers, vehicles and trailers
/// compared both ways. Matches are linked (so runs land on the right driver and truck); gaps
/// are listed for a planner to fix. Nothing is created or removed on either side by the check.
/// </summary>
public static class SamsaraReconciliation
{
    public const string SnapshotType = "samsarareconcile";
    private const string SnapshotKey = "samsara-reconcile:latest";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Item(string Name, string? Detail = null);
    public sealed record Section(int Tms, int Samsara, int Matched, IReadOnlyList<Item> OnlyInTms, IReadOnlyList<Item> OnlyInSamsara, IReadOnlyList<Item> Differences, string? Problem = null);
    public sealed record Result(DateTimeOffset CheckedAtUtc, Section Drivers, Section Vehicles, Section Trailers, string? Problem)
    {
        public int Gaps => Drivers.OnlyInTms.Count + Drivers.OnlyInSamsara.Count + Vehicles.OnlyInTms.Count + Vehicles.OnlyInSamsara.Count + Trailers.OnlyInTms.Count + Trailers.OnlyInSamsara.Count;
    }

    private static readonly Section Empty = new(0, 0, 0, [], [], []);

    public static async Task<Result> RunAsync(TmsDbContext db, SamsaraClient samsara, string actor, CancellationToken ct)
    {
        if (!samsara.IsConfigured) return await StoreAsync(db, new Result(DateTimeOffset.UtcNow, Empty, Empty, Empty, "Samsara isn't set up."), ct);
        IReadOnlyList<SamsaraDriver> remoteDrivers;
        IReadOnlyList<SamsaraVehicle> remoteVehicles;
        try
        {
            var driversTask = samsara.GetDriversAsync(ct);
            var vehiclesTask = samsara.GetVehiclesAsync(ct);
            await Task.WhenAll(driversTask, vehiclesTask);
            remoteDrivers = driversTask.Result;
            remoteVehicles = vehiclesTask.Result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await StoreAsync(db, new Result(DateTimeOffset.UtcNow, Empty, Empty, Empty, $"Samsara couldn't be reached: {ex.GetBaseException().Message}"), ct);
        }
        IReadOnlyList<SamsaraAsset> remoteTrailers = [];
        string? trailerProblem = null;
        try { remoteTrailers = await samsara.GetAssetsAsync("trailer", ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { trailerProblem = $"Samsara trailers couldn't be read: {ex.GetBaseException().Message}"; }

        var drivers = await db.Drivers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var vehicles = await db.Vehicles.AsNoTracking().Where(item => item.Active).ToListAsync(ct);
        var trailers = await db.Trailers.AsNoTracking().Where(item => item.Active).ToListAsync(ct);

        var links = new List<(string Type, Guid Id, string RemoteId, string Label)>();
        var driverSection = Compare(drivers, remoteDrivers,
            driver => driver.DisplayName, remote => remote.Name ?? remote.Username ?? remote.Id,
            (driver, remote) => DriverMatches(driver, remote),
            (driver, remote) => !SameName(driver.DisplayName, remote.Name) ? $"TMS \"{driver.DisplayName}\" / Samsara \"{remote.Name}\"" : null,
            (driver, remote) => links.Add(("Driver", driver.Id, remote.Id, remote.Username ?? remote.Name ?? driver.DisplayName)));
        var vehicleSection = Compare(vehicles, remoteVehicles,
            vehicle => vehicle.Registration, remote => remote.LicensePlate ?? remote.Name ?? remote.Id,
            (vehicle, remote) => VehicleMatches(vehicle.Registration, vehicle.VIN, remote.LicensePlate, remote.Name, remote.Vin),
            (vehicle, remote) => null,
            (vehicle, remote) => links.Add(("Vehicle", vehicle.Id, remote.Id, vehicle.Registration)));
        var trailerSection = trailerProblem is not null
            ? Empty with { Tms = trailers.Count, Problem = trailerProblem }
            : Compare(trailers, remoteTrailers,
                trailer => trailer.TrailerNumber, remote => remote.Name ?? remote.LicensePlate ?? remote.Id,
                (trailer, remote) => Norm(remote.Name) == Norm(trailer.TrailerNumber) || Norm(remote.LicensePlate) == Norm(trailer.TrailerNumber) ||
                                     remote.ExternalIds.Values.Any(value => Norm(value) == Norm(trailer.Id.ToString("N"))),
                (trailer, remote) => null,
                (trailer, remote) => links.Add(("Trailer", trailer.Id, remote.Id, trailer.TrailerNumber)));

        await SaveLinksAsync(db, links, actor, ct);
        return await StoreAsync(db, new Result(DateTimeOffset.UtcNow, driverSection, vehicleSection, trailerSection, null), ct);
    }

    internal static Section Compare<TLocal, TRemote>(
        IReadOnlyList<TLocal> local, IReadOnlyList<TRemote> remote,
        Func<TLocal, string> localName, Func<TRemote, string> remoteName,
        Func<TLocal, TRemote, bool> matches, Func<TLocal, TRemote, string?> difference,
        Action<TLocal, TRemote> link)
    {
        var usedRemote = new HashSet<int>();
        var onlyInTms = new List<Item>();
        var differences = new List<Item>();
        var matched = 0;
        foreach (var item in local)
        {
            var candidates = remote.Select((value, index) => (value, index)).Where(pair => !usedRemote.Contains(pair.index) && matches(item, pair.value)).ToList();
            if (candidates.Count != 1)
            {
                onlyInTms.Add(new Item(localName(item), candidates.Count > 1 ? $"{candidates.Count} possible matches in Samsara" : null));
                continue;
            }
            usedRemote.Add(candidates[0].index);
            matched++;
            link(item, candidates[0].value);
            if (difference(item, candidates[0].value) is { } note) differences.Add(new Item(localName(item), note));
        }
        var onlyInSamsara = remote.Select((value, index) => (value, index)).Where(pair => !usedRemote.Contains(pair.index))
            .Select(pair => new Item(remoteName(pair.value))).OrderBy(item => item.Name).ToList();
        return new Section(local.Count, remote.Count, matched, onlyInTms.OrderBy(item => item.Name).ToList(), onlyInSamsara, differences);
    }

    internal static bool DriverMatches(Driver driver, SamsaraDriver remote)
    {
        var employee = Norm(driver.EmployeeNumber);
        if (employee.Length > 0 && remote.ExternalIds.Values.Any(value => Norm(value) == employee)) return true;
        var card = Norm(driver.TachoCardNumber);
        if (card.Length >= 8 && Norm(remote.LicenseNumber).Length >= 8 && (Norm(remote.LicenseNumber).Contains(card) || card.Contains(Norm(remote.LicenseNumber)))) return true;
        return SameName(driver.DisplayName, remote.Name);
    }

    /// <summary>First name and surname both present, in either order ("Smith Adam" = "Adam Douglas Smith").</summary>
    internal static bool SameName(string? a, string? b)
    {
        var left = Tokens(a);
        var right = Tokens(b);
        if (left.Count < 2 || right.Count < 2) return Norm(a) == Norm(b) && Norm(a).Length > 0;
        var (shorter, longer) = left.Count <= right.Count ? (left, right) : (right, left);
        return longer.Contains(shorter[0]) && longer.Contains(shorter[^1]);
    }

    internal static bool VehicleMatches(string registration, string? vin, string? plate, string? name, string? remoteVin)
    {
        var reg = Norm(registration);
        return (reg.Length > 0 && (Norm(plate) == reg || Norm(name) == reg || Norm(name).StartsWith(reg, StringComparison.Ordinal))) ||
               (Norm(vin).Length > 0 && Norm(vin) == Norm(remoteVin));
    }

    private static async Task SaveLinksAsync(TmsDbContext db, List<(string Type, Guid Id, string RemoteId, string Label)> links, string actor, CancellationToken ct)
    {
        if (links.Count == 0) return;
        var types = links.Select(link => link.Type).Distinct().ToList();
        var existing = await db.IntegrationMappings
            .Where(item => item.Provider == "Samsara" && item.Active && types.Contains(item.TmsEntityType))
            .ToListAsync(ct);
        foreach (var link in links)
        {
            var mapping = existing.FirstOrDefault(item => item.TmsEntityType == link.Type && item.TmsEntityId == link.Id);
            if (mapping is null)
                db.IntegrationMappings.Add(new IntegrationMapping
                {
                    Provider = "Samsara", ExternalKey = link.RemoteId, ExternalLabel = link.Label, TmsEntityType = link.Type, TmsEntityId = link.Id,
                    Active = true, MappingKind = "ProviderIdentity", NormalizedExternalValue = Norm(link.RemoteId),
                    Notes = "Linked by the TMS / Samsara check.", UpdatedBy = actor
                });
            else if (mapping.ExternalKey != link.RemoteId)
            {
                mapping.ExternalKey = link.RemoteId;
                mapping.ExternalLabel = link.Label;
                mapping.NormalizedExternalValue = Norm(link.RemoteId);
                mapping.UpdatedBy = actor;
                mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            }
        }
        await db.SaveChangesAsync(ct);
    }

    public static async Task<Result?> ReadAsync(TmsDbContext db, CancellationToken ct)
    {
        var json = await db.StagedImports.AsNoTracking().Where(item => item.EntityType == SnapshotType && item.IdempotencyKey == SnapshotKey).Select(item => item.PayloadJson).FirstOrDefaultAsync(ct);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<Result>(json, Json); } catch (JsonException) { return null; }
    }

    private static async Task<Result> StoreAsync(TmsDbContext db, Result result, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(result, Json);
        var row = await db.StagedImports.FirstOrDefaultAsync(item => item.EntityType == SnapshotType && item.IdempotencyKey == SnapshotKey, ct);
        if (row is null)
            db.StagedImports.Add(new StagedImport { EntityType = SnapshotType, IdempotencyKey = SnapshotKey, PayloadJson = payload, Status = StagingStatus.Archived, Source = "TMS / Samsara check" });
        else { row.PayloadJson = payload; row.ReceivedAtUtc = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        return result;
    }

    private static string Norm(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static List<string> Tokens(string? value) => Regex.Split((value ?? string.Empty).ToLowerInvariant(), @"[^a-z']+").Where(token => token.Length >= 2).ToList();
}

/// <summary>Runs the TMS / Samsara check every six hours.</summary>
public sealed class SamsaraReconciliationService(IServiceScopeFactory scopes, ILogger<SamsaraReconciliationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartupGrace.WaitAsync(stoppingToken, TimeSpan.FromMinutes(3));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var samsara = scope.ServiceProvider.GetRequiredService<SamsaraClient>();
                if (samsara.IsConfigured)
                {
                    var result = await SamsaraReconciliation.RunAsync(scope.ServiceProvider.GetRequiredService<TmsDbContext>(), samsara, "system:samsara-check", stoppingToken);
                    logger.LogInformation("TMS / Samsara check: {Gaps} gap(s).", result.Gaps);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "TMS / Samsara check failed; it will run again later.");
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
