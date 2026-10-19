using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Runs bulk approvals in the background, one order at a time, each in its own database
/// scope so one failure cannot affect the rest. Progress is held in memory and polled by
/// the planner's screen.
/// </summary>
public sealed class BulkApprovalJobs(IServiceScopeFactory scopes, ILogger<BulkApprovalJobs> logger)
{
    private readonly ConcurrentDictionary<Guid, BulkApprovalJob> jobs = new();

    public BulkApprovalJob? Get(Guid id) => jobs.TryGetValue(id, out var job) ? job : null;

    public BulkApprovalJob Start(BulkApproveOrdersRequest request, ClaimsPrincipal user)
    {
        foreach (var old in jobs.Values.Where(job => job.Done && job.FinishedAtUtc < DateTimeOffset.UtcNow.AddHours(-6)).ToList())
            jobs.TryRemove(old.Id, out _);

        var job = new BulkApprovalJob(request.Date, request.Ids.Count);
        jobs[job.Id] = job;
        // Keep only the identity needed for the audit trail; the request's principal is
        // not valid once the HTTP request ends.
        var name = user.Identity?.Name ?? user.FindFirst("preferred_username")?.Value ?? user.FindFirst("oid")?.Value ?? "planner";
        var claims = user.Claims.Select(claim => new Claim(claim.Type, claim.Value)).ToList();
        claims.Add(new Claim(ClaimTypes.Name, name));
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, "bulk-approval", ClaimTypes.Name, ClaimTypes.Role));
        _ = Task.Run(() => RunAsync(job, request, actor));
        return job;
    }

    private async Task RunAsync(BulkApprovalJob job, BulkApproveOrdersRequest request, ClaimsPrincipal actor)
    {
        foreach (var id in request.Ids)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                var staging = scope.ServiceProvider.GetRequiredService<StagingService>();
                var item = await db.StagedImports.SingleOrDefaultAsync(x => x.Id == id);
                if (item is null) { job.Skip(id, "No longer in the review queue."); continue; }
                var eligibility = BulkOrderApprovalController.CheckEligibility(item, request.Date, request.AcknowledgeReviewFlags);
                if (eligibility is not null) { job.Skip(id, eligibility); continue; }
                if (request.AcknowledgeReviewFlags) BulkOrderApprovalController.MarkPlannerApproved(item, request.Date);
                var note = request.AcknowledgeReviewFlags
                    ? $"Explicitly selected and approved from Order Control for {request.Date:yyyy-MM-dd}; visible review flags were acknowledged by the planner."
                    : $"Approved from Order Control for {request.Date:yyyy-MM-dd}. Clean, planner-ready order.";
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var promoted = await staging.ReviewAndPromote(item.Id, true, note, actor, timeout.Token);
                if (promoted.Status == StagingStatus.Promoted) job.Approve(id);
                else job.Fail(id, $"Approval finished as {promoted.Status}.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Bulk approval job {JobId} could not approve staged order {StagedImportId}.", job.Id, id);
                job.Fail(id, ex.GetBaseException().Message);
            }
        }
        job.Finish();
    }
}

public sealed class BulkApprovalJob(DateOnly date, int total)
{
    private readonly object gate = new();
    private readonly List<object> failed = [];
    private readonly List<object> skipped = [];
    private readonly List<Guid> approvedIds = [];

    public Guid Id { get; } = Guid.NewGuid();
    public DateOnly Date { get; } = date;
    public int Total { get; } = total;
    public bool Done { get; private set; }
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAtUtc { get; private set; }

    public void Approve(Guid id) { lock (gate) approvedIds.Add(id); }
    public void Fail(Guid id, string reason) { lock (gate) failed.Add(new { id, reason }); }
    public void Skip(Guid id, string reason) { lock (gate) skipped.Add(new { id, reason }); }
    public void Finish() { lock (gate) { Done = true; FinishedAtUtc = DateTimeOffset.UtcNow; } }

    public object Snapshot()
    {
        lock (gate)
        {
            return new
            {
                id = Id,
                date = Date,
                total = Total,
                processed = approvedIds.Count + failed.Count + skipped.Count,
                approved = approvedIds.Count,
                approvedIds = approvedIds.ToList(),
                failed = failed.Count,
                skipped = skipped.Count,
                failedItems = failed.Take(100).ToList(),
                skippedItems = skipped.Take(100).ToList(),
                done = Done,
                startedAtUtc = StartedAtUtc,
                finishedAtUtc = FinishedAtUtc
            };
        }
    }
}
