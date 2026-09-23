using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Calculates the driver's current/planned duty-day number from TachoMaster duty history.
/// Day numbers count actual duty periods in the current cycle rather than elapsed 24-hour
/// clock periods, so changing sign-on times cannot incorrectly reset a driver to Day 1.
/// A gap of at least 24 continuous hours is retained as the existing reduced weekly-rest
/// cycle reset threshold for this planning indicator; weekly-rest compliance remains the
/// authoritative legal check elsewhere.
/// </summary>
public static class DriverDayCycleCalculator
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeSpan WeeklyRestReset = TimeSpan.FromHours(24);

    public static int Calculate(DateOnly planningDate, IEnumerable<TachoDriverDutyStatus> source)
    {
        var duties = source
            .Where(item => item.DutyStartUtc != default)
            .GroupBy(item => new DutyKey(item.MemberCode, item.DutyStartUtc, item.DutyEndUtc, item.VehicleCode))
            .Select(group => group.First())
            .OrderBy(item => item.DutyStartUtc)
            .ToList();

        if (duties.Count == 0) return 1;

        var referenceUtc = PlanningReferenceUtc(planningDate, duties);
        var relevant = duties.Where(item => item.DutyStartUtc <= referenceUtc).ToList();
        if (relevant.Count == 0) return 1;

        var blocks = MergeOverlappingDuties(relevant, referenceUtc);
        if (blocks.Count == 0) return 1;

        // Find the first duty block in the current cycle. This deliberately counts real duty
        // periods rather than dividing wall-clock time since cycle start by 24 hours. A driver
        // who worked Monday and Tuesday therefore projects to Day 3 on Wednesday even if the
        // sign-on time has moved between those days.
        var cycleStartIndex = 0;
        for (var index = 1; index < blocks.Count; index++)
        {
            var previous = blocks[index - 1];
            var current = blocks[index];
            if (previous.EndUtc is DateTimeOffset previousEnd && current.StartUtc - previousEnd >= WeeklyRestReset)
                cycleStartIndex = index;
        }

        var currentCycle = blocks.Skip(cycleStartIndex).ToList();
        if (currentCycle.Count == 0) return 1;

        // If no new duty has started and the driver has already accumulated a weekly-rest-sized
        // gap before the prospective planning duty, the next duty is Day 1.
        var last = currentCycle[^1];
        if (last.EndUtc is DateTimeOffset lastEnd &&
            lastEnd < referenceUtc &&
            referenceUtc - lastEnd >= WeeklyRestReset)
        {
            return 1;
        }

        var hasDutyOnPlanningDate = currentCycle.Any(block => LondonDate(block.StartUtc) == planningDate);
        var projectedPeriods = currentCycle.Count + (hasDutyOnPlanningDate ? 0 : 1);

        // Day 7 is intentionally retained as an exception signal rather than wrapping to Day 1.
        // A compliant weekly rest should have reset the cycle before this point.
        return Math.Clamp(projectedPeriods, 1, 7);
    }

    public static bool MatchesDriver(Driver driver, TachoDriverDutyStatus duty)
    {
        if (int.TryParse(driver.TachoMasterDriverId, out var memberCode) && memberCode == duty.MemberCode)
            return true;

        var driverCard = Normalise(driver.TachoCardNumber);
        var dutyCard = Normalise(duty.CardNumber);
        if (driverCard.Length >= 8 && dutyCard.Length >= 8 &&
            (driverCard == dutyCard || driverCard.EndsWith(dutyCard, StringComparison.OrdinalIgnoreCase) || dutyCard.EndsWith(driverCard, StringComparison.OrdinalIgnoreCase)))
            return true;

        var employee = Normalise(driver.EmployeeNumber);
        var dutyEmployee = Normalise(duty.EmployeeNumber);
        if (employee.Length > 0 && dutyEmployee.Length > 0 && employee == dutyEmployee)
            return true;

        var name = Normalise(driver.TachoName ?? driver.DisplayName);
        var dutyName = Normalise(duty.DriverName);
        return name.Length > 0 && dutyName.Length > 0 && name == dutyName;
    }

    public static bool HasBoundTachoIdentity(Driver driver) =>
        !string.IsNullOrWhiteSpace(driver.TachoMasterDriverId) ||
        !string.IsNullOrWhiteSpace(driver.TachoCardNumber) ||
        !string.IsNullOrWhiteSpace(driver.TachoName) ||
        driver.LastTachoSyncUtc is not null;

    private static DateTimeOffset PlanningReferenceUtc(DateOnly planningDate, IReadOnlyList<TachoDriverDutyStatus> duties)
    {
        var dutyOnPlanningDate = duties
            .Where(item => LondonDate(item.DutyStartUtc) == planningDate)
            .OrderBy(item => item.DutyStartUtc)
            .FirstOrDefault();
        if (dutyOnPlanningDate is not null) return dutyOnPlanningDate.DutyStartUtc;

        // With no duty yet, project the driver's own most recent sign-on time onto the planning
        // date. This is only used to decide whether a qualifying rest gap exists; the Day number
        // itself is counted from actual duty periods.
        var previousDuty = duties
            .Where(item => LondonDate(item.DutyStartUtc) < planningDate)
            .OrderByDescending(item => item.DutyStartUtc)
            .FirstOrDefault();
        var localTime = previousDuty is null
            ? TimeOnly.MinValue
            : TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(previousDuty.DutyStartUtc, London).DateTime);

        var local = DateTime.SpecifyKind(planningDate.ToDateTime(localTime), DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, London), TimeSpan.Zero);
    }

    private static List<DutyBlock> MergeOverlappingDuties(IReadOnlyList<TachoDriverDutyStatus> duties, DateTimeOffset referenceUtc)
    {
        var blocks = new List<DutyBlock>();
        foreach (var duty in duties.OrderBy(item => item.DutyStartUtc))
        {
            var end = duty.DutyEndUtc;
            var effectiveEnd = end ?? referenceUtc;
            if (effectiveEnd < duty.DutyStartUtc) effectiveEnd = duty.DutyStartUtc;

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

    private static DateOnly LondonDate(DateTimeOffset value) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, London).DateTime);

    private static string Normalise(string? value) =>
        new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private sealed record DutyKey(int MemberCode, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string VehicleCode);
    private sealed record DutyBlock(DateTimeOffset StartUtc, DateTimeOffset? EndUtc);
}
