using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps Driver Master to people who are actually driving. A driver with no tachograph
/// activity (no duty under their TachoMaster member or card) for more than 28 days is
/// archived; when tacho data for them appears again they come back on their own.
/// Never touches anyone on a run in the last 28 days or ahead, or anyone added or changed by
/// hand in the last 28 days, and stops if the tacho feed looks incomplete.
/// </summary>
public static class DriverTachoActivity
{
    public const int Days = 28;
    public const string ArchiveAction = "ArchivedNoTachoActivity";
    public const string RestoreAction = "RestoredTachoActivity";
    /// <summary>Written on each duplicate when a planner merges drivers; the sync never revives these.</summary>
    public const string MergedAction = "DuplicateMergedInto";
    private static readonly string[] HandEditedActions = ["Create", "Add", "Roster", "Promot", "Manual", "Edit", "Update", "Merge"];

    public sealed record Outcome(int Archived, int Restored, string Message);

    /// <summary>The last duty start for this driver's member code(s) or card, if any in the window.</summary>
    public static DateTimeOffset? LastActivity(Driver driver, TachoRecentActivity activity)
    {
        DateTimeOffset? last = null;
        foreach (var member in MemberCodes(driver, activity))
            if (activity.LastDutyByMember.TryGetValue(member, out var at) && (last is null || at > last)) last = at;
        return last;
    }

    public static IEnumerable<int> MemberCodes(Driver driver, TachoRecentActivity activity)
    {
        var codes = new HashSet<int>();
        if (int.TryParse(TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId), out var own) && own > 0) codes.Add(own);
        if (!string.IsNullOrWhiteSpace(driver.TachoCardNumber))
            foreach (var (member, card) in activity.MemberCards)
                if (TachoDriverIdentityRules.CardsMatch(driver.TachoCardNumber, card)) codes.Add(member);
        return codes;
    }

    /// <summary>True when the snapshot is recent, covers the full 28 days and has real activity in it.</summary>
    public static bool Trustworthy(TachoRecentActivity? activity, DateTimeOffset now, out string reason)
    {
        reason = string.Empty;
        if (activity is null) { reason = "No TachoMaster snapshot is loaded."; return false; }
        var today = TachoDriverCardReadEligibility.UkToday(now);
        if (now - activity.RefreshedAtUtc > TimeSpan.FromHours(2)) { reason = "The TachoMaster snapshot is more than two hours old."; return false; }
        if (activity.FromDate > today.AddDays(-(Days - 1))) { reason = "The TachoMaster snapshot doesn't cover the last 28 days."; return false; }
        var recentMembers = activity.LastDutyByMember.Count(item => item.Value >= now.AddDays(-3));
        if (activity.LastDutyByMember.Count < 15 || recentMembers < 5) { reason = "The TachoMaster snapshot has too little driving in it to be trusted."; return false; }
        return true;
    }

    public static async Task<Outcome> ApplyAsync(TmsDbContext db, TachoRecentActivity? activity, string actor, DateTimeOffset now, CancellationToken ct)
    {
        if (!Trustworthy(activity, now, out var reason)) return new(0, 0, $"Driver activity check skipped: {reason}");
        var today = TachoDriverCardReadEligibility.UkToday(now);
        var cutoff = now.AddDays(-Days);

        var drivers = await db.Drivers.ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var onRecentRuns = (await db.Loads.AsNoTracking()
            .Where(load => load.DriverId != null && load.PlanningDate >= today.AddDays(-Days) && load.Status != LoadStatus.Cancelled)
            .Select(load => load.DriverId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var recentAudits = (await db.MasterDataAudits.AsNoTracking()
            .Where(audit => audit.EntityType == "Driver")
            .Select(audit => new { audit.EntityId, audit.Action, audit.ChangedBy, audit.ChangedAtUtc })
            .ToListAsync(ct))
            .Where(audit => audit.ChangedAtUtc >= cutoff)
            .ToList();
        var handEdited = recentAudits
            .Where(audit => audit.Action != ArchiveAction && audit.Action != RestoreAction)
            .Where(audit => HandEditedActions.Any(word => audit.Action.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .Where(audit => audit.ChangedBy is { } by && by.Contains('@'))
            .Select(audit => audit.EntityId).ToHashSet();

        // Archive: active, no duty in the window, not on a recent/future run, not just added by hand.
        var toArchive = drivers
            .Where(driver => driver.Active)
            .Where(driver => !onRecentRuns.Contains(driver.Id) && !handEdited.Contains(driver.Id))
            .Where(driver => LastActivity(driver, activity!) is not { } last || last < cutoff)
            .ToList();
        var activeCount = drivers.Count(driver => driver.Active);
        if (activeCount > 0 && toArchive.Count > Math.Max(10, activeCount * 0.4))
            return new(0, 0, $"Driver activity check stopped: it would archive {toArchive.Count} of {activeCount} drivers, which looks like a feed problem. Nothing was changed.");

        foreach (var driver in toArchive)
        {
            driver.Active = false;
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Driver",
                EntityId = driver.Id,
                Action = ArchiveAction,
                ChangedBy = actor,
                ChangesJson = JsonSerializer.Serialize(new { reason = $"No tachograph activity for more than {Days} days", driver.DisplayName, driver.EmployeeNumber, driver.TachoMasterDriverId, driver.TachoCardNumber })
            });
        }

        // Restore: archived by this rule, driving again in the last week, and nobody active holds the identity.
        var archivedByRule = (await db.MasterDataAudits.AsNoTracking()
            .Where(audit => audit.EntityType == "Driver" && (audit.Action == ArchiveAction || audit.Action == RestoreAction))
            .Select(audit => new { audit.EntityId, audit.Action, audit.ChangedAtUtc })
            .ToListAsync(ct))
            .GroupBy(audit => audit.EntityId)
            .Where(group => group.OrderByDescending(audit => audit.ChangedAtUtc).First().Action == ArchiveAction)
            .Select(group => group.Key).ToHashSet();
        var restored = 0;
        foreach (var driver in drivers.Where(driver => !driver.Active && archivedByRule.Contains(driver.Id) && !toArchive.Contains(driver)))
        {
            if (LastActivity(driver, activity!) is not { } last || last < now.AddDays(-7)) continue;
            var member = TachoDriverIdentityRules.NormaliseIdentifier(driver.TachoMasterDriverId);
            if (member.Length > 0 && drivers.Any(other => other.Active && other.Id != driver.Id && TachoDriverIdentityRules.MemberMatches(other.TachoMasterDriverId, member))) continue;
            driver.Active = true;
            restored++;
            db.MasterDataAudits.Add(new MasterDataAudit
            {
                EntityType = "Driver",
                EntityId = driver.Id,
                Action = RestoreAction,
                ChangedBy = actor,
                ChangesJson = JsonSerializer.Serialize(new { reason = "Tachograph activity resumed", lastDutyUtc = last, driver.DisplayName })
            });
        }

        await db.SaveChangesAsync(ct);
        return new(toArchive.Count, restored, $"Driver activity check: {toArchive.Count} archived (no tacho activity for over {Days} days), {restored} restored.");
    }

    /// <summary>
    /// Driver ids the Tacho sync must not switch back on: archived by this rule and not restored
    /// since, or merged into another driver by a planner.
    /// </summary>
    public static async Task<HashSet<Guid>> HeldArchivedAsync(TmsDbContext db, CancellationToken ct) =>
        (await db.MasterDataAudits.AsNoTracking()
            .Where(audit => audit.EntityType == "Driver" && (audit.Action == ArchiveAction || audit.Action == RestoreAction || audit.Action == MergedAction))
            .Select(audit => new { audit.EntityId, audit.Action, audit.ChangedAtUtc })
            .ToListAsync(ct))
        .GroupBy(audit => audit.EntityId)
        .Where(group => group.OrderByDescending(audit => audit.ChangedAtUtc).First().Action is ArchiveAction or MergedAction)
        .Select(group => group.Key).ToHashSet();
}
