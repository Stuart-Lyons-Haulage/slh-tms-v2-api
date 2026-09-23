using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Jobs;

public sealed class SageHrScheduledJob(
    TmsDbContext db,
    IntegrationSyncCoordinator integration,
    ILogger<SageHrScheduledJob> logger)
{
    public async Task<JobExecutionResult> RunAsync(CancellationToken ct)
    {
        var zone = LondonTimeZone();
        var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone);
        if (localNow.TimeOfDay < new TimeSpan(5, 30, 0))
        {
            logger.LogInformation("SageHrScheduledJobNotDue LocalNow={LocalNow} DueLocalTime=05:30", localNow);
            return new JobExecutionResult(true, "Sage HR sync is not due yet for the current Europe/London date.");
        }

        var localDate = DateOnly.FromDateTime(localNow.DateTime);
        var startLocal = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var endLocal = localDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(startLocal, zone), TimeSpan.Zero);
        var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(endLocal, zone), TimeSpan.Zero);

        var alreadyCompleted = await db.StagedImports.AsNoTracking().AnyAsync(row =>
            row.EntityType == "sagehrsync" &&
            row.Status == StagingStatus.Promoted &&
            row.ReviewedAtUtc >= startUtc &&
            row.ReviewedAtUtc < endUtc, ct);

        if (alreadyCompleted)
        {
            logger.LogInformation("SageHrScheduledJobAlreadyCompleted LocalDate={LocalDate}", localDate);
            return new JobExecutionResult(true, $"Sage HR sync already completed for Europe/London date {localDate:yyyy-MM-dd}.");
        }

        var result = await integration.SyncSageHrAsync("system:aca-job:sagehr", ct);
        return new JobExecutionResult(result.Success, result.Message, result.Changed);
    }

    private static TimeZoneInfo LondonTimeZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time"); }
    }
}
