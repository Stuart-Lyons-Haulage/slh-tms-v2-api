using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record LiveDriverEvidence(
    TachoVehicleDriverStatus? LiveCard,
    TachoVehicleDriverStatus? TachoDuty,
    TachoVehicleDriverStatus? EtaAuthority,
    TachoVehicleDriverStatus? DisplayIdentity,
    string CardDutyStatus);

public static class LiveDriverEvidenceRules
{
    public static LiveDriverEvidence Resolve(
        IReadOnlyCollection<string> aliases,
        Driver? plannedDriver,
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statusesByVehicle)
    {
        var duty = ExecutionIdentityResolver.MatchTachoForDriver(aliases, plannedDriver, statusesByVehicle);
        var card = MatchFalconCard(aliases, plannedDriver, statusesByVehicle);

        if (card is not null && duty is not null)
        {
            if (!SameIdentity(card, duty))
                return new(card, duty, null, card, "Mismatch");
            return new(card, duty, duty, card, "Matched");
        }

        if (card is not null)
            return new(card, null, card, card, "LiveCardOnly");
        if (duty is not null)
            return new(null, duty, duty, duty, "TachoDutyOnly");
        return new(null, null, null, null, "Unavailable");
    }

    public static TachoVehicleDriverStatus? MatchFalconCard(
        IReadOnlyCollection<string> aliases,
        Driver? plannedDriver,
        IReadOnlyDictionary<string, IReadOnlyList<TachoVehicleDriverStatus>> statusesByVehicle)
    {
        var candidates = statusesByVehicle
            .Where(pair => ExecutionIdentityResolver.MatchesVehicleIdentifier(aliases, pair.Key))
            .SelectMany(pair => pair.Value)
            .Where(status => string.Equals(status.EvidenceSource, "FalconLiveCard", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(status => status.DutyStartUtc)
            .ToList();

        if (candidates.Count == 0) return null;
        if (plannedDriver is not null)
        {
            var plannedMatch = candidates.FirstOrDefault(status => ExecutionIdentityResolver.DriverMatches(plannedDriver, status));
            if (plannedMatch is not null) return plannedMatch;
        }
        return candidates[0];
    }

    public static bool SameIdentity(TachoVehicleDriverStatus left, TachoVehicleDriverStatus right)
    {
        if (left.MemberCode > 0 && right.MemberCode > 0)
            return left.MemberCode == right.MemberCode;

        var leftCard = Normalise(left.CardNumber);
        var rightCard = Normalise(right.CardNumber);
        if (leftCard.Length >= 8 && rightCard.Length >= 8)
            return leftCard == rightCard || leftCard.EndsWith(rightCard, StringComparison.OrdinalIgnoreCase) || rightCard.EndsWith(leftCard, StringComparison.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(left.EmployeeNumber) && !string.IsNullOrWhiteSpace(right.EmployeeNumber) &&
            string.Equals(Normalise(left.EmployeeNumber), Normalise(right.EmployeeNumber), StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(
            ExecutionIdentityResolver.NormalisePerson(left.DriverName),
            ExecutionIdentityResolver.NormalisePerson(right.DriverName),
            StringComparison.OrdinalIgnoreCase);
    }

    public static bool SpansUkMidnight(DateOnly operatingDate, DateTimeOffset dutyStartUtc, DateTimeOffset? dutyEndUtc)
    {
        var nextMidnight = StartOfUkDay(operatingDate.AddDays(1));
        return dutyStartUtc < nextMidnight && (dutyEndUtc is null || dutyEndUtc > nextMidnight);
    }

    private static DateTimeOffset StartOfUkDay(DateOnly date)
    {
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
            var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone));
        }
        catch (TimeZoneNotFoundException)
        {
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        }
    }

    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
