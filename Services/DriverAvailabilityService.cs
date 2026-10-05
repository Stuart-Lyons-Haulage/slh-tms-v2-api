using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Controllers;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record DriverAvailabilitySummary(
    int EmployedAvailable,
    int AgencyConfirmed,
    int AgencyUnconfirmed,
    int CasualConfirmed,
    int CasualUnconfirmed,
    int UnavailableBlocked,
    int DriversRequired,
    int AvailableDrivers,
    int SurplusShortfall);

public sealed record DriverAvailabilityItem(
    Guid DriverId,
    Guid? AvailabilityWindowId,
    string EmployeeNumber,
    string DisplayName,
    string EmploymentType,
    string Group,
    bool Dispatchable,
    IReadOnlyList<string> BlockReasons,
    bool ClassificationMismatch,
    string? ClassificationReviewReason,
    string? AgencyName,
    string? Skills,
    string? DriverGroup,
    DateTimeOffset? AvailableFromUtc,
    DateTimeOffset? AvailableUntilUtc,
    bool AvailabilityConfirmed,
    bool LongTermPlacement,
    DateOnly? PlacementEndDate,
    string? UsualDays,
    string? Notes,
    string? BookingReference,
    int CurrentAllocationCount,
    string? CurrentAllocationReference,
    int? DriveAvailableTodayMinutes,
    int? WorkAvailableWeekMinutes,
    DateOnly? LicenceExpiry,
    DateOnly? CpcExpiry,
    DateOnly? TachoCardExpiry,
    string? LicenceStatus);

public sealed record DriverAvailabilitySnapshot(
    DateOnly PlanningDate,
    DateTimeOffset GeneratedAtUtc,
    DriverAvailabilitySummary Summary,
    IReadOnlyList<DriverAvailabilityItem> Drivers,
    int ClassificationMismatchCount);

public static class DriverAvailabilityService
{
    private const string DriverDetailType = "masterdetail:driver";
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    public static async Task<DriverAvailabilitySnapshot> ReadAsync(TmsDbContext db, DateOnly planningDate, CancellationToken ct, SageHrClient? sageHr = null, ILogger? logger = null)
    {
        var drivers = await db.Drivers.AsNoTracking().Where(driver => driver.Active).OrderBy(driver => driver.DisplayName).ToListAsync(ct);
        await MasterDetailStore.EnrichDriversAsync(db, drivers, ct);

        var (dayStartUtc, dayEndUtc) = OperatingDay(planningDate);
        var windows = await db.DriverAvailabilityWindows.AsNoTracking()
            .Where(window => window.AvailableFromUtc < dayEndUtc &&
                             (window.AvailableUntilUtc > dayStartUtc || window.LongTermPlacement) &&
                             (window.PlacementEndDate == null || window.PlacementEndDate >= planningDate))
            .OrderByDescending(window => window.Confirmed)
            .ThenByDescending(window => window.UpdatedAtUtc)
            .ToListAsync(ct);
        var loads = (await PlanningResilience.ReadLoadsAsync(db, planningDate, ct))
            .Where(load => load.Status != LoadStatus.Cancelled)
            .ToList();
        var allocations = loads.Where(load => load.DriverId is not null)
            .GroupBy(load => load.DriverId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(load => load.Reference).ToList());
        var details = await ReadDetailsAsync(db, drivers, ct);
        var sageRoster = await ReadSageRosterAsync(db, ct);
        var sageLeave = await ReadSageLeaveAsync(sageHr, planningDate, logger, ct);

        var items = drivers
            .Where(driver => DriverPopulationRules.IsDriver(driver) || IsRecognisedEmploymentType(driver))
            .Select(driver => Evaluate(
                driver,
                planningDate,
                windows.Where(window => window.DriverId == driver.Id).ToList(),
                allocations.GetValueOrDefault(driver.Id) ?? [],
                details.GetValueOrDefault(driver.Id) ?? DriverAvailabilityDetail.Empty,
                sageRoster,
                sageLeave))
            .OrderBy(item => GroupOrder(item.Group))
            .ThenBy(item => item.DisplayName)
            .ToList();

        static bool AvailableForRequirement(DriverAvailabilityItem item) => item.Dispatchable ||
            item.BlockReasons.Count > 0 && item.BlockReasons.All(reason => reason.StartsWith("Already allocated", StringComparison.Ordinal));
        var available = items.Count(AvailableForRequirement);
        var summary = new DriverAvailabilitySummary(
            items.Count(item => item.EmploymentType == "Employed" && AvailableForRequirement(item)),
            items.Count(item => item.EmploymentType == "Agency" && item.AvailabilityConfirmed && AvailableForRequirement(item)),
            items.Count(item => item.Group == "Agency unconfirmed"),
            items.Count(item => item.EmploymentType == "Casual" && item.AvailabilityConfirmed && AvailableForRequirement(item)),
            items.Count(item => item.Group == "Casual unconfirmed"),
            items.Count(item => item.Group == "Unavailable/blocked"),
            loads.Count,
            available,
            available - loads.Count);

        return new DriverAvailabilitySnapshot(planningDate, DateTimeOffset.UtcNow, summary, items, items.Count(item => item.ClassificationMismatch));
    }

    internal static DriverAvailabilityItem Evaluate(
        Driver driver,
        DateOnly planningDate,
        IReadOnlyList<DriverAvailabilityWindow> windows,
        IReadOnlyList<Load> allocations,
        DriverAvailabilityDetail detail,
        SageRosterEvidence sageRoster,
        IReadOnlySet<string>? sageLeave = null)
    {
        var employmentType = CanonicalEmploymentType(detail.EmploymentType ?? driver.DriverType, driver.DriverGroup);
        var employeeNumber = Normalise(driver.EmployeeNumber);
        var threeDigitEmployeeNumber = employeeNumber.Length == 3 && employeeNumber.All(char.IsDigit);
        var sageMatched = sageRoster.Available && sageRoster.EmployeeNumbers.Contains(employeeNumber);
        var mismatch = employmentType == "Employed"
            ? !threeDigitEmployeeNumber || (sageRoster.Available && !sageMatched)
            : sageMatched;
        var reviewReason = mismatch
            ? employmentType == "Employed"
                ? !threeDigitEmployeeNumber
                    ? "Master Data says Employed but the employee number is not a 3-digit payroll number."
                    : "Master Data says Employed but the latest SageHR driver roster has no matching employee number."
                : $"Master Data says {employmentType}, but the 3-digit employee number matches the SageHR employed-driver roster."
            : null;

        var occurrence = windows
            .Select(window => Occurrence(window, planningDate))
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .OrderByDescending(value => value.Window.Confirmed)
            .ThenByDescending(value => value.Window.UpdatedAtUtc)
            .FirstOrDefault();
        var hasOccurrence = occurrence.Window is not null;
        var blockReasons = new List<string>();

        if (employmentType == "Unknown") blockReasons.Add("Employment type is Unknown in Master Data.");
        if (detail.HolidayDates.Contains(planningDate) || sageLeave?.Contains(employeeNumber) == true) blockReasons.Add("Leave");
        if (employmentType == "Employed" && detail.ContractedDays.Count > 0 && !detail.ContractedDays.Contains(planningDate.DayOfWeek))
            blockReasons.Add("Outside known working pattern");
        if (driver.LicenceExpiry is DateOnly licence && licence < planningDate) blockReasons.Add("Driving licence expired");
        if (driver.CPCExpiry is DateOnly cpc && cpc < planningDate) blockReasons.Add("Driver CPC expired");
        if (driver.DigitalTachoCardExpiry is DateOnly card && card < planningDate) blockReasons.Add("Digital tachograph card expired");
        if (!string.IsNullOrWhiteSpace(driver.LicenceStatus) && !driver.LicenceStatus.Equals("Valid", StringComparison.OrdinalIgnoreCase))
            blockReasons.Add($"Licence/compliance: {driver.LicenceStatus.Trim()}");
        if (allocations.Count > 0) blockReasons.Add(allocations.Count == 1 ? $"Already allocated to {allocations[0].Reference}" : $"Already allocated to {allocations.Count} runs");

        var explicitAvailabilityRequired = employmentType is "Agency" or "Casual";
        var confirmed = hasOccurrence && occurrence.Window!.Confirmed;
        if (explicitAvailabilityRequired && !confirmed)
        {
            var expired = windows.Any(window => window.LongTermPlacement && window.PlacementEndDate < planningDate);
            blockReasons.Add(expired ? "Placement expired" : "No confirmed availability");
        }

        var dispatchable = blockReasons.Count == 0;
        var group = employmentType switch
        {
            "Employed" when dispatchable => "Employed available",
            "Agency" when confirmed && dispatchable => "Agency confirmed",
            "Agency" when !confirmed => "Agency unconfirmed",
            "Casual" when confirmed && dispatchable => "Casual confirmed",
            "Casual" when !confirmed => "Casual unconfirmed",
            _ => "Unavailable/blocked"
        };
        if (!dispatchable && confirmed) group = "Unavailable/blocked";

        var selected = hasOccurrence ? occurrence.Window : null;
        return new DriverAvailabilityItem(
            driver.Id,
            selected?.Id,
            driver.EmployeeNumber,
            driver.DisplayName,
            employmentType,
            group,
            dispatchable,
            blockReasons,
            mismatch,
            reviewReason,
            driver.AgencyName,
            detail.Skills ?? driver.Skills,
            driver.DriverGroup,
            hasOccurrence ? occurrence.FromUtc : null,
            hasOccurrence ? occurrence.UntilUtc : null,
            selected?.Confirmed ?? false,
            selected?.LongTermPlacement ?? false,
            selected?.PlacementEndDate,
            selected?.UsualDays,
            selected?.Notes,
            selected?.BookingReference,
            allocations.Count,
            allocations.FirstOrDefault()?.Reference,
            driver.TachoDriveAvailableTodayMinutes,
            driver.TachoWorkAvailableWeekMinutes,
            driver.LicenceExpiry,
            driver.CPCExpiry,
            driver.DigitalTachoCardExpiry,
            driver.LicenceStatus);
    }

    internal static string CanonicalEmploymentType(string? value, string? fallback)
    {
        var token = Normalise($"{value} {fallback}");
        if (token.Contains("AGENCY", StringComparison.Ordinal)) return "Agency";
        if (token.Contains("CASUAL", StringComparison.Ordinal) || token.Contains("ZEROHOUR", StringComparison.Ordinal)) return "Casual";
        if (token.Contains("EMPLOY", StringComparison.Ordinal) || token.Contains("DRIVER", StringComparison.Ordinal) || token.Contains("TRAMP", StringComparison.Ordinal)) return "Employed";
        return "Unknown";
    }

    internal static (DriverAvailabilityWindow Window, DateTimeOffset FromUtc, DateTimeOffset UntilUtc)? Occurrence(DriverAvailabilityWindow window, DateOnly date)
    {
        if (!window.LongTermPlacement)
        {
            var (dayStart, dayEnd) = OperatingDay(date);
            return window.AvailableFromUtc < dayEnd && window.AvailableUntilUtc > dayStart
                ? (window, window.AvailableFromUtc, window.AvailableUntilUtc)
                : null;
        }

        var localStart = TimeZoneInfo.ConvertTime(window.AvailableFromUtc, London);
        var localUntil = TimeZoneInfo.ConvertTime(window.AvailableUntilUtc, London);
        var startDate = DateOnly.FromDateTime(localStart.DateTime);
        var placementEnd = window.PlacementEndDate ?? DateOnly.FromDateTime(localUntil.DateTime);
        if (date < startDate || date > placementEnd || !MatchesUsualDay(window.UsualDays, date.DayOfWeek)) return null;
        var fromLocal = date.ToDateTime(TimeOnly.FromDateTime(localStart.DateTime), DateTimeKind.Unspecified);
        var untilDate = TimeOnly.FromDateTime(localUntil.DateTime) <= TimeOnly.FromDateTime(localStart.DateTime) ? date.AddDays(1) : date;
        var untilLocal = untilDate.ToDateTime(TimeOnly.FromDateTime(localUntil.DateTime), DateTimeKind.Unspecified);
        return (window, ToUtc(fromLocal), ToUtc(untilLocal));
    }

    private static bool MatchesUsualDay(string? value, DayOfWeek day)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var token = Normalise(value);
        var aliases = day switch
        {
            DayOfWeek.Monday => new[] { "MON", "MONDAY", "1" },
            DayOfWeek.Tuesday => new[] { "TUE", "TUESDAY", "2" },
            DayOfWeek.Wednesday => new[] { "WED", "WEDNESDAY", "3" },
            DayOfWeek.Thursday => new[] { "THU", "THURSDAY", "4" },
            DayOfWeek.Friday => new[] { "FRI", "FRIDAY", "5" },
            DayOfWeek.Saturday => new[] { "SAT", "SATURDAY", "6" },
            _ => new[] { "SUN", "SUNDAY", "0" }
        };
        return aliases.Any(token.Contains);
    }

    private static async Task<Dictionary<Guid, DriverAvailabilityDetail>> ReadDetailsAsync(TmsDbContext db, IReadOnlyCollection<Driver> drivers, CancellationToken ct)
    {
        var byCode = drivers.ToDictionary(driver => Normalise(driver.EmployeeNumber), driver => driver, StringComparer.OrdinalIgnoreCase);
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == DriverDetailType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Take(5000).ToListAsync(ct);
        var result = new Dictionary<Guid, DriverAvailabilityDetail>();
        foreach (var row in rows)
        {
            try
            {
                using var document = JsonDocument.Parse(row.PayloadJson);
                var root = document.RootElement;
                var code = Text(root, "employeeNumber") ?? Text(root, "driverId") ?? Text(root, "payrollNumber");
                if (string.IsNullOrWhiteSpace(code) || !byCode.TryGetValue(Normalise(code), out var driver) || result.ContainsKey(driver.Id)) continue;
                result[driver.Id] = new DriverAvailabilityDetail(
                    Text(root, "employmentType") ?? Text(root, "driverType"),
                    Values(root, "holidayDates").Concat(Values(root, "holidays")).Select(ParseDate).Where(value => value is not null).Select(value => value!.Value).ToHashSet(),
                    Values(root, "contractedDays").Select(ParseDay).Where(value => value is not null).Select(value => value!.Value).ToHashSet(),
                    Text(root, "skills"));
            }
            catch (JsonException) { }
        }
        return result;
    }

    private static async Task<SageRosterEvidence> ReadSageRosterAsync(TmsDbContext db, CancellationToken ct)
    {
        var payload = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == "sagehrsync" && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Select(row => row.PayloadJson).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(payload)) return SageRosterEvidence.Unavailable;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("activeDriverEmployeeNumbers", out var values) || values.ValueKind != JsonValueKind.Array)
                return SageRosterEvidence.Unavailable;
            return new(true, values.EnumerateArray().Select(value => Normalise(value.GetString())).Where(value => value.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }
        catch (JsonException) { return SageRosterEvidence.Unavailable; }
    }

    private static async Task<IReadOnlySet<string>> ReadSageLeaveAsync(SageHrClient? sageHr, DateOnly date, ILogger? logger, CancellationToken ct)
    {
        if (sageHr?.IsConfigured != true) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var employeesTask = sageHr.GetActiveEmployeesAsync(timeout.Token);
            var leaveTask = sageHr.GetOutOfOfficeAsync(date, timeout.Token);
            await Task.WhenAll(employeesTask, leaveTask);
            var employees = employeesTask.Result.Where(employee => !string.IsNullOrWhiteSpace(employee.EmployeeNumber)).ToDictionary(employee => employee.Id);
            return leaveTask.Result
                .Where(leave => employees.ContainsKey(leave.EmployeeId))
                .Select(leave => Normalise(employees[leave.EmployeeId].EmployeeNumber))
                .Where(value => value.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger?.LogWarning("SageHR leave exceeded the Driver Availability time budget for {PlanningDate}; persisted working-pattern evidence will be used.", date);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger?.LogWarning(exception, "SageHR leave was unavailable for Driver Availability on {PlanningDate}; persisted working-pattern evidence will be used.", date);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsRecognisedEmploymentType(Driver driver) => CanonicalEmploymentType(driver.DriverType, driver.DriverGroup) != "Unknown";
    private static int GroupOrder(string group) => group switch { "Employed available" => 0, "Agency confirmed" => 1, "Agency unconfirmed" => 2, "Casual confirmed" => 3, "Casual unconfirmed" => 4, _ => 5 };
    private static (DateTimeOffset Start, DateTimeOffset End) OperatingDay(DateOnly date) => (ToUtc(date.ToDateTime(TimeOnly.MinValue)), ToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue)));
    private static DateTimeOffset ToUtc(DateTime local) => new(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), London), TimeSpan.Zero);
    private static string Normalise(string? value) => new((value ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static DateOnly? ParseDate(string value) => DateOnly.TryParse(value, out var date) ? date : null;
    private static DayOfWeek? ParseDay(string value) => Enum.TryParse<DayOfWeek>(value, true, out var day) ? day : int.TryParse(value, out var number) && number is >= 0 and <= 6 ? (DayOfWeek)number : null;
    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    private static IEnumerable<string> Values(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return [];
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(item => item.ToString()).Where(item => item.Length > 0).ToArray();
        return value.ValueKind == JsonValueKind.String ? (value.GetString() ?? string.Empty).Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }
}

internal sealed record DriverAvailabilityDetail(string? EmploymentType, IReadOnlySet<DateOnly> HolidayDates, IReadOnlySet<DayOfWeek> ContractedDays, string? Skills)
{
    public static DriverAvailabilityDetail Empty { get; } = new(null, new HashSet<DateOnly>(), new HashSet<DayOfWeek>(), null);
}

internal sealed record SageRosterEvidence(bool Available, IReadOnlySet<string> EmployeeNumbers)
{
    public static SageRosterEvidence Unavailable { get; } = new(false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
}
