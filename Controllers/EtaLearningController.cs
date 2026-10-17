using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

/// <summary>
/// Learns a small SLH-specific correction on top of Azure Maps HGV live-traffic ETAs.
/// Azure remains the route/traffic authority; this endpoint only learns the repeatable
/// difference between earlier ETA snapshots and the later confirmed geofence arrival.
/// One historical journey contributes at most one sample, preventing frequent wallboard
/// snapshots from overweighting a single run.
/// </summary>
[ApiController, Route("api/v1/operations/eta-learning")]
[Authorize]
public sealed class EtaLearningController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] int? lookbackDays,
        CancellationToken ct)
    {
        var suppliedKey = !string.IsNullOrWhiteSpace(displayKey)
            ? displayKey
            : Request.Query.TryGetValue("key", out var queryKey) ? queryKey.FirstOrDefault() : null;
        var paired = await TvDisplayKeyStore.ValidateAsync(db, suppliedKey, ct);
        if (!paired && !TvWallboardAccess.IsAllowed(HttpContext, configuration)) return Unauthorized();

        var days = Math.Clamp(lookbackDays ?? 42, 14, 120);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        try
        {
            await ManagementReportingStore.EnsureSchemaAsync(db, ct);
            var snapshots = await db.EtaSnapshots.AsNoTracking()
                .Where(item => item.CapturedAtUtc >= cutoff && item.EtaUtc != null &&
                    (item.Source == "Live" || item.Source == "Estimated" || item.Source == "Geofence" || item.Source == "GeofenceEstimated"))
                .OrderByDescending(item => item.CapturedAtUtc)
                .Take(12000)
                .ToListAsync(ct);
            if (snapshots.Count == 0) return Ok(new { generatedAtUtc = DateTimeOffset.UtcNow, corrections = Array.Empty<object>() });

            var stopIds = snapshots.Select(item => item.StopId).Distinct().ToList();
            var stops = await db.LoadStops.AsNoTracking()
                .Where(stop => stopIds.Contains(stop.Id))
                .Select(stop => new { stop.Id, stop.Name })
                .ToDictionaryAsync(stop => stop.Id, ct);
            var visits = await db.GeofenceVisits.AsNoTracking()
                .Where(visit => visit.LoadStopId != null && stopIds.Contains(visit.LoadStopId.Value) && visit.EnteredAtUtc >= cutoff)
                .Select(visit => new { StopId = visit.LoadStopId!.Value, visit.EnteredAtUtc })
                .ToListAsync(ct);
            var arrivals = visits
                .GroupBy(visit => visit.StopId)
                .ToDictionary(group => group.Key, group => group.Min(visit => visit.EnteredAtUtc));

            // Pick one representative prediction per completed stop: approximately one hour
            // before arrival where possible. This makes the learned error comparable between runs.
            var journeySamples = new List<(string DestinationKey, double ErrorMinutes)>();
            foreach (var group in snapshots.GroupBy(item => item.StopId))
            {
                if (!arrivals.TryGetValue(group.Key, out var arrivedAt) || !stops.TryGetValue(group.Key, out var stop)) continue;
                var candidates = group
                    .Where(item => item.EtaUtc is not null && item.CapturedAtUtc < arrivedAt)
                    .Select(item => new
                    {
                        Snapshot = item,
                        LeadMinutes = (arrivedAt - item.CapturedAtUtc).TotalMinutes
                    })
                    .Where(item => item.LeadMinutes is >= 20 and <= 360)
                    .OrderBy(item => Math.Abs(item.LeadMinutes - 60))
                    .ToList();
                if (candidates.Count == 0) continue;
                var chosen = candidates[0].Snapshot;
                var errorMinutes = (arrivedAt - chosen.EtaUtc!.Value).TotalMinutes;
                if (errorMinutes is < -120 or > 180) continue;
                journeySamples.Add((DestinationKey(stop.Name), errorMinutes));
            }

            var corrections = journeySamples
                .GroupBy(sample => sample.DestinationKey, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() >= 3)
                .Select(group =>
                {
                    var values = group.Select(sample => sample.ErrorMinutes).OrderBy(value => value).ToList();
                    var median = Median(values);
                    var count = values.Count;
                    var learningWeight = count >= 10 ? 0.45 : count >= 5 ? 0.35 : 0.25;
                    var correction = Math.Clamp((int)Math.Round(median * learningWeight), -25, 25);
                    return new
                    {
                        destinationKey = group.Key,
                        sampleCount = count,
                        medianHistoricalErrorMinutes = (int)Math.Round(median),
                        correctionMinutes = correction,
                        confidence = count >= 10 ? "High" : count >= 5 ? "Medium" : "Learning"
                    };
                })
                .OrderByDescending(item => item.sampleCount)
                .ToList();

            return Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                lookbackDays = days,
                baseline = "Azure Maps live traffic HGV route",
                method = "SLH correction from confirmed geofence arrival versus prior ETA; conservative weighted median",
                corrections
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // ETA learning is an enhancement only. Never make the live wallboard dependent on
            // historical calibration storage being available.
            return Ok(new
            {
                generatedAtUtc = DateTimeOffset.UtcNow,
                lookbackDays = days,
                baseline = "Azure Maps live traffic HGV route",
                warning = $"Historical ETA learning unavailable on this refresh: {exception.GetBaseException().Message}",
                corrections = Array.Empty<object>()
            });
        }
    }

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0;
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2d;
    }

    private static string DestinationKey(string? value)
    {
        var text = value?.Trim() ?? string.Empty;
        foreach (var prefix in new[] { "Collect · ", "Deliver · ", "Collect - ", "Deliver - " })
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..].Trim();
        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }
}
