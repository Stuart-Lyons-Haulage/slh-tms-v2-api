using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Models.Integrations;

namespace Slh.Tms.Api.Services;

public sealed record SamsaraStopProgressState(
    string? State,
    string? Operation,
    DateTimeOffset? OccurredAtUtc,
    DateTimeOffset? EnRouteTime,
    DateTimeOffset? ArrivalTime,
    DateTimeOffset? DepartureTime,
    DateTimeOffset? SkippedTime,
    DateTimeOffset? EstimatedArrivalTime,
    string? LiveSharingUrl);

public sealed record SamsaraRouteProgressCycle(
    bool Configured,
    int PagesRead,
    int EventsRead,
    int StopsUpdated,
    string? EndCursor);

/// <summary>
/// Consumes Samsara's append-only route audit feed and stores only the latest
/// execution snapshot against the existing LoadStop integration mapping.
/// Raw route-feed events are deliberately not retained in operational SQL.
/// </summary>
public sealed class SamsaraRouteProgressService(
    TmsDbContext db,
    SamsaraClient samsara,
    ILogger<SamsaraRouteProgressService> logger)
{
    private const string Provider = "Samsara";
    private const string CursorEntityType = "SyncCursor";
    private const string CursorMappingKind = "RouteAuditCursor";
    private static readonly Guid CursorEntityId = Guid.Parse("8c9480a7-17b7-4f2e-9231-89f48b4ef34a");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<SamsaraRouteProgressCycle> PollAsync(CancellationToken ct)
    {
        if (!samsara.IsConfigured)
            return new SamsaraRouteProgressCycle(false, 0, 0, 0, null);

        var cursorMapping = await db.IntegrationMappings
            .SingleOrDefaultAsync(item =>
                item.Active &&
                item.Provider == Provider &&
                item.TmsEntityType == CursorEntityType &&
                item.TmsEntityId == CursorEntityId, ct);

        var cursor = cursorMapping?.ExternalKey;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);
        var pagesRead = 0;
        var eventsRead = 0;
        var stopsUpdated = 0;

        for (var page = 0; page < 20; page++)
        {
            var feed = await samsara.GetRouteAuditFeedAsync(cursor, ct);
            pagesRead++;
            eventsRead += feed.Entries.Count;

            foreach (var entry in feed.Entries)
                stopsUpdated += await ApplyEntryAsync(entry, ct);

            if (!string.IsNullOrWhiteSpace(feed.EndCursor) &&
                !string.Equals(feed.EndCursor, cursor, StringComparison.Ordinal))
            {
                await SaveCursorAsync(feed.EndCursor, ct);
                cursor = feed.EndCursor;
            }

            if (!feed.HasNextPage ||
                string.IsNullOrWhiteSpace(feed.EndCursor) ||
                !seenCursors.Add(feed.EndCursor))
                break;
        }

        return new SamsaraRouteProgressCycle(true, pagesRead, eventsRead, stopsUpdated, cursor);
    }

    public static SamsaraStopProgressState? ReadProgress(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes) || !notes.TrimStart().StartsWith('{'))
            return null;

        try
        {
            return JsonSerializer.Deserialize<SamsaraStopProgressState>(notes, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<int> ApplyEntryAsync(SamsaraRouteAuditEntry entry, CancellationToken ct)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(entry.RawJson);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Ignoring an invalid Samsara route audit entry.");
            return 0;
        }

        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("route", out var route) || route.ValueKind != JsonValueKind.Object)
                return 0;

            var routeId = Text(route, "id");
            if (string.IsNullOrWhiteSpace(routeId))
                return 0;

            var knownLoad = await db.IntegrationMappings.AsNoTracking()
                .AnyAsync(item =>
                    item.Active &&
                    item.Provider == Provider &&
                    item.TmsEntityType == "Load" &&
                    item.ExternalKey == routeId, ct);

            if (!knownLoad)
                return 0;

            if (!root.TryGetProperty("changes", out var changes) ||
                changes.ValueKind != JsonValueKind.Object ||
                !changes.TryGetProperty("after", out var after) ||
                after.ValueKind != JsonValueKind.Object ||
                !after.TryGetProperty("stops", out var stops) ||
                stops.ValueKind != JsonValueKind.Array)
                return 0;

            var changedStops = stops.EnumerateArray()
                .Select(stop => new
                {
                    Id = Text(stop, "id"),
                    Element = stop.Clone()
                })
                .Where(stop => !string.IsNullOrWhiteSpace(stop.Id))
                .ToList();

            if (changedStops.Count == 0)
                return 0;

            var externalStopIds = changedStops.Select(stop => stop.Id!).Distinct(StringComparer.Ordinal).ToList();
            var mappings = await db.IntegrationMappings
                .Where(item =>
                    item.Active &&
                    item.Provider == Provider &&
                    item.TmsEntityType == "LoadStop" &&
                    externalStopIds.Contains(item.ExternalKey))
                .ToListAsync(ct);

            var updated = 0;
            foreach (var changedStop in changedStops)
            {
                var mapping = mappings.FirstOrDefault(item =>
                    string.Equals(item.ExternalKey, changedStop.Id, StringComparison.Ordinal));
                if (mapping is null)
                    continue;

                var stop = changedStop.Element;
                var progress = new SamsaraStopProgressState(
                    Text(stop, "state"),
                    entry.Operation,
                    entry.OccurredAtUtc,
                    ParseDate(stop, "enRouteTime"),
                    ParseDate(stop, "arrivalTime") ?? ParseDate(stop, "actualArrivalTime"),
                    ParseDate(stop, "departureTime") ?? ParseDate(stop, "actualDepartureTime"),
                    ParseDate(stop, "skippedTime"),
                    ParseDate(stop, "eta") ?? ParseDate(stop, "estimatedArrivalTime"),
                    Text(stop, "liveSharingUrl"));

                mapping.Notes = JsonSerializer.Serialize(progress, JsonOptions);
                mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
                mapping.UpdatedBy = "system:samsara-progress";
                updated++;
            }

            if (updated > 0)
                await db.SaveChangesAsync(ct);

            return updated;
        }
    }

    private async Task SaveCursorAsync(string cursor, CancellationToken ct)
    {
        var mapping = await db.IntegrationMappings.SingleOrDefaultAsync(item =>
            item.Active &&
            item.Provider == Provider &&
            item.TmsEntityType == CursorEntityType &&
            item.TmsEntityId == CursorEntityId, ct);

        if (mapping is null)
        {
            mapping = new IntegrationMapping
            {
                Provider = Provider,
                ExternalKey = cursor,
                ExternalLabel = "Route audit feed",
                TmsEntityType = CursorEntityType,
                TmsEntityId = CursorEntityId,
                Active = true,
                MappingKind = CursorMappingKind,
                NormalizedExternalValue = cursor,
                Notes = "Latest successfully processed Samsara route audit-feed cursor.",
                UpdatedBy = "system:samsara-progress"
            };
            db.IntegrationMappings.Add(mapping);
        }
        else
        {
            mapping.ExternalKey = cursor;
            mapping.NormalizedExternalValue = cursor;
            mapping.UpdatedAtUtc = DateTimeOffset.UtcNow;
            mapping.UpdatedBy = "system:samsara-progress";
        }

        await db.SaveChangesAsync(ct);
    }

    private static string? Text(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static DateTimeOffset? ParseDate(JsonElement item, string propertyName)
    {
        var value = Text(item, propertyName);
        return DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;
    }
}

public sealed class SamsaraRouteProgressWorker(
    IServiceScopeFactory scopeFactory,
    SamsaraOptions options,
    ILogger<SamsaraRouteProgressWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled || !options.EnableRouteProgressSync)
        {
            logger.LogInformation("Samsara route-progress sync is disabled.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(options.RouteProgressPollSeconds, 15, 300));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<SamsaraRouteProgressService>();
                var result = await service.PollAsync(stoppingToken);

                if (result.StopsUpdated > 0)
                {
                    logger.LogInformation(
                        "Samsara route progress processed {EventCount} event(s) across {PageCount} page(s) and updated {StopCount} stop snapshot(s).",
                        result.EventsRead,
                        result.PagesRead,
                        result.StopsUpdated);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Samsara route-progress polling failed; the next scheduled poll will retry.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
