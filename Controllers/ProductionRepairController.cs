using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Narrow, read-key guarded production recovery surface. This exists only to repair the
/// scheduled-jobs managed identity and to queue the canonical Driver Master pass through
/// the API process that already has governed SQL access. Remove after production recovery.
/// </summary>
[ApiController]
[Route("api/v1/production-repair")]
public sealed class ProductionRepairController(
    TmsDbContext db,
    TachoDriverMasterSyncJobService driverMasterJobs,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost("scheduled-jobs-sql-user")]
    [AllowAnonymous]
    public async Task<IActionResult> RepairScheduledJobsSqlUser([FromQuery] Guid objectId, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();
        if (objectId == Guid.Empty) return BadRequest(new { message = "A managed-identity object ID is required." });

        const string principal = "slh-tms-jobs-prod-id";
        var objectIdText = objectId.ToString("D");
        var create = $"IF DATABASE_PRINCIPAL_ID(N'{principal}') IS NULL EXEC(N'CREATE USER [{principal}] FROM EXTERNAL PROVIDER WITH OBJECT_ID = ''{objectIdText}''');";
        await db.Database.ExecuteSqlRawAsync(create, ct);

        await db.Database.ExecuteSqlRawAsync($"IF IS_ROLEMEMBER(N'db_datareader', N'{principal}') <> 1 ALTER ROLE db_datareader ADD MEMBER [{principal}];", ct);
        await db.Database.ExecuteSqlRawAsync($"IF IS_ROLEMEMBER(N'db_datawriter', N'{principal}') <> 1 ALTER ROLE db_datawriter ADD MEMBER [{principal}];", ct);

        var exists = await db.Database.SqlQueryRaw<int>($"SELECT CASE WHEN DATABASE_PRINCIPAL_ID(N'{principal}') IS NULL THEN 0 ELSE 1 END AS Value").SingleAsync(ct);
        return Ok(new { principal, exists = exists == 1, roles = new[] { "db_datareader", "db_datawriter" }, repairedAtUtc = DateTimeOffset.UtcNow });
    }

    [HttpPost("driver-master")]
    [AllowAnonymous]
    public async Task<IActionResult> QueueDriverMaster(CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();
        var job = await driverMasterJobs.EnqueueAsync("system:production-repair", ct);
        return Accepted(new { job.JobId, job.Status, job.RequestedAtUtc });
    }

    [HttpGet("driver-master/{jobId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> DriverMasterStatus(Guid jobId, CancellationToken ct)
    {
        if (!TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();
        var job = await driverMasterJobs.GetAsync(jobId, ct);
        return job is null ? NotFound() : Ok(job);
    }
}
