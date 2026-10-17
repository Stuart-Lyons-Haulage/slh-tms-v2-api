using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Models.Tracking;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/health/tracking")]
public sealed class DotTrackingHealthController(
    IServiceProvider services,
    DotTrackingOptions options,
    ILogger<DotTrackingHealthController> logger) : ControllerBase
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var checkedAtUtc = DateTimeOffset.UtcNow;
        if (!options.IsConfigured)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "unconfigured",
                configured = false,
                dataMask = options.DataMask,
                baseUrlValid = options.BaseUrlConfigurationError is null,
                configurationError = options.BaseUrlConfigurationError,
                checkedAtUtc
            });
        }

        try
        {
            // Resolve inside the guarded block so a malformed runtime setting can never
            // fail during controller construction and surface as an opaque HTTP 500.
            var trackingClient = services.GetRequiredService<DotTrackingClient>();
            var snapshotCapturedAtUtc = trackingClient.LiveSnapshotCapturedAtUtc;
            var providerRows = await trackingClient.GetLatestVehicleEventsAsync(cancellationToken);

            if (snapshotCapturedAtUtc is null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    status = "awaiting-central-snapshot",
                    configured = true,
                    dataMask = options.DataMask,
                    pollIntervalMinutes = options.PollIntervalMinutes,
                    providerRecords = 0,
                    snapshotCapturedAtUtc,
                    checkedAtUtc,
                    message = "The central RoadTech ingestion worker has not yet completed a successful live snapshot."
                });
            }

            var snapshotAge = checkedAtUtc - snapshotCapturedAtUtc.Value;
            if (snapshotAge > TimeSpan.FromMinutes(Math.Max(2, options.StaleAfterMinutes)))
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    status = "stale-central-snapshot",
                    configured = true,
                    dataMask = options.DataMask,
                    pollIntervalMinutes = options.PollIntervalMinutes,
                    providerRecords = providerRows.Count,
                    snapshotCapturedAtUtc,
                    snapshotAgeSeconds = Math.Round(snapshotAge.TotalSeconds),
                    checkedAtUtc,
                    message = "The last successful central RoadTech snapshot is stale. API request paths are intentionally not probing RoadTech independently."
                });
            }

            var records = providerRows.Select(DotTelemetryRecord.FromProvider).ToList();
            var gpsRecords = records
                .Where(record => record.Latitude is not null && record.Longitude is not null)
                .ToList();
            var driverNameRecords = records.Count(record => !string.IsNullOrWhiteSpace(record.DriverName));
            var driverCardRecords = records.Count(record => !string.IsNullOrWhiteSpace(record.DriverCardNumber));
            var driverEvidenceRecords = records.Count(record =>
                !string.IsNullOrWhiteSpace(record.DriverName) ||
                !string.IsNullOrWhiteSpace(record.DriverCardNumber));
            var extraPayloadSections = providerRows
                .SelectMany(row => row.Extra.Keys)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

            if (gpsRecords.Count == 0)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new
                {
                    status = "no-current-gps",
                    configured = true,
                    dataMask = options.DataMask,
                    providerRecords = records.Count,
                    gpsRecords = 0,
                    driverEvidenceRecords,
                    driverNameRecords,
                    driverCardRecords,
                    extraPayloadSections,
                    snapshotCapturedAtUtc,
                    snapshotAgeSeconds = Math.Round(snapshotAge.TotalSeconds),
                    checkedAtUtc,
                    message = "The latest central RoadTech snapshot contains no GPS coordinates."
                });
            }

            // Health probes are deliberately cache-only. The one-minute ingestion worker owns
            // the single RoadTech live request and persistence/live-status freshness.
            return Ok(new
            {
                status = "healthy",
                configured = true,
                dataMask = options.DataMask,
                pollIntervalMinutes = options.PollIntervalMinutes,
                providerRecords = records.Count,
                gpsRecords = gpsRecords.Count,
                driverEvidenceRecords,
                driverNameRecords,
                driverCardRecords,
                extraPayloadSections,
                snapshotCapturedAtUtc,
                snapshotAgeSeconds = Math.Round(snapshotAge.TotalSeconds),
                newestProviderEventUtc = gpsRecords.Max(record => record.EventTimeUtc),
                checkedAtUtc
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RoadTech central live snapshot health check failed.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                status = "snapshot-failure",
                configured = true,
                dataMask = options.DataMask,
                baseUrlValid = options.BaseUrlConfigurationError is null,
                error = exception.GetType().Name,
                message = exception.Message,
                checkedAtUtc = DateTimeOffset.UtcNow
            });
        }
    }
}
