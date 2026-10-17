using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Evaluates the 6 x 24-hour weekly-rest deadline from TachoMaster duty history.
/// Rest gaps are classified as reduced (at least 24 hours) or regular (at least 45 hours).
/// Reduced-rest compensation is exposed as evidence, but is not used as a hard dispatch block until
/// the complete multi-week compensation pattern can be proven from the returned history.
/// </summary>
public sealed class DriverWeeklyRestComplianceService(TachoMasterClient tachoMaster, ILogger<DriverWeeklyRestComplianceService> logger)
{
    private static readonly TimeSpan ReducedWeeklyRest = TimeSpan.FromHours(24);
    private static readonly TimeSpan RegularWeeklyRest = TimeSpan.FromHours(45);
    private static readonly TimeSpan WeeklyRestDeadline = TimeSpan.FromHours(144);
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public bool IsConfigured => tachoMaster.IsConfigured;

    public async Task<WeeklyRestComplianceResult> EvaluateAsync(
        Driver driver,
        DateOnly planningDate,
        DateTimeOffset referenceUtc,
        CancellationToken ct)
    {
        if (!tachoMaster.IsConfigured)
            return WeeklyRestComplianceResult.Unknown("TachoMaster is not configured; weekly-rest availability could not be independently verified.");

        if (!DriverDayCycleCalculator.HasBoundTachoIdentity(driver))
            return WeeklyRestComplianceResult.Unknown("The driver is not bound to a TachoMaster identity.");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var throughDate = planningDate;
            // Four weeks gives enough context to classify regular/reduced weekly rests and expose
            // outstanding reduced-rest compensation without pretending that a short snapshot is a full legal history.
            var fromDate = throughDate.AddDays(-28);
            var duties = new List<TachoDriverDutyStatus>();
            for (var day = fromDate; day <= throughDate; day = day.AddDays(1))
                duties.AddRange(await tachoMaster.GetDriverDutyStatusesAsync(day, timeout.Token));

            return Evaluate(driver, referenceUtc, duties);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("TachoMaster exceeded the weekly-rest compliance budget for driver {DriverId}.", driver.Id);
            return WeeklyRestComplianceResult.Unverified("TachoMaster weekly-rest history timed out. The driver remains available for planning; review TachoMaster before final dispatch.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster weekly-rest history was unavailable for driver {DriverId}.", driver.Id);
            return WeeklyRestComplianceResult.Unverified("TachoMaster weekly-rest history is unavailable. The driver remains available for planning; review TachoMaster before final dispatch.");
        }
    }

    public static WeeklyRestComplianceResult Evaluate(
        Driver driver,
        DateTimeOffset referenceUtc,
        IEnumerable<TachoDriverDutyStatus> source)
    {
        var duties = source
            .Where(item => DriverDayCycleCalculator.MatchesDriver(driver, item))
            .Where(item => item.DutyStartUtc != default)
            .GroupBy(item => new DutyKey(item.MemberCode, item.DutyStartUtc, item.DutyEndUtc, item.VehicleCode))
            .Select(group => group.First())
            .OrderBy(item => item.DutyStartUtc)
            .ToList();

        if (duties.Count == 0)
            return WeeklyRestComplianceResult.Unknown("No recent TachoMaster duty history was returned for this driver.");

        var blocks = MergeDuties(duties, referenceUtc);
        if (blocks.Count == 0)
            return WeeklyRestComplianceResult.Unknown("No usable TachoMaster duty blocks were returned for this driver.");

        var restGaps = new List<WeeklyRestGap>();
        for (var index = 1; index < blocks.Count; index++)
        {
            var previous = blocks[index - 1];
            var current = blocks[index];
            if (previous.EndUtc is not DateTimeOffset previousEnd) continue;
            var gap = current.StartUtc - previousEnd;
            if (gap >= ReducedWeeklyRest)
                restGaps.Add(CreateRestGap(previousEnd, current.StartUtc));
        }

        // Driver Dispatch can evaluate a driver before the next duty has started. If the most recent
        // completed duty ended at least 24 hours before the planning/reference time, that trailing gap
        // is a completed weekly rest for the prospective duty starting at the reference time. Counting
        // it here keeps weekly-rest availability consistent with DriverDayCycleCalculator's Day 1 reset.
        var lastBlock = blocks[^1];
        if (lastBlock.EndUtc is DateTimeOffset lastDutyEnd &&
            lastDutyEnd < referenceUtc &&
            referenceUtc - lastDutyEnd >= ReducedWeeklyRest)
        {
            restGaps.Add(CreateRestGap(lastDutyEnd, referenceUtc));
        }

        // Without at least one observed qualifying weekly rest we do not know where the current
        // 144-hour clock actually started. Do not manufacture an overdue decision from the first duty
        // in a truncated history window.
        var completedRests = restGaps
            .Where(gap => gap.EndUtc <= referenceUtc)
            .OrderBy(gap => gap.EndUtc)
            .ToList();
        if (completedRests.Count == 0)
        {
            return WeeklyRestComplianceResult.Unverified(
                "No completed 24-hour weekly rest is visible in the returned TachoMaster history, so the start of the current 6 x 24-hour window cannot be proven. Do not mark this driver unavailable from this evidence alone.");
        }

        DateTimeOffset? missedDeadlineUtc = null;
        WeeklyRestGap? lastRest = null;
        foreach (var gap in completedRests)
        {
            if (lastRest is not null)
            {
                var deadline = lastRest.EndUtc + WeeklyRestDeadline;
                if (gap.StartUtc > deadline)
                {
                    missedDeadlineUtc = deadline;
                    break;
                }
            }
            lastRest = gap;
        }

        if (lastRest is null)
            return WeeklyRestComplianceResult.Unverified("Weekly-rest history could not be established from TachoMaster.");

        var evidence = BuildEvidence(lastRest, completedRests);

        if (missedDeadlineUtc is not null && referenceUtc >= missedDeadlineUtc.Value)
        {
            return WeeklyRestComplianceResult.Overdue(
                missedDeadlineUtc.Value,
                lastRest.EndUtc,
                $"A later weekly rest started after the legal 144-hour deadline. {evidence}",
                lastRest.Type,
                lastRest.Duration.TotalHours,
                OutstandingCompensationHours(completedRests));
        }

        var deadlineAfterLastRest = lastRest.EndUtc + WeeklyRestDeadline;
        if (referenceUtc >= deadlineAfterLastRest)
        {
            var elapsedHours = Math.Max(0, (referenceUtc - lastRest.EndUtc).TotalHours);
            return WeeklyRestComplianceResult.Overdue(
                deadlineAfterLastRest,
                lastRest.EndUtc,
                $"Weekly rest is due. {elapsedHours:0.#} hours have elapsed since the end of the last qualifying weekly rest. {evidence}",
                lastRest.Type,
                lastRest.Duration.TotalHours,
                OutstandingCompensationHours(completedRests));
        }

        var remaining = deadlineAfterLastRest - referenceUtc;
        if (remaining <= TimeSpan.FromHours(12))
        {
            return WeeklyRestComplianceResult.DueSoon(
                deadlineAfterLastRest,
                lastRest.EndUtc,
                $"Weekly rest is due by {LocalTime(deadlineAfterLastRest)}. Only {remaining.TotalHours:0.#} hours remain. {evidence}",
                lastRest.Type,
                lastRest.Duration.TotalHours,
                OutstandingCompensationHours(completedRests));
        }

        return WeeklyRestComplianceResult.Ready(
            deadlineAfterLastRest,
            lastRest.EndUtc,
            $"Weekly-rest window is open until {LocalTime(deadlineAfterLastRest)}. {evidence}",
            lastRest.Type,
            lastRest.Duration.TotalHours,
            OutstandingCompensationHours(completedRests));
    }

    private static WeeklyRestGap CreateRestGap(DateTimeOffset start, DateTimeOffset end)
    {
        var duration = end - start;
        var type = duration >= RegularWeeklyRest ? "Regular45" : "Reduced24";
        return new WeeklyRestGap(start, end, duration, type);
    }

    private static string BuildEvidence(WeeklyRestGap lastRest, IReadOnlyCollection<WeeklyRestGap> completedRests)
    {
        var compensation = OutstandingCompensationHours(completedRests);
        var restLabel = lastRest.Type == "Regular45" ? "regular 45h+" : "reduced 24–45h";
        var compensationText = compensation > 0
            ? $"Reduced-rest compensation visible in this history: {compensation:0.#}h (informational; not used as a hard block)."
            : "No reduced-rest compensation is visible in this history.";
        return $"Last weekly rest: {restLabel}, {lastRest.Duration.TotalHours:0.#}h, ending {LocalTime(lastRest.EndUtc)}. {compensationText}";
    }

    private static double OutstandingCompensationHours(IEnumerable<WeeklyRestGap> rests)
        => rests
            .Where(rest => rest.Type == "Reduced24")
            .Sum(rest => Math.Max(0, RegularWeeklyRest.TotalHours - rest.Duration.TotalHours));

    private static List<DutyBlock> MergeDuties(IReadOnlyList<TachoDriverDutyStatus> duties, DateTimeOffset referenceUtc)
    {
        var blocks = new List<DutyBlock>();
        foreach (var duty in duties.OrderBy(item => item.DutyStartUtc))
        {
            var end = duty.DutyEndUtc;
            if (end is DateTimeOffset dutyEnd && dutyEnd < duty.DutyStartUtc)
                end = duty.DutyStartUtc;

            if (blocks.Count == 0)
            {
                blocks.Add(new DutyBlock(duty.DutyStartUtc, end));
                continue;
            }

            var previous = blocks[^1];
            var previousEffectiveEnd = previous.EndUtc ?? referenceUtc;
            if (duty.DutyStartUtc <= previousEffectiveEnd)
            {
                DateTimeOffset? mergedEnd;
                if (previous.EndUtc is null || end is null) mergedEnd = null;
                else mergedEnd = previous.EndUtc >= end ? previous.EndUtc : end;
                blocks[^1] = new DutyBlock(previous.StartUtc, mergedEnd);
                continue;
            }

            blocks.Add(new DutyBlock(duty.DutyStartUtc, end));
        }
        return blocks;
    }

    private static string LocalTime(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, London).ToString("dd/MM HH:mm");

    private sealed record DutyKey(int MemberCode, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string VehicleCode);
    private sealed record DutyBlock(DateTimeOffset StartUtc, DateTimeOffset? EndUtc);
    private sealed record WeeklyRestGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc, TimeSpan Duration, string Type);
}

public sealed record WeeklyRestComplianceResult(
    string Status,
    string Message,
    DateTimeOffset? WeeklyRestDueUtc,
    DateTimeOffset? LastWeeklyRestEndUtc,
    string? LastWeeklyRestType = null,
    double? LastWeeklyRestHours = null,
    double? ReducedRestCompensationHours = null)
{
    // Only proven overdue evidence is a hard legal gate. Unknown/unverified history must not
    // incorrectly make a driver unavailable; it remains a visible review warning.
    public bool IsBlocked => Status == "Overdue";

    public static WeeklyRestComplianceResult Ready(DateTimeOffset? due, DateTimeOffset? lastEnd, string message, string? restType = null, double? restHours = null, double? compensationHours = null)
        => new("Ready", message, due, lastEnd, restType, restHours, compensationHours);

    public static WeeklyRestComplianceResult DueSoon(DateTimeOffset due, DateTimeOffset? lastEnd, string message, string? restType = null, double? restHours = null, double? compensationHours = null)
        => new("DueSoon", message, due, lastEnd, restType, restHours, compensationHours);

    public static WeeklyRestComplianceResult Overdue(DateTimeOffset due, DateTimeOffset? lastEnd, string message, string? restType = null, double? restHours = null, double? compensationHours = null)
        => new("Overdue", message, due, lastEnd, restType, restHours, compensationHours);

    public static WeeklyRestComplianceResult Unverified(string message)
        => new("Unverified", message, null, null);

    public static WeeklyRestComplianceResult Unknown(string message)
        => new("Unknown", message, null, null);
}