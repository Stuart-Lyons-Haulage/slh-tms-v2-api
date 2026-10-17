using System.Globalization;

namespace Slh.Tms.Api.Services;

public sealed record WaitroseLegacyRow(
    string Depot,
    int Pallets,
    string? Po,
    DateOnly CollectionDate,
    DateOnly DeliveryDate,
    TimeOnly? ReadyTime,
    bool IsAmendment);

public static class WaitroseLegacyWorkbookParser
{
    private static readonly string[] Depots = ["Aylesford", "Bracknell", "Brinklow", "Leyland"];

    public static IReadOnlyList<WaitroseLegacyRow> ParseVitacress(IReadOnlyList<object?[]> rows)
    {
        var heading = rows.FirstOrDefault(row => Contains(row, "COLLECTION DATE") && Contains(row, "DELIVERY DATE"));
        if (heading is null) return [];
        var collection = Date(heading.ElementAtOrDefault(2));
        var delivery = Date(heading.ElementAtOrDefault(5));
        if (collection is null || delivery is null) return [];
        var amended = rows.Any(row => Contains(row, "AMENDED"));
        var result = new List<WaitroseLegacyRow>();
        foreach (var row in rows)
        {
            var depot = Text(row.ElementAtOrDefault(1));
            if (!Depots.Contains(depot, StringComparer.OrdinalIgnoreCase)) continue;
            var pallets = Integer(row.ElementAtOrDefault(2));
            if (pallets is null or <= 0) continue;
            result.Add(new(depot!, pallets.Value, Text(row.ElementAtOrDefault(3)), collection.Value, delivery.Value,
                Time(row.ElementAtOrDefault(4)), amended));
        }
        return result;
    }

    public static IReadOnlyList<WaitroseLegacyRow> ParseApsWeekly(IReadOnlyList<object?[]> rows, DateOnly receivedDate)
    {
        var dayRow = rows.FirstOrDefault(row => Text(row.ElementAtOrDefault(0)).Equals("Depot day", StringComparison.OrdinalIgnoreCase));
        var depotRow = rows.FirstOrDefault(row => row.Count(value => Depots.Contains(Text(value), StringComparer.OrdinalIgnoreCase)) >= 4);
        var palletRow = rows.FirstOrDefault(row => Text(row.ElementAtOrDefault(0)).Contains("Number of Pallets", StringComparison.OrdinalIgnoreCase));
        var timeRow = rows.FirstOrDefault(row => Text(row.ElementAtOrDefault(0)).Contains("Time Ready", StringComparison.OrdinalIgnoreCase));
        var poRow = rows.FirstOrDefault(row => Text(row.ElementAtOrDefault(0)).Contains("PO Number", StringComparison.OrdinalIgnoreCase));
        if (dayRow is null || depotRow is null || palletRow is null || poRow is null) return [];

        var result = new List<WaitroseLegacyRow>();
        string? currentDay = null;
        for (var column = 1; column < depotRow.Length; column++)
        {
            var labelledDay = Text(dayRow.ElementAtOrDefault(column));
            if (Enum.TryParse<DayOfWeek>(labelledDay, true, out _)) currentDay = labelledDay;
            var depot = Text(depotRow.ElementAtOrDefault(column));
            var pallets = Integer(palletRow.ElementAtOrDefault(column));
            var po = Text(poRow.ElementAtOrDefault(column));
            if (!Depots.Contains(depot, StringComparer.OrdinalIgnoreCase) || pallets is null or <= 0 || string.Equals(po, "N/A", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(currentDay)) continue;
            var delivery = Nearest(receivedDate, Enum.Parse<DayOfWeek>(currentDay, true));
            result.Add(new(depot!, pallets.Value, po, delivery, delivery, Time(timeRow?.ElementAtOrDefault(column)), false));
        }
        return result;
    }

    private static DateOnly Nearest(DateOnly anchor, DayOfWeek target) => Enumerable.Range(-3, 7)
        .Select(offset => anchor.AddDays(offset))
        .OrderBy(date => Math.Abs(date.DayNumber - anchor.DayNumber))
        .First(date => date.DayOfWeek == target);

    private static bool Contains(object?[] row, string value) => row.Any(cell => Text(cell).Contains(value, StringComparison.OrdinalIgnoreCase));
    private static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;
    private static int? Integer(object? value) => value switch
    {
        double number => (int)Math.Round(number, MidpointRounding.AwayFromZero),
        int number => number,
        _ => int.TryParse(Text(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null
    };
    private static DateOnly? Date(object? value) => value switch
    {
        DateTime date => DateOnly.FromDateTime(date),
        double serial when serial > 1 => DateOnly.FromDateTime(DateTime.FromOADate(serial)),
        _ => DateOnly.TryParse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null
    };
    private static TimeOnly? Time(object? value)
    {
        if (value is DateTime date) return TimeOnly.FromDateTime(date);
        if (value is double fraction && fraction >= 0 && fraction < 1) return TimeOnly.FromTimeSpan(TimeSpan.FromDays(fraction));
        var text = Text(value).Replace(';', ':').Replace('.', ':');
        return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }
}
