using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/staging/count")]
[Authorize]
public sealed class StagingCountController(TmsDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Count(
        [FromQuery] StagingStatus? status,
        [FromQuery] string? entityType,
        CancellationToken ct = default)
    {
        var query = db.StagedImports.AsNoTracking().AsQueryable();
        query = query.Where(x => x.Status == (status ?? StagingStatus.PendingReview));
        if (!string.IsNullOrWhiteSpace(entityType))
            query = query.Where(x => x.EntityType == entityType.Trim().ToLowerInvariant());

        return Ok(new { count = await query.CountAsync(ct) });
    }
}
