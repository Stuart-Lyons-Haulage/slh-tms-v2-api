using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Tracking;

namespace Slh.Tms.Api.Services;

public sealed record DriverDispatchVisibilityItem(
    Guid DriverId,
    string EmploymentType,
    string? Skills,
    string? Coding,
    DateOnly? LastTachoRead,
    DateTimeOffset? LastLiveActivity,
    DateOnly? LastExecutedRun,
    bool CurrentlyAllocated,
    bool RosteredAgency,
    bool Subcontractor,
    string Evidence);

public sealed record DriverDispatchVisibilitySnapshot(
    DateOnly PlanningDate,
    int WindowDays,
    DateOnly CutoffDate,
    IReadOnlyList<DriverDispatchVisibilityItem> Drivers);

/// <summary>
/// Builds one canonical Dispatch population from persisted TachoMaster worker profiles, live
/// tracking identity and TMS executed-run history. No 28-day TachoMaster network sweep is needed.
/// </summary>
public static class DriverDispatchVisibilityStore
{
    private const string TachoProfileType = "tachodriverprofile";
    private static readonly LoadStatus[] ExecutedStatuses = [LoadStatus.Dispatched, LoadStatus.InProgress, LoadStatus.Completed];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static async Task<DriverDispatchVisibilitySnapshot> ReadAsync(
        TmsDbContext db,
        DateOnly planningDate,
        ILogger logger,
        CancellationToken ct)
    {
        var cutoff = planningDate.AddDays(-DriverDispatchVisibilityRules.RecentWindowDays);
        var drivers = await db.Drivers.AsNoTracking()
            .Where(driver => driver.Active)
            .OrderBy(driver => driver.DisplayName)
            .ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);
        drivers = drivers.Where(driver => DriverPopulationRules.IsDriver(driver) || DriverPopulationRules.IsSubcontractor(driver)).ToList();

        var currentDriverIds = await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null && load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
            .Select(load => load.DriverId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var current = currentDriverIds.ToHashSet();

        var recentRuns = await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null &&
                           load.PlanningDate >= cutoff && load.PlanningDate < planningDate &&
                           ExecutedStatuses.Contains(load.Status))
            .Select(load => new { DriverId = load.DriverId!.Value, load.PlanningDate })
            .ToListAsync(ct);
        var lastRun = recentRuns
            .GroupBy(item => item.DriverId)
            .ToDictionary(group => group.Key, group => group.Max(item => item.PlanningDate));

        var roster = await DriverDispatchAgencyRosterStore.ReadForDateAsync(db, planningDate, ct);
        var profiles = await ReadProfilesAsync(db, logger, ct);
        var liveStatuses = await ReadLiveStatusesAsync(db, logger, ct);

        var visible = new List<DriverDispatchVisibilityItem>();
        foreach (var driver in drivers)
        {
            var profile = MatchProfile(driver, profiles);
            var lastTachoRead = ParseDate(profile?.CardLastRead);
            var live = MatchLive(driver, liveStatuses);
            var lastLiveActivity = live?.LastEventTimeUtc;
            lastRun.TryGetValue(driver.Id, out var lastExecutedRun);

            var allocated = current.Contains(driver.Id);
            var rosteredAgency = roster.ContainsKey(driver.Id);
            var subcontractor = DriverPopulationRules.IsSubcontractor(driver);
            if (!DriverDispatchVisibilityRules.IsVisible(
                    planningDate,
                    lastTachoRead,
                    lastLiveActivity,
                    lastExecutedRun == default ? null : lastExecutedRun,
                    allocated,
                    rosteredAgency,
                    subcontractor))
                continue;

            var evidence = allocated ? "Allocated today"
                : rosteredAgency ? "Rostered agency"
                : subcontractor ? "Subcontractor"
                : lastTachoRead is DateOnly tacho && tacho >= cutoff ? $"Tacho read {tacho:dd/MM/yyyy}"
                : lastLiveActivity is DateTimeOffset tracked && DateOnly.FromDateTime(tracked.UtcDateTime) >= cutoff ? "Live tracking"
                : lastExecutedRun != default ? $"Live run {lastExecutedRun:dd/MM/yyyy}"
                : "Recent operational evidence";

            visible.Add(new DriverDispatchVisibilityItem(
                driver.Id,
                EmploymentType(driver),
                Clean(driver.Skills),
                Clean(driver.Coding),
                lastTachoRead,
                lastLiveActivity,
                lastExecutedRun == default ? null : lastExecutedRun,
                allocated,
                rosteredAgency,
                subcontractor,
                evidence));
        }

        return new DriverDispatchVisibilitySnapshot(
            planningDate,
            DriverDispatchVisibilityRules.RecentWindowDays,
            cutoff,
            visible.OrderBy(item => EmploymentOrder(item.EmploymentType)).ThenBy(item => item.DriverId).ToList());
    }

    private static async Task<List<TachoLiveWorker>> ReadProfilesAsync(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        try
        {
            var rows = await db.StagedImports.AsNoTracking()
                .Where(row => row.EntityType == TachoProfileType && row.Status == StagingStatus.Promoted)
                .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
                .Take(5000)
                .ToListAsync(ct);
            var result = new List<TachoLiveWorker>();
            var seenMembers = new HashSet<int>();
            foreach (var row in rows)
            {
                try
                {
                    var profile = JsonSerializer.Deserialize<TachoLiveWorker>(row.PayloadJson, Json);
                    if (profile is null || profile.MemberCode <= 0 || !seenMembers.Add(profile.MemberCode)) continue;
                    result.Add(profile);
                }
                catch (JsonException) { }
            }
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Stored TachoMaster worker profiles were unavailable for Driver Dispatch visibility.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static async Task<List<VehicleLiveStatus>> ReadLiveStatusesAsync(TmsDbContext db, ILogger logger, CancellationToken ct)
    {
        try { return await db.VehicleLiveStatuses.AsNoTracking().ToListAsync(ct); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "Live tracking identity was unavailable for Driver Dispatch visibility.");
            db.ChangeTracker.Clear();
            return [];
        }
    }

    private static TachoLiveWorker? MatchProfile(Driver driver, IReadOnlyList<TachoLiveWorker> profiles)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var member) && member > 0)
        {
            var byMember = profiles.FirstOrDefault(profile => profile.MemberCode == member);
            if (byMember is not null) return byMember;
        }

        var byCard = profiles.Where(profile => TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, profile.CardNumber)).ToList();
        if (byCard.Count == 1) return byCard[0];

        var employee = Normalise(driver.EmployeeNumber);
        if (employee.Length > 0)
        {
            var byEmployee = profiles.Where(profile => Normalise(profile.EmployeeNumber) == employee).ToList();
            if (byEmployee.Count == 1) return byEmployee[0];
        }

        var names = new[] { driver.TachoName, driver.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(TachoDriverIdentityRules.NormalisePerson)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var byName = profiles.Where(profile => names.Contains(TachoDriverIdentityRules.NormalisePerson(profile.DisplayName))).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static VehicleLiveStatus? MatchLive(Driver driver, IEnumerable<VehicleLiveStatus> statuses)
    {
        var names = new[] { driver.TachoName, driver.DisplayName }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(TachoDriverIdentityRules.NormalisePerson)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return statuses
            .Where(status =>
                TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, status.CurrentDriverCardNumber) ||
                names.Contains(TachoDriverIdentityRules.NormalisePerson(status.CurrentDriverName)))
            .OrderByDescending(status => status.LastEventTimeUtc)
            .FirstOrDefault();
    }

    private static DateOnly? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateOnly.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var gb)) return gb;
        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var invariant)) return invariant;
        if (DateTimeOffset.TryParse(value, CultureInfo.GetCultureInfo("en-GB"), DateTimeStyles.AllowWhiteSpaces, out var timestamp))
            return DateOnly.FromDateTime(timestamp.UtcDateTime);
        return null;
    }

    private static string EmploymentType(Driver driver)
    {
        if (DriverPopulationRules.IsSubcontractor(driver)) return "Subcontractor";
        var token = Normalise($"{driver.DriverType} {driver.DriverGroup} {driver.AgencyName}");
        if (token.Contains("AGENCY", StringComparison.Ordinal)) return "Agency";
        if (token.Contains("CASUAL", StringComparison.Ordinal) || token.Contains("ZEROHOUR", StringComparison.Ordinal)) return "Casual";
        return "Employed";
    }

    private static int EmploymentOrder(string value) => value switch
    {
        "Employed" => 0,
        "Casual" => 1,
        "Agency" => 2,
        "Subcontractor" => 3,
        _ => 9
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}