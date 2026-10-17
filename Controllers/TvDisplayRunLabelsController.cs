using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController]
[Route("api/v1/tv-display/run-labels")]
public sealed class TvDisplayRunLabelsController(TmsDbContext db, IConfiguration configuration) : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public async Task<IActionResult> Get(
        [FromHeader(Name = "X-TV-Display-Key")] string? displayKey,
        [FromQuery] DateOnly? date,
        CancellationToken ct)
    {
        var pairedKeyAllowed = await TvDisplayKeyStore.ValidateAsync(db, displayKey, ct);
        var legacyKeyAllowed = TvWallboardAccess.IsAllowed(HttpContext, configuration);
        if (!pairedKeyAllowed && !legacyKeyAllowed)
            return Unauthorized(new { message = "This TV display is not authorised." });

        var day = date ?? UkOperatingDate(DateTimeOffset.UtcNow);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, day, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();
        await LoadCommercialStore.EnrichAsync(db, loads, ct);
        foreach (var load in loads) OvernightRunContinuity.Apply(load);

        return Ok(new
        {
            planningDate = day,
            labels = loads
                .OrderBy(load => load.Stops.Where(stop => stop.PlannedArrivalUtc is not null).Select(stop => stop.PlannedArrivalUtc).Min() ?? DateTimeOffset.MaxValue)
                .ThenBy(load => load.Reference)
                .Select(load => new
                {
                    loadId = load.Id,
                    reference = load.Reference,
                    displayReference = RunDisplayLabel.For(load)
                }).ToList()
        });
    }

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

internal static partial class RunDisplayLabel
{
    private static readonly TimeZoneInfo UkZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [GeneratedRegex(@"^PLAN-\d{8}-(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex InternalReferenceRegex();

    [GeneratedRegex(@"^RUN[\s_-]*\d{8}[\s_-]+0*(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DatedRunRegex();

    [GeneratedRegex(@"^(?:RUN[\s_-]*)?(\d+)(?:[\s_-]*(AM|PM))?$", RegexOptions.IgnoreCase)]
    private static partial Regex NumericRunRegex();

    [GeneratedRegex(@"\b(AM|PM)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PeriodRegex();

    public static string For(Load load)
    {
        var plannerRun = NoteValue(load.PlannerNotes, "Planner run");
        var runType = NoteValue(load.PlannerNotes, "Run type");
        var source = string.IsNullOrWhiteSpace(plannerRun) ? StripInternalReference(load.Reference) : plannerRun.Trim();
        var plannedPeriod = PeriodFromFirstStop(load);

        // Sequence is operational truth. A stale imported "AM" must not override a run whose
        // first actual planned movement is the previous evening at 17:00.
        if (plannedPeriod is not null && NumericRunRegex().IsMatch(source))
            source = PeriodRegex().Replace(source, string.Empty).Trim();

        var period = plannedPeriod ?? ExplicitPeriod(source) ?? ExplicitPeriod(runType);
        return Format(source, period, OvernightRunContinuity.IsCarryIn(load) || ExplicitOvernight(load.PlannerNotes));
    }

    private static string Format(string source, string? period, bool overnight)
    {
        var clean = source.Trim();
        var numeric = NumericRunRegex().Match(clean);
        if (numeric.Success)
        {
            var number = int.TryParse(numeric.Groups[1].Value, out var parsed) ? parsed.ToString() : numeric.Groups[1].Value;
            var resolvedPeriod = period ?? ExplicitPeriod(numeric.Groups[2].Value);
            return $"Run {number}{(resolvedPeriod is null ? string.Empty : $" {resolvedPeriod}")}{(overnight ? " O/N" : string.Empty)}";
        }

        clean = Regex.Replace(clean, @"^RUN[\s:_-]*", string.Empty, RegexOptions.IgnoreCase).Trim();
        clean = Regex.Replace(clean, @"[-_]+", " ").Trim();
        if (string.IsNullOrWhiteSpace(clean)) clean = "TBC";

        var existing = ExplicitPeriod(clean);
        if (existing is not null)
        {
            clean = PeriodRegex().Replace(clean, string.Empty).Trim();
            return $"Run {clean} {period ?? existing}{(overnight ? " O/N" : string.Empty)}";
        }

        return $"Run {clean}{(period is null ? string.Empty : $" {period}")}{(overnight ? " O/N" : string.Empty)}";
    }

    private static bool ExplicitOvernight(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Regex.IsMatch(value, @"\b(?:O/N|overnight|night[ -]?out)\b", RegexOptions.IgnoreCase) &&
        !Regex.IsMatch(value, @"\bnight[ -]?out:\s*(?:no|false)\b", RegexOptions.IgnoreCase);

    private static string StripInternalReference(string reference)
    {
        var dated = DatedRunRegex().Match(reference.Trim());
        if (dated.Success) return dated.Groups[1].Value;
        var match = InternalReferenceRegex().Match(reference.Trim());
        return match.Success ? match.Groups[1].Value : reference.Trim();
    }

    private static string? NoteValue(string? notes, string key)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        foreach (var part in notes.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = $"{key}:";
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return part[prefix.Length..].Trim();
        }
        return null;
    }

    private static string? ExplicitPeriod(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = PeriodRegex().Match(value);
        if (match.Success) return match.Groups[1].Value.ToUpperInvariant();
        if (value.Contains("morning", StringComparison.OrdinalIgnoreCase)) return "AM";
        if (value.Contains("afternoon", StringComparison.OrdinalIgnoreCase) || value.Contains("evening", StringComparison.OrdinalIgnoreCase)) return "PM";
        return null;
    }

    private static string? PeriodFromFirstStop(Load load)
    {
        var first = load.Stops
            .Where(stop => stop.PlannedArrivalUtc is not null)
            .OrderBy(stop => stop.Sequence)
            .FirstOrDefault()?.PlannedArrivalUtc;
        if (first is null) return null;
        var local = TimeZoneInfo.ConvertTime(first.Value, UkZone);
        return local.TimeOfDay >= TimeSpan.FromHours(12) ? "PM" : "AM";
    }
}
