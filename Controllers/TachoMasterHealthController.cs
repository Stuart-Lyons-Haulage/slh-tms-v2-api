using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/tachomaster")]
public sealed class TachoMasterHealthController(
    TmsDbContext? db,
    TachoMasterClient tachoMasterClient,
    ILogger<TachoMasterHealthController> logger) : ControllerBase
{
    private const double LiveJobAgeMinutes = 15;
    private const double StaleJobAgeMinutes = 30;

    internal TachoMasterHealthController(
        TachoMasterClient tachoMasterClient,
        ILogger<TachoMasterHealthController> logger)
        : this(null, tachoMasterClient, logger)
    {
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!tachoMasterClient.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "unconfigured",
                configured = false,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                missingSettings = tachoMasterClient.MissingSettings
            });
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var today = UkOperatingDate(now);

            // TachoMaster upstream calls may run together, but SQL work on this scoped DbContext
            // is deliberately kept out of Task.WhenAll. This health endpoint must work with the
            // normal production connection string and must never require MultipleActiveResultSets.
            var profilesTask = tachoMasterClient.GetDriverProfilesAsync(cancellationToken);
            var openDutiesTask = tachoMasterClient.GetOpenDriverStatusesByVehicleAsync(today, cancellationToken);
            var dayDutiesTask = tachoMasterClient.GetDriverDutyStatusesAsync(today, cancellationToken);
            await Task.WhenAll(profilesTask, openDutiesTask, dayDutiesTask);

            var profiles = await profilesTask;
            var duties = await openDutiesTask;
            var dayDuties = await dayDutiesTask;
            var latestPersistedSyncUtc = db is null
                ? null
                : await db.Drivers.AsNoTracking()
                    .Where(driver => driver.LastTachoSyncUtc != null)
                    .MaxAsync(driver => driver.LastTachoSyncUtc, cancellationToken);

            var lastSuccessfulPollUtc = DateTimeOffset.UtcNow;
            var openDuties = duties.Values.SelectMany(items => items).ToList();

            var newestMetric = profiles.Select(item => item.MetricsValidAtUtc)
                .Concat(openDuties.Select(item => item.MetricsValidAtUtc))
                .Where(item => item is not null)
                .Select(item => item!.Value)
                .DefaultIfEmpty()
                .Max();
            var metricsAgeMinutes = newestMetric == default ? (double?)null : Math.Max(0, Math.Round((lastSuccessfulPollUtc - newestMetric).TotalMinutes, 1));
            var metricsFreshness = metricsAgeMinutes switch
            {
                null => "unknown",
                <= 15 => "live",
                <= 60 => "delayed",
                _ => "stale"
            };
            var metricsStale = metricsAgeMinutes is null || metricsAgeMinutes > 60;

            var jobAgeMinutes = latestPersistedSyncUtc is null
                ? (double?)null
                : Math.Max(0, Math.Round((lastSuccessfulPollUtc - latestPersistedSyncUtc.Value).TotalMinutes, 1));
            var jobFreshness = JobFreshness(jobAgeMinutes);
            var jobStale = db is not null && (jobAgeMinutes is null || jobAgeMinutes > StaleJobAgeMinutes);

            var latestDutyStartUtc = dayDuties.Count == 0
                ? (DateTimeOffset?)null
                : dayDuties.Max(item => item.DutyStartUtc);
            var latestDutyEndUtc = dayDuties
                .Where(item => item.DutyEndUtc is not null)
                .Select(item => item.DutyEndUtc)
                .OrderByDescending(value => value)
                .FirstOrDefault();

            return Ok(new
            {
                status = jobStale ? "degraded" : "healthy",
                configured = true,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                operatingDate = today,
                driverProfiles = profiles.Count,
                dayDutyRecords = dayDuties.Count,
                dayDutyVehicles = dayDuties.Select(item => item.VehicleCode).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                dayDutiesWithEnd = dayDuties.Count(item => item.DutyEndUtc is not null),
                dayDutiesWithoutEnd = dayDuties.Count(item => item.DutyEndUtc is null),
                latestDutyStartUtc,
                latestDutyEndUtc,
                currentVehicleDuties = openDuties.Count,
                openVehicleDuties = openDuties.Count,
                connectionFreshness = "live",
                lastSuccessfulPollUtc,
                scheduledSync = new
                {
                    expectedIntervalMinutes = 5,
                    latestPersistedSyncUtc,
                    ageMinutes = jobAgeMinutes,
                    freshness = jobFreshness,
                    stale = jobStale,
                    warning = jobStale
                        ? "The scheduled TachoMaster synchronisation has not refreshed persisted driver data within 30 minutes. Check the slh-tms-job-tachomaster Container Apps Job execution history and deployed jobs image."
                        : (string?)null
                },
                metricsFreshness,
                newestMetricsTimestampUtc = newestMetric == default ? (DateTimeOffset?)null : newestMetric,
                metricsAgeMinutes,
                metricsStale,
                sourceFreshness = metricsFreshness,
                newestSourceTimestampUtc = newestMetric == default ? (DateTimeOffset?)null : newestMetric,
                sourceAgeMinutes = metricsAgeMinutes,
                stale = jobStale,
                sourceDataStale = metricsStale,
                checkedAtUtc = lastSuccessfulPollUtc
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "TachoMaster health check failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "upstream-failure",
                configured = true,
                usesSharedRoadTechCredentials = tachoMasterClient.UsesSharedRoadTechCredentials,
                error = exception.GetType().Name,
                message = exception.Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }

    internal static string JobFreshness(double? ageMinutes) => ageMinutes switch
    {
        null => "unknown",
        <= LiveJobAgeMinutes => "live",
        <= StaleJobAgeMinutes => "delayed",
        _ => "stale"
    };

    private static DateOnly UkOperatingDate(DateTimeOffset value)
    {
        try
        {
            return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).DateTime);
        }
        catch (TimeZoneNotFoundException)
        {
            return DateOnly.FromDateTime(value.UtcDateTime);
        }
    }
}
