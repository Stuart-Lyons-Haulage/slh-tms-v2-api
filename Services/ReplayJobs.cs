using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Runs "Re-parse" on the server. The screen used to drive it in small HTTP batches,
/// and a slow email made the request time out on a phone ("Load failed"). Here each
/// email is replayed in its own scope; one that fails is recorded and skipped.
/// </summary>
public sealed class ReplayJobs(IServiceScopeFactory scopes, ILogger<ReplayJobs> logger)
{
    private readonly ConcurrentDictionary<Guid, ReplayJob> jobs = new();

    public ReplayJob? Get(Guid id) => jobs.TryGetValue(id, out var job) ? job : null;

    public ReplayJob Start(RetainedOrderEvidenceReplayRequest request, ClaimsPrincipal user)
    {
        foreach (var old in jobs.Values.Where(job => job.Done && job.FinishedAtUtc < DateTimeOffset.UtcNow.AddHours(-6)).ToList())
            jobs.TryRemove(old.Id, out _);
        // One re-parse per planning window at a time.
        var running = jobs.Values.FirstOrDefault(job => !job.Done && job.Key == Key(request));
        if (running is not null) return running;

        var job = new ReplayJob(Key(request));
        jobs[job.Id] = job;
        var name = user.Identity?.Name ?? user.FindFirst("preferred_username")?.Value ?? "planner";
        var claims = user.Claims.Select(claim => new Claim(claim.Type, claim.Value)).ToList();
        claims.Add(new Claim(ClaimTypes.Name, name));
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, "replay-job", ClaimTypes.Name, ClaimTypes.Role));
        _ = Task.Run(() => RunAsync(job, request, actor));
        return job;
    }

    private static string Key(RetainedOrderEvidenceReplayRequest request) =>
        $"{request.ReceivedFromUtc:O}|{request.ReceivedToUtc:O}|{request.MinimumPlanningDate}|{request.MaximumPlanningDate}";

    private async Task RunAsync(ReplayJob job, RetainedOrderEvidenceReplayRequest request, ClaimsPrincipal actor)
    {
        var cursor = request with { MaxMessages = 2, AfterReceivedAtUtc = request.AfterReceivedAtUtc, AfterEvidenceId = request.AfterEvidenceId };
        try
        {
            for (var batch = 0; batch < 2000; batch++)
            {
                JsonElement? result = await TryBatchAsync(cursor, actor);
                if (result is null)
                {
                    // Retry the next email alone; if it still fails, record it and move past it.
                    var single = cursor with { MaxMessages = 1 };
                    result = await TryBatchAsync(single, actor);
                    if (result is null)
                    {
                        var next = await NextEvidenceAsync(cursor);
                        if (next is null) break;
                        job.Error($"{next.Value.Subject ?? "An email"} could not be re-parsed and was skipped.");
                        cursor = cursor with { AfterReceivedAtUtc = next.Value.ReceivedAtUtc, AfterEvidenceId = next.Value.Id };
                        continue;
                    }
                }
                var value = result.Value;
                job.Add(Int(value, "evidenceScanned"), Int(value, "eligibleOrders"), Int(value, "pendingAfterReplay"), Int(value, "legacyMappingExceptionsArchived"));
                if (value.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
                    foreach (var message in messages.EnumerateArray())
                        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                            job.Error($"{(message.TryGetProperty("subject", out var subject) ? subject.GetString() : "An email")}: {error.GetString()}");
                if (!value.TryGetProperty("hasMore", out var hasMore) || hasMore.ValueKind != JsonValueKind.True) break;
                cursor = cursor with
                {
                    AfterReceivedAtUtc = value.GetProperty("nextAfterReceivedAtUtc").GetDateTimeOffset(),
                    AfterEvidenceId = value.GetProperty("nextAfterEvidenceId").GetGuid()
                };
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Re-parse job {JobId} stopped.", job.Id);
            job.Error($"Re-parse stopped: {ex.GetBaseException().Message}");
        }
        job.Finish();
    }

    private async Task<JsonElement?> TryBatchAsync(RetainedOrderEvidenceReplayRequest request, ClaimsPrincipal actor)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var controller = new RetainedOrderEvidenceReplayController(
                scope.ServiceProvider.GetRequiredService<TmsDbContext>(),
                scope.ServiceProvider.GetRequiredService<StagingService>(),
                scope.ServiceProvider.GetRequiredService<ILogger<OrderIntakeController>>());
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = actor };
            http.Request.Scheme = "https";
            http.Request.Host = new HostString("slh-tms-api");
            controller.ControllerContext = new ControllerContext { HttpContext = http };
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var result = await controller.Replay(request, timeout.Token);
            return result is ObjectResult { Value: { } value } && result is OkObjectResult
                ? JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Re-parse batch failed.");
            return null;
        }
    }

    private async Task<(Guid Id, DateTimeOffset ReceivedAtUtc, string? Subject)?> NextEvidenceAsync(RetainedOrderEvidenceReplayRequest request)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
        var from = request.ReceivedFromUtc ?? DateTimeOffset.UtcNow.AddDays(-4);
        var row = await db.StagedImports.AsNoTracking()
            .Where(item => item.EntityType == "email-evidence" && item.ReceivedAtUtc >= from &&
                           (request.ReceivedToUtc == null || item.ReceivedAtUtc < request.ReceivedToUtc) &&
                           (request.AfterReceivedAtUtc == null || item.ReceivedAtUtc > request.AfterReceivedAtUtc ||
                            (item.ReceivedAtUtc == request.AfterReceivedAtUtc && request.AfterEvidenceId != null && item.Id > request.AfterEvidenceId)))
            .OrderBy(item => item.ReceivedAtUtc).ThenBy(item => item.Id)
            .Select(item => new { item.Id, item.ReceivedAtUtc, item.Source })
            .FirstOrDefaultAsync();
        return row is null ? null : (row.Id, row.ReceivedAtUtc, row.Source);
    }

    private static int Int(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt32(out var number) ? number : 0;
}

public sealed class ReplayJob(string key)
{
    private readonly object gate = new();
    private readonly List<string> errors = [];
    public Guid Id { get; } = Guid.NewGuid();
    public string Key { get; } = key;
    public bool Done { get; private set; }
    public DateTimeOffset? FinishedAtUtc { get; private set; }
    private int scanned, eligible, pending, archived;

    public void Add(int evidenceScanned, int eligibleOrders, int pendingAfter, int archivedPlaceholders)
    {
        lock (gate) { scanned += evidenceScanned; eligible += eligibleOrders; pending += pendingAfter; archived += archivedPlaceholders; }
    }
    public void Error(string message) { lock (gate) if (errors.Count < 50) errors.Add(message); }
    public void Finish() { lock (gate) { Done = true; FinishedAtUtc = DateTimeOffset.UtcNow; } }
    public object Snapshot()
    {
        lock (gate)
            return new { id = Id, done = Done, evidenceScanned = scanned, eligibleOrders = eligible, pendingAfterReplay = pending, legacyMappingExceptionsArchived = archived, errors = errors.ToList(), finishedAtUtc = FinishedAtUtc };
    }
}
