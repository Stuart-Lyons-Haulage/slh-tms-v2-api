using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;
using Slh.Tms.Api.Services;

namespace Slh.Tms.Api.Controllers;

[ApiController, Route("api/v1/planning")]
[Authorize]
public sealed class PlannerRunSequenceController(TmsDbContext db) : ControllerBase
{
    private static readonly TimeZoneInfo UkZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    [HttpPost("resequence-runs/{date:datetime}"), Authorize(Policy = "TmsWrite")]
    public async Task<IActionResult> Resequence(DateTime date, CancellationToken ct)
    {
        var planningDate = DateOnly.FromDateTime(date);
        List<Load> loads;
        var registerFallback = false;
        try
        {
            loads = await db.Loads.Include(load => load.Stops)
                .Where(load => load.PlanningDate == planningDate && load.Status != LoadStatus.Cancelled)
                .ToListAsync(ct);
        }
        catch (Exception exception) when (SchemaUnavailable(exception))
        {
            db.ChangeTracker.Clear();
            loads = (await PlanningRegisterStore.ReadLoadsAsync(db, planningDate, ct))
                .Where(load => load.Status != LoadStatus.Cancelled)
                .ToList();
            registerFallback = true;
        }

        await LoadCommercialStore.EnrichAsync(db, loads, ct);
        var ordered = loads
            .Select(load => new { Load = load, Start = FirstOperationalTime(load) })
            .OrderBy(item => item.Start is null)
            .ThenBy(item => item.Start)
            .ThenBy(item => item.Load.CreatedAtUtc)
            .ThenBy(item => item.Load.Reference, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changed = 0;
        var withoutTime = new List<string>();
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var number = index + 1;
            var period = item.Start is null ? ExistingPeriod(item.Load.PlannerNotes) : Period(item.Start.Value);
            if (item.Start is null && period is null) withoutTime.Add(item.Load.Reference);
            period ??= "AM"; // deterministic fallback for legacy runs with no usable time evidence

            var notes = SetTag(item.Load.PlannerNotes, "Planner run", number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            notes = SetTag(notes, "Run type", period);
            if (string.Equals(notes, item.Load.PlannerNotes, StringComparison.Ordinal)) continue;

            item.Load.PlannerNotes = notes;
            changed++;
            if (registerFallback)
            {
                await PlanningRegisterStore.SaveLoadAsync(db, item.Load, User.Identity?.Name, ct);
            }
            else
            {
                await LoadCommercialStore.SaveAsync(db, item.Load,
                    new LoadCommercialValues(null, null, null, null, null, item.Load.EmptyMiles, null, null,
                        item.Load.PalletSpacesUsed, item.Load.TotalPalletSpaces, item.Load.CapacityType,
                        item.Load.DepotSplits, item.Load.TemperatureC, notes), User.Identity?.Name, ct);
            }
        }

        return Ok(new
        {
            planningDate,
            runs = ordered.Count,
            changed,
            rule = "Before 12:00 = AM; 12:00 and later = PM; one chronological Run 1..N sequence.",
            withoutTime
        });
    }

    internal static DateTimeOffset? FirstOperationalTime(Load load) =>
        load.Stops
            .Where(stop => stop.PlannedArrivalUtc is not null)
            .OrderBy(stop => stop.PlannedArrivalUtc)
            .ThenBy(stop => stop.Sequence)
            .Select(stop => stop.PlannedArrivalUtc)
            .FirstOrDefault();

    internal static string Period(DateTimeOffset value)
    {
        var local = TimeZoneInfo.ConvertTime(value, UkZone);
        return local.TimeOfDay >= TimeSpan.FromHours(12) ? "PM" : "AM";
    }

    private static string? ExistingPeriod(string? notes)
    {
        var value = Tag(notes, "Run type");
        if (string.Equals(value, "AM", StringComparison.OrdinalIgnoreCase)) return "AM";
        if (string.Equals(value, "PM", StringComparison.OrdinalIgnoreCase)) return "PM";
        return null;
    }

    private static string? Tag(string? notes, string key)
    {
        if (string.IsNullOrWhiteSpace(notes)) return null;
        var prefix = $"{key}:";
        return notes.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(part => part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();
    }

    private static string SetTag(string? notes, string key, string value)
    {
        var prefix = $"{key}:";
        var parts = (notes ?? string.Empty).Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        parts.Insert(0, $"{key}: {value}");
        return string.Join(" | ", parts);
    }

    private static bool SchemaUnavailable(Exception exception)
    {
        var message = exception.GetBaseException().Message;
        return exception is InvalidOperationException or DbUpdateException ||
               message.Contains("Invalid object name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Invalid column name", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Cannot find the object", StringComparison.OrdinalIgnoreCase);
    }
}
