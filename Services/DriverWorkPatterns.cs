using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Each employed driver's working pattern as held in Sage HR, e.g. "5 on 2 off, then 6 on
/// 1 off" from a cycle start date, or fixed weekdays. Sage HR is the source of the plan;
/// Tacho stays the authority on what the driver has actually worked and what is legal.
///
/// Patterns are stored append-only (latest per driver wins) so changes are auditable.
/// </summary>
public static class DriverWorkPatterns
{
    public const string EntityType = "driverworkpattern";
    public const string SourceSage = "Sage HR";
    public const string SourceSageEntered = "Sage HR (entered)";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];

    /// <param name="Blocks">"5-2,6-1" = 5 on, 2 off, 6 on, 1 off repeating; or weekdays "Mon,Tue,Wed,Thu,Fri".</param>
    /// <param name="CycleStart">First day of the first "on" block (ignored for weekday patterns).</param>
    public sealed record Pattern(Guid DriverId, string Name, string Blocks, DateOnly CycleStart, string Source, string? UpdatedBy, DateTimeOffset UpdatedAtUtc, bool Cleared = false);

    public sealed record SaveRequest(string? Name, string? Blocks, DateOnly? CycleStart, string? Source);

    /// <summary>Where the date falls in the pattern. Day/BlockLength are null on a rest day.</summary>
    public sealed record DayPlan(bool Working, int? Day, int? BlockLength, int CycleLength);

    /// <summary>The repeating cycle as working (true) / rest (false) days, or null if the text is not a pattern.</summary>
    public static bool[]? ParseCycle(string? blocks, out bool weekdays)
    {
        weekdays = false;
        var text = (blocks ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        if (text.Any(char.IsLetter))
        {
            var days = text.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => Array.FindIndex(DayNames, name => token.StartsWith(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (days.Count == 0 || days.Any(index => index < 0)) return null;
            weekdays = true;
            var week = new bool[7];
            foreach (var index in days) week[index] = true;
            return week;
        }

        var cycle = new List<bool>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split(new[] { '-', '/', ':' }, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2 || !int.TryParse(pieces[0], out var on) || !int.TryParse(pieces[1], out var off)) return null;
            if (on is < 1 or > 14 || off is < 0 or > 14) return null;
            cycle.AddRange(Enumerable.Repeat(true, on));
            cycle.AddRange(Enumerable.Repeat(false, off));
        }
        return cycle.Count is >= 2 and <= 56 && cycle.Contains(false) ? cycle.ToArray() : null;
    }

    public static DayPlan? Evaluate(Pattern pattern, DateOnly date)
    {
        var cycle = ParseCycle(pattern.Blocks, out var weekdays);
        if (cycle is null) return null;
        // Weekday patterns are a 7-day cycle starting on Monday.
        var index = weekdays
            ? ((int)date.DayOfWeek + 6) % 7
            : Mod(date.DayNumber - pattern.CycleStart.DayNumber, cycle.Length);
        if (!cycle[index]) return new DayPlan(false, null, null, cycle.Length);

        if (cycle.All(day => day)) return new DayPlan(true, null, null, cycle.Length);
        var back = 0;
        while (cycle[Mod(index - back - 1, cycle.Length)]) back++;
        var forward = 0;
        while (cycle[Mod(index + forward + 1, cycle.Length)]) forward++;
        return new DayPlan(true, back + 1, back + 1 + forward, cycle.Length);
    }

    public static string Describe(string blocks)
    {
        var cycle = ParseCycle(blocks, out var weekdays);
        if (cycle is null) return blocks;
        if (weekdays) return string.Join(", ", DayNames.Where((_, index) => cycle[index]));
        return string.Join(", then ", blocks.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var pieces = part.Split(new[] { '-', '/', ':' }, StringSplitOptions.TrimEntries);
            return $"{pieces[0]} on {pieces[1]} off";
        }));
    }

    public static async Task<Dictionary<Guid, Pattern>> LatestAsync(TmsDbContext db, CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType)
            .OrderByDescending(row => row.ReceivedAtUtc)
            .Take(5000)
            .Select(row => row.PayloadJson)
            .ToListAsync(ct);
        var result = new Dictionary<Guid, Pattern>();
        var seen = new HashSet<Guid>();
        foreach (var json in rows)
        {
            try
            {
                var pattern = JsonSerializer.Deserialize<Pattern>(json, JsonOptions);
                if (pattern is null || !seen.Add(pattern.DriverId) || pattern.Cleared) continue;
                result[pattern.DriverId] = pattern;
            }
            catch (JsonException) { }
        }
        return result;
    }

    public static async Task<Pattern> SaveAsync(TmsDbContext db, Guid driverId, SaveRequest request, string? user, CancellationToken ct)
    {
        var blocks = (request.Blocks ?? string.Empty).Trim();
        var cleared = blocks.Length == 0;
        if (!cleared && ParseCycle(blocks, out _) is null)
            throw new ArgumentException("Pattern must be on/off blocks like \"5-2,6-1\" or weekdays like \"Mon,Tue,Wed,Thu,Fri\".");
        var pattern = new Pattern(driverId,
            string.IsNullOrWhiteSpace(request.Name) ? (cleared ? "" : Describe(blocks)) : request.Name.Trim()[..Math.Min(80, request.Name.Trim().Length)],
            blocks, request.CycleStart ?? DateOnly.FromDateTime(DateTime.UtcNow),
            request.Source == SourceSage ? SourceSage : SourceSageEntered,
            user, DateTimeOffset.UtcNow, cleared);
        db.StagedImports.Add(new StagedImport
        {
            EntityType = EntityType,
            IdempotencyKey = $"driverworkpattern:{driverId:N}:{Guid.NewGuid():N}",
            Status = StagingStatus.Promoted,
            Source = pattern.Source,
            PayloadJson = JsonSerializer.Serialize(pattern, JsonOptions),
            ReviewedBy = user,
            ReviewedAtUtc = pattern.UpdatedAtUtc
        });
        await db.SaveChangesAsync(ct);
        return pattern;
    }

    private static int Mod(int value, int modulus) => ((value % modulus) + modulus) % modulus;
}

/// <summary>Combines the Sage HR plan for the day with what Tacho shows has been worked.</summary>
public static class WorkPatternFit
{
    public sealed record Result(int? DisplayDay, int? BlockLength, bool RestDay, string? Note);

    public static Result Resolve(DriverWorkPatterns.DayPlan? plan, string? patternName, int? tachoDay, int? tachoCycleLength, string? lastWeeklyRestType)
    {
        if (plan is null) return new Result(null, null, false, tachoDay is not null ? "No Sage work pattern recorded: block worked out from Tacho." : null);
        if (!plan.Working)
            return new Result(null, null, true, $"Rest day on {patternName} (Sage HR).");
        if (plan.Day is not int day || plan.BlockLength is not int block) return new Result(null, null, false, null);

        var display = Math.Max(day, tachoDay ?? 0);
        string? note;
        if (lastWeeklyRestType == "Reduced24" && display >= 6)
            note = $"Sage block is {block} days, but last weekly rest was reduced: a full 45h rest is due before Day 6.";
        else if (lastWeeklyRestType == "Reduced24" && display == 5 && block > 5)
            note = "Last legal day this block: reduced rest last week, so a full 45h rest after today.";
        else if (tachoDay is int worked && worked > day)
            note = $"Tacho shows Day {worked}: {worked - day} more than the Sage pattern.";
        else if (display > block)
            note = $"Beyond the {block}-day block on the Sage pattern.";
        else if (display == block)
            note = "Last day of the block, then rest.";
        else
            note = null;
        return new Result(display, block, false, note);
    }
}
