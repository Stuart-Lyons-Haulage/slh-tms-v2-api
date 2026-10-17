using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/order-intake/ledger"), Authorize]
public sealed class OrderIntakeLedgerController(OrderIntakeLedgerService ledger, Slh.Tms.Api.Data.TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int take = 200, CancellationToken ct = default) =>
        Ok(await ledger.ListAsync(take, ct));

    [HttpGet("{stagedImportId:guid}")]
    public async Task<IActionResult> Get(Guid stagedImportId, CancellationToken ct)
    {
        try
        {
            var (item, revision, movement) = await ledger.FindAsync(stagedImportId, ct);
            return Ok(new { item, revision, movement });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("{stagedImportId:guid}/replay"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Replay(Guid stagedImportId, CancellationToken ct)
    {
        try
        {
            var (item, revision, movement) = await ledger.FindAsync(stagedImportId, ct);
            var actor = User.Identity?.Name ?? User.FindFirst("oid")?.Value;
            db.StagedImportEvents.Add(StagingAudit.Create(item, "ReplayRequested", item.Status, "Controlled replay requested against retained source evidence.", actor));
            db.StagedImportEvents.Add(StagingAudit.Create(item, "ReplayCompleted", item.Status, "Idempotency matched the existing staged intake; no clone was created.", actor));
            await db.SaveChangesAsync(ct);
            return Ok(new { stagedImportId = item.Id, movementId = movement?.Id, revisionId = revision?.Id, idempotent = true, status = item.Status.ToString() });
        }
        catch (KeyNotFoundException) { return NotFound(); }
    }
}
