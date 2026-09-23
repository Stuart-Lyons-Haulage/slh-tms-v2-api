using Slh.Tms.Api.Contracts;
using System.Text.RegularExpressions;

namespace Slh.Tms.Api.Services;

public static class PlannerPlanImportRules
{
    public static string TmsReference(DateOnly planningDate, string runRef)
    {
        if (string.IsNullOrWhiteSpace(runRef)) throw new ArgumentException("RunRef is required.", nameof(runRef));
        var clean = new string(runRef.Trim().ToUpperInvariant().Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(clean)) throw new ArgumentException("RunRef does not contain a usable identifier.", nameof(runRef));
        var reference = $"PLAN-{planningDate:yyyyMMdd}-{clean}";
        return reference[..Math.Min(reference.Length, 80)];
    }

    public static string PlannerRunLabel(PlannerPlanRunRequest run)
    {
        var source = string.IsNullOrWhiteSpace(run.PlannerRun) ? run.RunRef : run.PlannerRun;
        var period = PlannerPeriod(run);
        var clean = Regex.Replace(source.Trim(), @"\bWAVE\s*[13]\b", string.Empty, RegexOptions.IgnoreCase).Trim(' ', '-', '_', ':');
        var digits = new string(clean.Where(char.IsDigit).ToArray());
        var simpleSource = clean.All(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch));
        var label = digits.Length > 0 && simpleSource ? $"Run {int.Parse(digits)}" : clean;
        if (!label.StartsWith("Run ", StringComparison.OrdinalIgnoreCase)) label = $"Run {label}";
        var overnight = IsOvernight(run);
        var withPeriod = string.IsNullOrWhiteSpace(period) || label.Contains(period, StringComparison.OrdinalIgnoreCase) ? label : $"{label} {period}";
        return overnight && !withPeriod.Contains("O/N", StringComparison.OrdinalIgnoreCase) ? $"{withPeriod} O/N" : withPeriod;
    }

    public static string? PlannerPeriod(PlannerPlanRunRequest run)
    {
        var explicitText = $"{run.PlannerRun} {run.RunType}";
        if (explicitText.Contains("AM", StringComparison.OrdinalIgnoreCase)) return "AM";
        if (explicitText.Contains("PM", StringComparison.OrdinalIgnoreCase)) return "PM";
        var first = (run.Stops ?? [])
            .Where(stop => !string.IsNullOrWhiteSpace(stop.CollectFrom))
            .OrderBy(stop => stop.Sequence)
            .Select(stop => stop.CollectFrom)
            .FirstOrDefault();
        return TimeOnly.TryParse(first, out var time) ? time.Hour >= 12 ? "PM" : "AM" : null;
    }

    public static bool IsOvernight(PlannerPlanRunRequest run)
    {
        if ($"{run.PlannerNote} {run.PlannerRun} {run.RunType}".Contains("O/N", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch($"{run.PlannerNote} {run.PlannerRun} {run.RunType}", @"\bovernight\b|\bnight[ -]?out\b", RegexOptions.IgnoreCase)) return true;

        return (run.Stops ?? []).Any(stop => DateDiffers(run.PlanningDate, stop.CollectionSiteArrDate) || DateDiffers(run.PlanningDate, stop.DeliveredDate));
    }

    public static PalletCapacityResult Capacity(PlannerPlanRunRequest run)
    {
        decimal standard = 0m, euro = 0m, unknown = 0m;
        foreach (var stop in run.Stops ?? [])
        {
            var pallets = stop.Pallets ?? 0m;
            if (pallets <= 0) continue;
            var type = EffectivePalletType(stop);
            if (string.Equals(type, "standard", StringComparison.OrdinalIgnoreCase)) standard += pallets;
            else if (string.Equals(type, "euro", StringComparison.OrdinalIgnoreCase)) euro += pallets;
            else unknown += pallets;
        }
        return PalletCapacityCalculator.Calculate(standard, euro, unknown);
    }

    public static string? EffectivePalletType(PlannerPlanStopRequest stop)
    {
        var explicitType = Normalize(stop.PalletType);
        if (explicitType is "STD" or "STANDARD") return "standard";
        if (explicitType == "EURO") return "euro";
        if (explicitType.Contains("TRAY", StringComparison.Ordinal) || explicitType.Contains("CRATE", StringComparison.Ordinal)) return null;

        var collection = Normalize(stop.CollectionSite);
        var delivery = Normalize(stop.DeliverySite);

        // Stuart Lyons pallet rules: Morrisons and Waitrose are Standard pallets.
        if (delivery.Contains("MORRISONS", StringComparison.Ordinal) || delivery.Contains("WAITROSE", StringComparison.Ordinal)) return "standard";

        // Aldi loads from Barfoots or Natures Way Foods are Euro pallets.
        if (delivery.Contains("ALDI", StringComparison.Ordinal) &&
            (collection.Contains("BAR", StringComparison.Ordinal) || collection.Contains("BARFOOT", StringComparison.Ordinal) ||
             collection.Contains("NWF", StringComparison.Ordinal) || collection.Contains("NATURESWAY", StringComparison.Ordinal))) return "euro";

        // Langmeads to Aldi Atherstone is Euro; every other Langmeads pallet movement is Standard.
        if (collection.Contains("LANGMEAD", StringComparison.Ordinal) || collection.StartsWith("LAN", StringComparison.Ordinal))
            return delivery.Contains("ALDI", StringComparison.Ordinal) && delivery.Contains("ATHERSTONE", StringComparison.Ordinal) ? "euro" : "standard";

        return null;
    }

    public static string StopName(PlannerPlanStopRequest stop)
    {
        var collection = string.IsNullOrWhiteSpace(stop.CollectionSite) ? null : stop.CollectionSite.Trim();
        var delivery = string.IsNullOrWhiteSpace(stop.DeliverySite) ? null : stop.DeliverySite.Trim();
        return (collection, delivery) switch
        {
            ({ } c, { } d) => $"{c} → {d}",
            ({ } c, null) => c,
            (null, { } d) => d,
            _ => "Planner stop"
        };
    }

    public static string BuildPlannerNotes(PlannerPlanRunRequest run, PalletCapacityResult capacity)
    {
        var source = run.Source is null ? null : string.Join(" / ", new[] { run.Source.Workbook, run.Source.Sheet }.Where(v => !string.IsNullOrWhiteSpace(v)));
        var parts = new[]
        {
            run.PlannerNote,
            string.IsNullOrWhiteSpace(run.RunType) ? null : $"Run type: {NormalizeRunType(run.RunType)}",
            $"Planner run: {PlannerRunLabel(run)}",
            PlannerPeriod(run) is { } period ? $"Planner period: {period}" : null,
            IsOvernight(run) ? "O/N: Yes" : null,
            IsOvernight(run) ? "Night out: Yes" : null,
            string.IsNullOrWhiteSpace(run.ReconciliationStatus) ? null : $"Reconciliation: {run.ReconciliationStatus}",
            string.IsNullOrWhiteSpace(source) ? null : $"Source: {source}",
            $"Capacity: {capacity.StandardPallets:0.##} Standard + {capacity.EuroPallets:0.##} Euro + {capacity.UnknownPallets:0.##} unknown = {capacity.UtilisationPercent:0.0}% ({capacity.Status})"
        }.Where(v => !string.IsNullOrWhiteSpace(v));
        var text = string.Join(" | ", parts);
        return text[..Math.Min(text.Length, 1000)];
    }

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit)
        .Select(char.ToUpperInvariant)
        .ToArray());

    private static string NormalizeRunType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (Regex.IsMatch(value, @"\bWAVE\s*1\b", RegexOptions.IgnoreCase)) return "AM";
        if (Regex.IsMatch(value, @"\bWAVE\s*3\b", RegexOptions.IgnoreCase)) return "PM";
        return value.Trim();
    }

    private static bool DateDiffers(DateOnly planningDate, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Trim();
        return DateOnly.TryParse(normalized, out var parsed) && parsed != planningDate;
    }
}
