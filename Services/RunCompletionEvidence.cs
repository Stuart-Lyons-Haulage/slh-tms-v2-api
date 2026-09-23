using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

internal static class RunCompletionEvidence
{
    public static bool CanAutoComplete(Load load, IReadOnlySet<Guid> confirmedDepartedStopIds,
        ILogger? logger = null)
    {
        if (load.Status != LoadStatus.InProgress) return false;
        var plannedStops = (load.Stops ?? []).OrderBy(stop => stop.Sequence).ToList();
        if (plannedStops.Count == 0) return false;
        var emptyIdStops = plannedStops.Where(stop => stop.Id == Guid.Empty).ToList();
        if (emptyIdStops.Count > 0)
        {
            logger?.LogWarning(
                "Run {LoadReference} cannot auto-complete: {EmptyIdCount} stop(s) have an empty GUID identity. " +
                "This usually indicates an import that did not assign stop IDs. Investigate the source import.",
                load.Reference, emptyIdStops.Count);
            return false;
        }
        return plannedStops.All(stop => confirmedDepartedStopIds.Contains(stop.Id));
    }
}
