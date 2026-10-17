using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public static class StagingAudit
{
    public static StagedImportEvent Create(
        StagedImport item,
        string eventType,
        StagingStatus? previousStatus = null,
        string? note = null,
        string? actor = null) => new()
    {
        StagedImportId = item.Id,
        EventType = eventType,
        PreviousStatus = previousStatus,
        NewStatus = item.Status,
        PayloadJson = item.PayloadJson,
        Note = note,
        Actor = actor
    };
}
