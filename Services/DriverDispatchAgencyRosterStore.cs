using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

public sealed record DriverDispatchAgencyRosterEntry(
    Guid DriverId,
    DateOnly WeekStart,
    DateOnly FromDate,
    DateOnly ThroughDate,
    int RequestedDays,
    string? DriverName,
    string? AgencyName,
    string AddedBy,
    DateTimeOffset AddedAtUtc);

public static class DriverDispatchAgencyRosterStore
{
    private const string EntityType = "driverdispatchagencyroster";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    public static DateOnly WeekStart(DateOnly date)
    {
        var daysSinceWednesday = ((int)date.DayOfWeek - (int)DayOfWeek.Wednesday + 7) % 7;
        return date.AddDays(-daysSinceWednesday);
    }

    public static DateOnly WeekEnd(DateOnly date) => WeekStart(date).AddDays(6);

    public static async Task<IReadOnlyDictionary<Guid, DriverDispatchAgencyRosterEntry>> ReadForDateAsync(
        TmsDbContext db,
        DateOnly date,
        CancellationToken ct)
    {
        var rows = await db.StagedImports.AsNoTracking()
            .Where(row => row.EntityType == EntityType && row.Status == StagingStatus.Promoted)
            .OrderByDescending(row => row.ReviewedAtUtc ?? row.ReceivedAtUtc)
            .Take(1000)
            .ToListAsync(ct);

        var result = new Dictionary<Guid, DriverDispatchAgencyRosterEntry>();
        foreach (var row in rows)
        {
            try
            {
                var entry = JsonSerializer.Deserialize<DriverDispatchAgencyRosterEntry>(row.PayloadJson, JsonOptions);
                if (entry is null || date < entry.FromDate || date > entry.ThroughDate || result.ContainsKey(entry.DriverId)) continue;
                result[entry.DriverId] = entry;
            }
            catch (JsonException) { }
        }
        return result;
    }

    public static async Task<DriverDispatchAgencyRosterEntry> UpsertAsync(
        TmsDbContext db,
        Driver driver,
        DateOnly fromDate,
        int requestedDays,
        string actor,
        CancellationToken ct)
    {
        var weekStart = WeekStart(fromDate);
        var weekEnd = weekStart.AddDays(6);
        var safeDays = Math.Clamp(requestedDays, 1, 7);
        var throughDate = fromDate.AddDays(safeDays - 1);
        if (throughDate > weekEnd) throughDate = weekEnd;

        var entry = new DriverDispatchAgencyRosterEntry(
            driver.Id,
            weekStart,
            fromDate,
            throughDate,
            safeDays,
            driver.DisplayName,
            driver.AgencyName ?? driver.DriverGroup,
            actor,
            DateTimeOffset.UtcNow);

        var key = $"{EntityType}:{weekStart:yyyyMMdd}:{driver.Id:N}";
        var row = await db.StagedImports.SingleOrDefaultAsync(item => item.IdempotencyKey == key, ct);
        if (row is null)
        {
            row = new StagedImport
            {
                EntityType = EntityType,
                IdempotencyKey = key,
                PayloadJson = "{}",
                Status = StagingStatus.Promoted,
                Source = "Driver Dispatch weekly agency roster",
                ReceivedAtUtc = DateTimeOffset.UtcNow
            };
            db.StagedImports.Add(row);
        }

        row.PayloadJson = JsonSerializer.Serialize(entry, JsonOptions);
        row.Status = StagingStatus.Promoted;
        row.ReviewedAtUtc = DateTimeOffset.UtcNow;
        row.ReviewedBy = actor;
        row.ReviewNote = $"Agency driver available {entry.FromDate:dd/MM/yyyy} to {entry.ThroughDate:dd/MM/yyyy} for the Wednesday-Tuesday planning week.";
        await db.SaveChangesAsync(ct);
        return entry;
    }
}
