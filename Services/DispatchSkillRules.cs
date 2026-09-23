using Slh.Tms.Api.Contracts;

namespace Slh.Tms.Api.Services;

public static class DispatchSkillRules
{
    private static readonly (DispatchSkill Skill, string DisplayName)[] OrderedSkills =
    [
        (DispatchSkill.DoubleDecker, "DoubleDecker"),
        (DispatchSkill.MarketRun, "MarketRun"),
        (DispatchSkill.HazChem, "HazChem"),
        (DispatchSkill.Moffett, "Moffett"),
        (DispatchSkill.TailLift, "TailLift"),
        (DispatchSkill.RefrigeratedUnit, "RefrigeratedUnit"),
        (DispatchSkill.ManualHandling, "ManualHandling")
    ];

    public static DispatchSkill Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DispatchSkill.None;

        var result = DispatchSkill.None;
        var chunks = value.Split(new[] { ',', ';', '/', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(value.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Append(value);

        foreach (var chunk in chunks)
        {
            var token = Normalise(chunk);
            result |= token switch
            {
                "DD" or "DOUBLEDECK" or "DOUBLEDECKER" or "DOUBLEDECKTRAILER" => DispatchSkill.DoubleDecker,
                "M" or "MKT" or "MARKET" or "MARKETRUN" or "WHOLESALEMARKET" => DispatchSkill.MarketRun,
                "ADR" or "HAZCHEM" or "HAZARDOUSCHEMICALS" => DispatchSkill.HazChem,
                "MOFF" or "MOFFETT" => DispatchSkill.Moffett,
                "TL" or "TAILLIFT" => DispatchSkill.TailLift,
                "TEMP" or "FRIDGE" or "CHILLED" or "REFRIGERATED" or "REFRIGERATEDUNIT" => DispatchSkill.RefrigeratedUnit,
                "MH" or "MANUALHANDLING" => DispatchSkill.ManualHandling,
                _ => DispatchSkill.None
            };
        }

        return result;
    }

    public static bool HasAll(DispatchSkill held, DispatchSkill required) =>
        (held & required) == required;

    public static DispatchSkill Missing(DispatchSkill held, DispatchSkill required) =>
        required & ~held;

    public static IReadOnlyList<string> Names(DispatchSkill skills) => OrderedSkills
        .Where(item => skills.HasFlag(item.Skill))
        .Select(item => item.DisplayName)
        .ToArray();

    public static string MissingNames(DispatchSkill held, DispatchSkill required) =>
        string.Join(", ", Names(Missing(held, required)));

    private static string Normalise(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
