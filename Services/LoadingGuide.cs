using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Loading guide for a run that mixes euro and standard pallets, named the way the planners
/// already name them ("20ep-10p" = 20 euro, 10 standard). The trailer deck is 13.6m:
///   standard 1.2 x 1.0m: two across, 1.0m per row  (26 per trailer);
///   euro     1.2 x 0.8m: three across, 1.2m per row (33 per trailer),
///                         or two lengthways, 0.8m per row, to use up an odd remainder.
/// The last drop is loaded first (at the nose) so each drop comes off the back in order;
/// within a drop, euros go in front of standards so each block stays square.
/// </summary>
public static class LoadingGuide
{
    public const double DeckLengthMetres = 13.6;

    public sealed record Pallet(string Type, string Collection, string Delivery, string OrderReference);
    public sealed record Row(int Number, string Type, string Layout, double DepthMetres, IReadOnlyList<Pallet> Pallets);
    public sealed record Guide(string LoadReference, int Euro, int Standard, string Code, IReadOnlyList<Row> Rows, double LengthMetres, bool OverLength);

    public static string? UnitOf(string? palletType)
    {
        var text = (palletType ?? string.Empty).ToLowerInvariant();
        if (text.Contains("tray") || text.Contains("crate") || text.Contains("trolley") || text.Contains("dolly") || text.Contains("ifco")) return null;
        if (text.Contains("euro") || text is "eur" or "ep" || text.StartsWith("ep ", StringComparison.Ordinal)) return "Euro";
        return "Standard";
    }

    /// <summary>Guides for every load in the plan that mixes euro and standard pallets.</summary>
    public static IReadOnlyList<Guide> ForPlan(IEnumerable<CustomerLoadPlanRow> rows) =>
        rows.GroupBy(row => row.LoadReference, StringComparer.OrdinalIgnoreCase)
            .Select(group => Build(group.Key, group.ToList()))
            .Where(guide => guide is not null)
            .Cast<Guide>()
            .OrderBy(guide => guide.LoadReference, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static Guide? Build(string loadReference, IReadOnlyList<CustomerLoadPlanRow> rows)
    {
        var lines = rows
            .Select(row => (row, unit: UnitOf(row.PalletType), count: Math.Max(row.Pallets ?? 0, 0)))
            .Where(line => line.unit is not null && line.count > 0)
            .ToList();
        var euro = lines.Where(line => line.unit == "Euro").Sum(line => line.count);
        var standard = lines.Where(line => line.unit == "Standard").Sum(line => line.count);
        if (euro == 0 || standard == 0) return null;

        // Last drop at the nose. Unknown drop order keeps the plan's order.
        var dropOrder = lines
            .Select(line => (line.row.DeliverySite, Sequence: line.row.DeliverySequence ?? 0))
            .GroupBy(item => item.DeliverySite, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Max(item => item.Sequence), StringComparer.OrdinalIgnoreCase);

        var result = new List<Row>();
        foreach (var drop in lines.GroupBy(line => line.row.DeliverySite, StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(group => dropOrder.GetValueOrDefault(group.Key)))
        {
            foreach (var unit in new[] { "Euro", "Standard" })
            {
                var pallets = drop.Where(line => line.unit == unit)
                    .OrderBy(line => line.row.CollectionSite, StringComparer.OrdinalIgnoreCase)
                    .SelectMany(line => Enumerable.Repeat(new Pallet(unit, line.row.CollectionSite, line.row.DeliverySite, line.row.OrderReference), line.count))
                    .ToList();
                var index = 0;
                var perRow = unit == "Euro" ? 3 : 2;
                while (index < pallets.Count)
                {
                    var remaining = pallets.Count - index;
                    if (remaining >= perRow)
                    {
                        result.Add(new Row(result.Count + 1, unit, unit == "Euro" ? "3 across" : "2 across", unit == "Euro" ? 1.2 : 1.0, pallets.Skip(index).Take(perRow).ToList()));
                        index += perRow;
                    }
                    else
                    {
                        // Odd remainder: euros turn lengthways (0.8m); a single standard takes a 1.0m row.
                        result.Add(new Row(result.Count + 1, unit, unit == "Euro" ? $"{remaining} lengthways" : "1 only", unit == "Euro" ? 0.8 : 1.0, pallets.Skip(index).ToList()));
                        index = pallets.Count;
                    }
                }
            }
        }

        var length = Math.Round(result.Sum(row => row.DepthMetres), 2);
        return new Guide(loadReference, euro, standard, $"{euro}ep-{standard}p", result, length, length > DeckLengthMetres + 0.001);
    }
}
