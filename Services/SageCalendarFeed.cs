using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Slh.Tms.Api.Data;
using Slh.Tms.Api.Models;

namespace Slh.Tms.Api.Services;

/// <summary>
/// Sage HR's "Sync with my calendar" feed (the Who's off planner as iCal). Read hourly on the
/// server: each event is matched to a driver by name (first/surname in either order) and
/// becomes leave (holiday, sickness...) or a non-working day on that driver's availability.
/// The link is a secret (anyone with it sees everyone's leave), so it is stored encrypted
/// and never sent back to the browser.
/// </summary>
public sealed class SageCalendarFeed(IHttpClientFactory httpClientFactory, IDataProtectionProvider protection, ILogger<SageCalendarFeed> logger)
{
    public const string SettingType = "setting:sage-calendar";
    public const string SnapshotType = "sagecalendarsnapshot";
    private const string SettingKey = "setting:sage-calendar";
    private const string SnapshotKey = "sage-calendar:latest";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private IDataProtector Protector => protection.CreateProtector("SageCalendarFeedUrl");

    public sealed record Setting(string Protected, string Masked, string? UpdatedBy, DateTimeOffset UpdatedAtUtc);
    public sealed record DriverDay(Guid DriverId, string DriverName, DateOnly Date, string Kind, string Type);
    public sealed record Snapshot(
        DateTimeOffset FetchedAtUtc, int Events, int Matched, IReadOnlyList<DriverDay> Days,
        IReadOnlyList<string> Unmatched, IReadOnlyDictionary<string, int> Types, string? Problem,
        IReadOnlyList<string>? ArchivedDrivers = null, int Ignored = 0);

    public async Task SaveUrlAsync(TmsDbContext db, string url, string? user, CancellationToken ct)
    {
        var clean = Normalise(url) ?? throw new ArgumentException("That isn't a Sage HR calendar link (it should start webcal:// or https:// and contain /calendar/sync/).");
        var masked = Regex.Replace(clean, @"(/calendar/sync/)(\w{4})\w+(\w{4})", "$1$2…$3");
        var payload = JsonSerializer.Serialize(new Setting(Protector.Protect(clean), masked, user, DateTimeOffset.UtcNow), Json);
        var row = await db.StagedImports.FirstOrDefaultAsync(item => item.EntityType == SettingType && item.IdempotencyKey == SettingKey, ct);
        if (row is null)
            db.StagedImports.Add(new StagedImport { EntityType = SettingType, IdempotencyKey = SettingKey, PayloadJson = payload, Status = StagingStatus.Archived, Source = "Sage HR calendar link", ReviewedBy = user });
        else { row.PayloadJson = payload; row.ReviewedBy = user; row.ReviewedAtUtc = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
    }

    public static async Task<Setting?> ReadSettingAsync(TmsDbContext db, CancellationToken ct)
    {
        var json = await db.StagedImports.AsNoTracking().Where(item => item.EntityType == SettingType && item.IdempotencyKey == SettingKey).Select(item => item.PayloadJson).FirstOrDefaultAsync(ct);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<Setting>(json, Json); } catch (JsonException) { return null; }
    }

    public static async Task<Snapshot?> ReadSnapshotAsync(TmsDbContext db, CancellationToken ct)
    {
        var json = await db.StagedImports.AsNoTracking().Where(item => item.EntityType == SnapshotType && item.IdempotencyKey == SnapshotKey).Select(item => item.PayloadJson).FirstOrDefaultAsync(ct);
        if (json is null) return null;
        try { return JsonSerializer.Deserialize<Snapshot>(json, Json); } catch (JsonException) { return null; }
    }

    /// <summary>Driver id → what Sage says about that day (leave or non-working), for one date.</summary>
    public static async Task<IReadOnlyDictionary<Guid, DriverDay>> DayAsync(TmsDbContext db, DateOnly date, CancellationToken ct)
    {
        var snapshot = await ReadSnapshotAsync(db, ct);
        if (snapshot is null) return new Dictionary<Guid, DriverDay>();
        return snapshot.Days.Where(day => day.Date == date)
            .GroupBy(day => day.DriverId)
            .ToDictionary(group => group.Key, group => group.OrderBy(day => day.Kind == "Leave" ? 0 : 1).First());
    }

    public async Task<Snapshot> SyncAsync(TmsDbContext db, CancellationToken ct)
    {
        var setting = await ReadSettingAsync(db, ct);
        if (setting is null) return await StoreAsync(db, Failed("No Sage HR calendar link saved yet."), ct);
        string url;
        try { url = Protector.Unprotect(setting.Protected); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await StoreAsync(db, Failed("The saved Sage HR link can no longer be read; paste it again."), ct);
        }

        string ics;
        try
        {
            var client = httpClientFactory.CreateClient("SageCalendar");
            // Calendar apps identify themselves; some hosts turn away requests that don't.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (compatible; SLH-TMS calendar sync; +https://tms.lyonshaulage.com)");
            request.Headers.TryAddWithoutValidation("Accept", "text/calendar, text/plain, */*");
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return await StoreAsync(db, Failed($"Sage HR returned {(int)response.StatusCode} {response.ReasonPhrase}. If the link was reset in Sage, paste the new one."), ct);
            ics = await response.Content.ReadAsStringAsync(ct);
            if (!ics.Contains("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
            {
                var kind = response.Content.Headers.ContentType?.MediaType ?? "unknown";
                var hint = ics.Contains("<html", StringComparison.OrdinalIgnoreCase) ? "a web page (usually the Sage sign-in page), not a calendar" : $"something that isn't a calendar ({kind})";
                return await StoreAsync(db, Failed($"Sage HR sent back {hint}. Copy the link again from Who's off > Sync with my calendar and paste it here."), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sage HR calendar feed could not be read.");
            return await StoreAsync(db, Failed($"Sage HR calendar couldn't be reached: {ex.GetBaseException().Message}"), ct);
        }

        var drivers = await db.Drivers.AsNoTracking().Select(driver => new { driver.Id, driver.DisplayName, driver.Active }).ToListAsync(ct);
        var snapshot = Build(ParseEvents(ics),
            drivers.Where(driver => driver.Active).Select(driver => (driver.Id, driver.DisplayName)).ToList(), DateTimeOffset.UtcNow,
            drivers.Where(driver => !driver.Active).Select(driver => (driver.Id, driver.DisplayName)).ToList());
        logger.LogInformation("Sage HR calendar read: {Events} events, {Matched} matched to drivers, {Unmatched} names unmatched.", snapshot.Events, snapshot.Matched, snapshot.Unmatched.Count);
        return await StoreAsync(db, snapshot, ct);
    }

    private static Snapshot Failed(string problem) => new(DateTimeOffset.UtcNow, 0, 0, [], [], new Dictionary<string, int>(), problem);

    private static async Task<Snapshot> StoreAsync(TmsDbContext db, Snapshot snapshot, CancellationToken ct)
    {
        // A failed read keeps the last good days so availability doesn't lose everyone's leave.
        if (snapshot.Problem is not null && await ReadSnapshotAsync(db, ct) is { Days.Count: > 0 } previous)
            snapshot = previous with { Problem = snapshot.Problem };
        var payload = JsonSerializer.Serialize(snapshot, Json);
        var row = await db.StagedImports.FirstOrDefaultAsync(item => item.EntityType == SnapshotType && item.IdempotencyKey == SnapshotKey, ct);
        if (row is null)
            db.StagedImports.Add(new StagedImport { EntityType = SnapshotType, IdempotencyKey = SnapshotKey, PayloadJson = payload, Status = StagingStatus.Archived, Source = "Sage HR calendar" });
        else { row.PayloadJson = payload; row.ReceivedAtUtc = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct);
        return snapshot;
    }

    internal static string? Normalise(string? url)
    {
        var value = (url ?? string.Empty).Trim();
        if (value.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) value = "https://" + value["webcal://".Length..];
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!uri.Host.EndsWith(".sage.hr", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.Contains("/calendar/sync/", StringComparison.OrdinalIgnoreCase)) return null;
        return uri.ToString();
    }

    public sealed record CalendarEvent(string Summary, string? Description, string? Categories, DateOnly Start, DateOnly EndExclusive);

    internal static List<CalendarEvent> ParseEvents(string ics)
    {
        // Unfold continuation lines (RFC 5545: a line starting with space/tab continues the previous).
        var lines = Regex.Replace(ics.Replace("\r\n", "\n"), @"\n[ \t]", string.Empty).Split('\n');
        var events = new List<CalendarEvent>();
        Dictionary<string, (string Params, string Value)>? current = null;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd('\r');
            if (line == "BEGIN:VEVENT") { current = new(StringComparer.OrdinalIgnoreCase); continue; }
            if (line == "END:VEVENT" && current is not null)
            {
                if (current.TryGetValue("DTSTART", out var start) && Date(start.Value) is DateOnly from && current.TryGetValue("SUMMARY", out var summary))
                {
                    DateOnly to;
                    if (current.TryGetValue("DTEND", out var end) && Date(end.Value) is DateOnly endDate)
                        to = start.Params.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) || end.Value.Length == 8 ? endDate : endDate.AddDays(1);
                    else to = from.AddDays(1);
                    if (to <= from) to = from.AddDays(1);
                    events.Add(new CalendarEvent(Unescape(summary.Value), current.TryGetValue("DESCRIPTION", out var description) ? Unescape(description.Value) : null,
                        current.TryGetValue("CATEGORIES", out var categories) ? Unescape(categories.Value) : null, from, to));
                }
                current = null;
                continue;
            }
            if (current is null) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var head = line[..colon];
            var semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            current[name] = (semi < 0 ? string.Empty : head[(semi + 1)..], line[(colon + 1)..]);
        }
        return events;
    }

    private static DateOnly? Date(string value) =>
        value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static string Unescape(string value) => value.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\").Trim();

    private static readonly Regex NonWorking = new(@"\b(?:non[- ]?working|not\s+working|day\s+off|rest\s+day|off\s+day|weekend|working\s+pattern|unavailable)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Not absences: celebrations, and company-wide holidays that aren't a person.
    private static readonly Regex IgnoredType = new(@"\b(?:birthday|anniversary)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CompanyHoliday = new(@"^\s*(?:christmas\s+day|boxing\s+day|new\s+year'?s?\s+day|good\s+friday|easter\s+monday|.*bank\s+holiday.*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static Snapshot Build(List<CalendarEvent> events, IReadOnlyList<(Guid Id, string Name)> drivers, DateTimeOffset now,
        IReadOnlyList<(Guid Id, string Name)>? archived = null)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var from = today.AddDays(-14);
        var until = today.AddDays(90);
        var people = drivers.Select(driver => (driver.Id, driver.Name, Tokens: Tokens(driver.Name))).Where(item => item.Tokens.Count >= 2).ToList();
        var former = (archived ?? []).Select(driver => (driver.Id, driver.Name, Tokens: Tokens(driver.Name))).Where(item => item.Tokens.Count >= 2).ToList();
        var days = new List<DriverDay>();
        var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var archivedMatches = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matched = 0;
        var ignored = 0;
        foreach (var item in events.Where(item => item.EndExclusive > from && item.Start < until))
        {
            if (CompanyHoliday.IsMatch(item.Summary) || IgnoredType.IsMatch(item.Summary)) { ignored++; continue; }
            var summaryTokens = Tokens(item.Summary);
            var candidates = Candidates(people, summaryTokens);
            var type = CleanType(TypeOf(item, candidates.FirstOrDefault().person.Name), item.Summary);
            types[type] = types.GetValueOrDefault(type) + 1;
            if (candidates.Count == 0 || (candidates.Count > 1 && candidates[0].Score == candidates[1].Score))
            {
                var name = NamePart(CleanName(item.Summary));
                if (name.Length == 0) continue;
                // Someone archived (e.g. no tacho for 28 days) isn't a gap to fix.
                if (Candidates(former, summaryTokens).Count > 0) archivedMatches.Add(name);
                else unmatched.Add(name);
                continue;
            }
            matched++;
            var driver = candidates[0].person;
            var kind = NonWorking.IsMatch(type) || NonWorking.IsMatch(item.Description ?? string.Empty) ? "NonWorking" : "Leave";
            for (var day = item.Start < from ? from : item.Start; day < item.EndExclusive && day < until; day = day.AddDays(1))
                days.Add(new DriverDay(driver.Id, driver.Name, day, kind, type));
        }
        return new Snapshot(now, events.Count, matched, days, unmatched.Take(50).ToList(), types, null, archivedMatches.Take(50).ToList(), ignored);
    }

    private static List<((Guid Id, string Name, List<string> Tokens) person, int Score)> Candidates(
        List<(Guid Id, string Name, List<string> Tokens)> people, List<string> summaryTokens)
    {
        // First name and surname both in the title, either order; a one-letter slip in a
        // longer surname (Tullet / Tullett) still counts.
        bool Has(string token) => summaryTokens.Contains(token) ||
            (token.Length >= 5 && summaryTokens.Any(other => other.Length >= 5 && Math.Abs(other.Length - token.Length) <= 1 && Distance(other, token) <= 1));
        return people
            .Select(person => (person, Score: person.Tokens.Count(token => summaryTokens.Contains(token))))
            .Where(candidate => (Has(candidate.person.Tokens[0]) && Has(candidate.person.Tokens[^1])) || candidate.Score >= 3)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[b.Length];
    }

    /// <summary>"Annual Leave WP, Full day" → "Annual Leave WP"; half days marked; pending shown.</summary>
    internal static string CleanType(string type, string summary)
    {
        var half = Regex.IsMatch(type, @"first\s+part\s+of\s+the\s+day", RegexOptions.IgnoreCase) ? " (morning)"
            : Regex.IsMatch(type, @"second\s+part\s+of\s+the\s+day", RegexOptions.IgnoreCase) ? " (afternoon)" : string.Empty;
        var clean = Regex.Replace(type, @",?\s*(?:full\s+day|first\s+part\s+of\s+the\s+day|second\s+part\s+of\s+the\s+day)\s*,?", string.Empty, RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"\bPENDING\s+APPROVAL\b", string.Empty, RegexOptions.IgnoreCase);
        clean = Regex.Replace(clean, @"[\s,]+$", string.Empty).Trim(' ', ',', '-');
        if (clean.Length == 0) clean = "Leave";
        if (summary.Contains("PENDING APPROVAL", StringComparison.OrdinalIgnoreCase)) clean = $"{clean} (pending approval)";
        return clean + half;
    }

    private static string CleanName(string name) => Regex.Replace(name, @"\bPENDING\s+APPROVAL\b", string.Empty, RegexOptions.IgnoreCase).Trim(' ', ',', '-');

    private static string TypeOf(CalendarEvent item, string? driverName)
    {
        var summary = item.Summary;
        // "Ben Joshua Madge - Sick leave": the type is the side without the name (Sage may
        // carry middle names Driver Master doesn't have).
        var parts = Regex.Split(summary, @"\s+[-–:|]\s+").Where(part => !Regex.IsMatch(part, @"^\s*PENDING\s+APPROVAL\s*$", RegexOptions.IgnoreCase)).ToArray();
        if (parts.Length >= 2)
        {
            var nameTokens = driverName is null ? [] : Tokens(driverName);
            var typePart = nameTokens.Count == 0 ? parts[^1] : parts.FirstOrDefault(part => !Tokens(part).Any(nameTokens.Contains));
            if (!string.IsNullOrWhiteSpace(typePart)) return typePart.Trim().Length > 40 ? typePart.Trim()[..40] : typePart.Trim();
        }
        if (driverName is not null)
            foreach (var token in driverName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                summary = Regex.Replace(summary, $@"\b{Regex.Escape(token)}\b", string.Empty, RegexOptions.IgnoreCase);
        else summary = summary.Contains(" - ") ? summary[(summary.IndexOf(" - ", StringComparison.Ordinal) + 3)..] : summary;
        summary = Regex.Replace(summary, @"[\s\-–:|()\[\]]+", " ").Trim();
        if (summary.Length == 0) summary = item.Categories ?? "Off";
        return summary.Length > 40 ? summary[..40] : summary;
    }

    private static string NamePart(string summary)
    {
        var cut = summary.IndexOfAny(['-', '–', ':', '(']);
        var name = (cut > 0 ? summary[..cut] : summary).Trim();
        return name.Length > 60 ? name[..60] : name;
    }

    private static List<string> Tokens(string? value) =>
        Regex.Split(RemoveAccents(value ?? string.Empty).ToLowerInvariant(), @"[^a-z']+").Where(token => token.Length >= 2).ToList();

    private static string RemoveAccents(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        return new string(normalized.Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}

/// <summary>Reads the Sage HR calendar once an hour.</summary>
public sealed class SageCalendarSyncService(IServiceScopeFactory scopes, SageCalendarFeed feed, ILogger<SageCalendarSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartupGrace.WaitAsync(stoppingToken, TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TmsDbContext>();
                if (await SageCalendarFeed.ReadSettingAsync(db, stoppingToken) is not null)
                    await feed.SyncAsync(db, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sage HR calendar sync failed; it will try again in an hour.");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
