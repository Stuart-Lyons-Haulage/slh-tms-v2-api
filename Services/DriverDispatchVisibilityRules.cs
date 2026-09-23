namespace Slh.Tms.Api.Services;

/// <summary>
/// Keeps Dispatch focused on people who have actually been operationally relevant recently.
/// Current allocations, explicitly rostered agency cover and subcontractors remain visible even
/// when SLH does not own recent Tacho/live evidence for them.
/// </summary>
public static class DriverDispatchVisibilityRules
{
    public const int RecentWindowDays = 28;

    public static bool IsVisible(
        DateOnly planningDate,
        DateOnly? lastTachoRead,
        DateTimeOffset? lastLiveActivity,
        DateOnly? lastExecutedRun,
        bool currentlyAllocated,
        bool rosteredAgency,
        bool subcontractor)
    {
        if (currentlyAllocated || rosteredAgency || subcontractor) return true;

        var cutoff = planningDate.AddDays(-RecentWindowDays);
        var liveDate = lastLiveActivity is DateTimeOffset live
            ? DateOnly.FromDateTime(live.UtcDateTime)
            : (DateOnly?)null;

        return (lastTachoRead is DateOnly tacho && tacho >= cutoff && tacho <= planningDate) ||
               (liveDate is DateOnly liveDay && liveDay >= cutoff && liveDay <= planningDate) ||
               (lastExecutedRun is DateOnly runDay && runDay >= cutoff && runDay <= planningDate);
    }
}